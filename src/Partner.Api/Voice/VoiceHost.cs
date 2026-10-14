using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Voice;
using Partner.Voice.Asr;
using Partner.Voice.Playback;
using Partner.Voice.Audio;
using Partner.Voice.Settings;
using Partner.Voice.Tts;

namespace Partner.Api.Voice;

/// <summary>
/// Maps the editable settings onto the option records the voice halves consume. Field names are identical on
/// both sides on purpose, so this reads as a copy rather than a translation — and a rename on one side fails
/// to compile here instead of silently dropping a setting.
/// </summary>
public static class VoiceSettingsMapping
{
    public static VoiceInputSetting ToInput(Settings.InputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new VoiceInputSetting
        {
            Enabled = settings.Enabled,
            DeviceId = settings.DeviceId,
            SilenceThreshold = settings.SilenceThreshold,
            UtteranceGapMs = settings.UtteranceGapMs,
            SentenceGapMs = settings.SentenceGapMs,
            WindowSeconds = settings.WindowSeconds,
            AsrStream = settings.AsrStream,
            MaxSplitChars = settings.MaxSplitChars,
        };
    }

    public static VoiceOutputSetting ToOutput(Settings.OutputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new VoiceOutputSetting
        {
            Enabled = settings.Enabled,
            DeviceId = settings.DeviceId,
            Voice = settings.Voice,
            Speed = settings.Speed,
            GapMs = settings.GapMs,
            DrainMs = settings.DrainMs,
            SampleRate = settings.SampleRate,
            MuteDuringTts = settings.MuteDuringTts,
        };
    }

    /// <summary>Null when no endpoint is configured: an absent endpoint means the feature is off, not defaulted.</summary>
    public static AsrOptions? ToAsr(Settings.AsrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return null;
        }

        return new AsrOptions
        {
            BaseUrl = settings.BaseUrl.TrimEnd('/'),
            Protocol = AsrProtocols.Normalize(settings.Protocol),
            Model = settings.Model,
            Language = settings.Language,
            ConnectTimeout = TimeSpan.FromMilliseconds(settings.ConnectTimeoutMs),
            ReadTimeout = TimeSpan.FromMilliseconds(settings.ReadTimeoutMs),
            SampleRate = settings.SampleRate,
        };
    }

    public static TtsOptions? ToTts(Settings.TtsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return null;
        }

        return new TtsOptions
        {
            BaseUrl = settings.BaseUrl.TrimEnd('/'),
            Protocol = TtsProtocols.Normalize(settings.Protocol),
            Model = settings.Model,
            Voice = settings.Voice,
            Speed = settings.Speed,
            Lang = settings.Lang,
            SampleRate = settings.SampleRate,
            SpeakerRefPath = settings.SpeakerRefPath,
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
            ChunkMs = settings.ChunkMs,
        };
    }
}

/// <summary>
/// Everything the voice host reaches outside itself: audio devices and the two service clients. One seam, so
/// the whole pipeline can be driven with synthetic audio and a scripted ASR/TTS — the only way to test pause
/// boundaries and playback without a microphone and a speaker.
/// </summary>
public interface IVoiceComponents
{
    IAudioSource OpenSource(VoiceInputSetting setting);

    IAudioSink OpenSink(VoiceOutputSetting setting);

    IAsrClient CreateAsr(AsrOptions options);

    ITtsClient CreateTts(TtsOptions options);

    /// <summary>Names of the capture devices, so a UI can offer them instead of asking the user to type one.</summary>
    IReadOnlyList<string> ListInputDevices();

    IReadOnlyList<string> ListOutputDevices();
}

/// <summary>The real components: WASAPI devices and HTTP clients.</summary>
public sealed class WasapiVoiceComponents : IVoiceComponents
{
    public IAudioSource OpenSource(VoiceInputSetting setting) => new WasapiAudioSource(setting);

    public IAudioSink OpenSink(VoiceOutputSetting setting) => new WasapiAudioSink(setting);

    public IAsrClient CreateAsr(AsrOptions options) =>
        AsrProtocols.Normalize(options.Protocol) == AsrProtocols.OpenAi
            ? new OpenAiAsrClient(options)
            : new HttpAsrClient(options);

    public ITtsClient CreateTts(TtsOptions options) =>
        TtsProtocols.Normalize(options.Protocol) == TtsProtocols.OpenAi
            ? new OpenAiTtsClient(options)
            : new HttpTtsClient(options);

    public IReadOnlyList<string> ListInputDevices() => WasapiAudioSource.ListDevices();

    public IReadOnlyList<string> ListOutputDevices() => WasapiAudioSink.ListDevices();
}

/// <summary>What one half is doing. The device and the reason are reported, never assumed.</summary>
public sealed record VoiceHalfState(bool Running, string Reason, string? Device, int Count);

public sealed record VoiceState(VoiceHalfState Input, VoiceHalfState Output)
{
    public static VoiceState Idle { get; } = new(
        new VoiceHalfState(false, "未启用", null, 0),
        new VoiceHalfState(false, "未启用", null, 0));
}

/// <summary>
/// Hosts the two voice halves (design doc §2.8) inside the API process.
/// <para>
/// Same process is not a shortcut: the input queue has a single in-process consumer and no cross-process
/// lock, so a separate voice process would both consume and produce against the same log.
/// </para>
/// <para>
/// Each half reports its own state. Opening a device can fail (no microphone, device in use), and that must
/// read as "voice input is off, because X" rather than as a silently dead feature.
/// </para>
/// </summary>
public sealed class VoiceHost : IAsyncDisposable
{
    /// <summary>
    /// Capture runs on the frame cadence the pipeline expects (the smoke drives it one 20 ms frame per tick);
    /// the output half only needs to notice new text, so it polls far less often.
    /// </summary>
    private static readonly TimeSpan InputTickInterval = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan OutputPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly ApiContext _context;
    private readonly IVoiceComponents _components;
    private readonly SemaphoreSlim _reconfigure = new(1, 1);
    private readonly PlaybackGate _playbackGate = new();

    private VoiceInputHalf? _input;
    private VoiceOutputHalf? _output;
    private IAudioSource? _source;
    private IAudioSink? _sink;
    private IAsrClient? _asr;
    private ITtsClient? _tts;
    private CancellationTokenSource? _stop;
    private Task? _inputLoop;
    private Task? _outputLoop;
    private VoiceHalfState _inputState = VoiceState.Idle.Input;
    private VoiceHalfState _outputState = VoiceState.Idle.Output;

    private VoiceHost(ApiContext context, IVoiceComponents components)
    {
        _context = context;
        _components = components;
    }

    public VoiceState State => new(_inputState, _outputState);

    /// <summary>Device names as the host's components see them; empty when enumeration is unavailable.</summary>
    public IReadOnlyList<string> InputDevices => _components.ListInputDevices();

    public IReadOnlyList<string> OutputDevices => _components.ListOutputDevices();

    public static VoiceHost Create(ApiContext context, IVoiceComponents components)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(components);

        return new VoiceHost(context, components);
    }

    /// <summary>Applies settings: stops whatever is running, then starts whichever halves are usable.</summary>
    public async Task<VoiceState> ReconfigureAsync(Settings.RuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _reconfigure.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            Start(settings);
            return State;
        }
        finally
        {
            _reconfigure.Release();
        }
    }

    private void Start(Settings.RuntimeSettings settings)
    {
        _stop = new CancellationTokenSource();

        StartInput(settings);
        StartOutput(settings);
    }

    private void StartInput(Settings.RuntimeSettings settings)
    {
        var inputSetting = VoiceSettingsMapping.ToInput(settings.Input);
        var asrOptions = VoiceSettingsMapping.ToAsr(settings.Asr);

        if (!inputSetting.Enabled)
        {
            _inputState = new VoiceHalfState(false, "未启用（设置里关闭了麦克风输入）", null, 0);
            return;
        }

        if (asrOptions is null)
        {
            _inputState = new VoiceHalfState(false, "未配置 ASR 服务地址", null, 0);
            return;
        }

        try
        {
            _source = _components.OpenSource(inputSetting);
        }
        catch (Exception error)
        {
            _inputState = new VoiceHalfState(false, $"打开输入设备失败：{error.Message}", null, 0);
            return;
        }

        _asr = _components.CreateAsr(asrOptions);

        _input = new VoiceInputHalf(
            inputSetting,
            _source,
            _asr,
            _playbackGate,
            async utterance => await EnqueueAsync(utterance).ConfigureAwait(false));

        _input.Start();
        _inputLoop = RunInputAsync(_stop!.Token);
        _inputState = new VoiceHalfState(true, "运行中", _source.DeviceName, 0);
    }

    private void StartOutput(Settings.RuntimeSettings settings)
    {
        var outputSetting = VoiceSettingsMapping.ToOutput(settings.Output);
        var ttsOptions = VoiceSettingsMapping.ToTts(settings.Tts);

        if (!outputSetting.Enabled)
        {
            _outputState = new VoiceHalfState(false, "未启用（设置里关闭了语音输出）", null, 0);
            return;
        }

        if (ttsOptions is null)
        {
            _outputState = new VoiceHalfState(false, "未配置 TTS 服务地址", null, 0);
            return;
        }

        try
        {
            _sink = _components.OpenSink(outputSetting);
        }
        catch (Exception error)
        {
            _outputState = new VoiceHalfState(false, $"打开输出设备失败：{error.Message}", null, 0);
            return;
        }

        _tts = _components.CreateTts(ttsOptions);
        _sink.Start();

        _output = new VoiceOutputHalf(outputSetting, _sink, _tts, _playbackGate);
        _outputLoop = RunOutputAsync(_stop!.Token);
        _outputState = new VoiceHalfState(true, "运行中", _sink.DeviceName, 0);
    }

    /// <summary>Drives the capture pipeline and keeps the reported utterance count current.</summary>
    private async Task RunInputAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _input!.TickAsync(ct).ConfigureAwait(false);
                _inputState = _inputState with { Count = _input.Utterances.Count };
                await Task.Delay(InputTickInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                // A failed ASR block is retried by the half; the loop must not die on it.
                _inputState = _inputState with { Reason = $"运行中（最近一次识别出错：{error.Message}）" };
                await Task.Delay(InputTickInterval, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>An utterance becomes a queue item with the half's boot-scoped key, so a retry cannot duplicate it.</summary>
    private async Task EnqueueAsync(VoiceUtterance utterance) =>
        await _context.Queue.EnqueueAsync(new InputItem
        {
            ItemId = "voice-" + utterance.UtteranceId,
            Source = InputSource.Voice,
            Payload = utterance.Transcript,
            IdempotencyKey = utterance.IdempotencyKey,
        }).ConfigureAwait(false);

    /// <summary>
    /// Speaks assistant text as it appears in the session. Only events written after this host started are
    /// spoken: replaying the whole log on startup would read the entire history out loud.
    /// </summary>
    private async Task RunOutputAsync(CancellationToken ct)
    {
        var from = (await _context.Session.ProjectAsync(ct).ConfigureAwait(false)).LastSeq;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var e in _context.Session.ReadAsync(from + 1, 100, ct).ConfigureAwait(false))
                {
                    from = e.Seq;

                    if (e.Kind != SessionEventKind.Assistant || string.IsNullOrWhiteSpace(e.Body))
                    {
                        continue;
                    }

                    _output!.OnText(e.Body, e.Seq);
                    await _output.FlushTurnAsync(ct).ConfigureAwait(false);
                    await _output.PlayPendingAsync(ct).ConfigureAwait(false);
                    _outputState = _outputState with { Count = _output.Items.Count };
                }

                // Playback closes the gate; only the conditions being clear may reopen it.
                _output!.TryReleaseGate(
                    pendingBlock: false,
                    asrBusy: _input?.AsrBusy ?? false,
                    nowSeconds: Environment.TickCount64 / 1000.0);

                await Task.Delay(OutputPollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                // A failed synthesis is reported by the half itself; the loop must keep the other half alive.
                _outputState = _outputState with { Reason = $"播放出错：{error.Message}" };
                await Task.Delay(OutputPollInterval, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task StopAsync()
    {
        if (_stop is null)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);

        foreach (var loop in new[] { _inputLoop, _outputLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop was waiting.
            }
        }

        _input?.Dispose();
        _output?.Dispose();
        _source?.Dispose();
        _sink?.Dispose();
        _asr?.Dispose();
        _tts?.Dispose();
        _stop.Dispose();

        _stop = null;
        _inputLoop = null;
        _outputLoop = null;
        _input = null;
        _output = null;
        _source = null;
        _sink = null;
        _asr = null;
        _tts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _reconfigure.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _reconfigure.Release();
            _reconfigure.Dispose();
        }
    }
}
