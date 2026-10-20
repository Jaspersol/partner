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
    public static VoiceInputSetting ToInput(Settings.InputSettings settings, Settings.LocalEngineSettings? wakeEngine = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var setting = new VoiceInputSetting
        {
            Enabled = settings.Enabled,
            DeviceId = settings.DeviceId,
            SilenceThreshold = settings.SilenceThreshold,
            WakeTone = settings.WakeTone,
            BargeIn = settings.BargeIn,
            UtteranceGapMs = settings.UtteranceGapMs,
            WakeTimeoutMs = settings.WakeTimeoutMs,
            SentenceGapMs = settings.SentenceGapMs,
            WindowSeconds = settings.WindowSeconds,
            AsrStream = settings.AsrStream,
            MaxSplitChars = settings.MaxSplitChars,
        };

        // 唤醒门只在本地引擎托管开启时生效：宿主自己不跑 KWS 模型，它是个服务。
        // 地址跟 asr 段（同一引擎的伴生接口），端口由引擎托管那边配。
        if (wakeEngine is { Enabled: true } && wakeEngine.Port > 0 && wakeEngine.Port <= 65535)
        {
            setting = setting with
            {
                Wake = new LocalWakeClient($"http://127.0.0.1:{wakeEngine.Port}"),
            };
        }

        return setting;
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

    public IAsrClient CreateAsr(AsrOptions options) => CreateAsrClient(options);

    public ITtsClient CreateTts(TtsOptions options) => CreateTtsClient(options);

    public IReadOnlyList<string> ListInputDevices() => WasapiAudioSource.ListDevices();

    public IReadOnlyList<string> ListOutputDevices() => WasapiAudioSink.ListDevices();

    /// <summary>Protocol → client. Shared so the settings test endpoint probes the same wiring as the host.</summary>
    public static IAsrClient CreateAsrClient(AsrOptions options) =>
        AsrProtocols.Normalize(options.Protocol) switch
        {
            AsrProtocols.OpenAi => new OpenAiAsrClient(options),
            AsrProtocols.SenseVoice => new LocalAsrClient(options),
            _ => new HttpAsrClient(options),
        };

    public static ITtsClient CreateTtsClient(TtsOptions options) =>
        TtsProtocols.Normalize(options.Protocol) == TtsProtocols.OpenAi
            ? new OpenAiTtsClient(options)
            : new HttpTtsClient(options);
}

/// <summary>What one half is doing. The device and the reason are reported, never assumed.</summary>
public sealed record VoiceHalfState(bool Running, string Reason, string? Device, int Count);

/// <summary>
/// The input half adds what only a microphone can answer: how loud it is right now. A dead device, a quiet
/// room and speech below the gate's threshold used to look identical in the UI — the level, the floor it was
/// measured against, and whether the gate considers it noise are what tell them apart.
/// </summary>
public sealed record VoiceInputState(
    bool Running,
    string Reason,
    string? Device,
    int Count,
    double Rms = 0,
    double AmbientRms = 0,
    bool ThresholdMisconfigured = false,
    bool Awake = false);

public sealed record VoiceState(VoiceInputState Input, VoiceHalfState Output)
{
    public static VoiceState Idle { get; } = new(
        new VoiceInputState(false, "未启用", null, 0),
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

    /// <summary>While a reply is being written the live text is read much more often: it sets how soon the first word is heard.</summary>
    private static readonly TimeSpan StreamingPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly ApiContext _context;
    private readonly IVoiceComponents _components;

    /// <summary>The reply the model is writing right now; null when the host has no stream to listen to.</summary>
    private readonly Func<LiveAssistant?>? _live;
    private readonly LiveSpeech _liveSpeech = new();
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
    private VoiceInputState _inputState = VoiceState.Idle.Input;
    private VoiceHalfState _outputState = VoiceState.Idle.Output;

    /// <summary>输出设置里配的播放采样率。唤醒提示音按它合成，与 TTS 同档才不会被 sink 分到另一个流。</summary>
    private int _toneSampleRate = 24_000;

    private VoiceHost(ApiContext context, IVoiceComponents components, Func<LiveAssistant?>? live)
    {
        _context = context;
        _components = components;
        _live = live;
    }

    public VoiceState State => new(_inputState, _outputState);

    /// <summary>Device names as the host's components see them; empty when enumeration is unavailable.</summary>
    public IReadOnlyList<string> InputDevices => _components.ListInputDevices();

    public IReadOnlyList<string> OutputDevices => _components.ListOutputDevices();

    public static VoiceHost Create(ApiContext context, IVoiceComponents components, Func<LiveAssistant?>? live = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(components);

        return new VoiceHost(context, components, live);
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

        // 输出先起：输入半区的唤醒提示音要往 sink 里灌音，输入建在输出之前就永远拿不到 sink
        // （_sink 还是 null），提示音静默失效。反过来没有依赖，所以这个顺序不许换。
        StartOutput(settings);
        StartInput(settings);
    }

    private void StartInput(Settings.RuntimeSettings settings)
    {
        var inputSetting = VoiceSettingsMapping.ToInput(settings.Input, settings.Local.Kws);
        var asrOptions = VoiceSettingsMapping.ToAsr(settings.Asr);

        if (!inputSetting.Enabled)
        {
            _inputState = new VoiceInputState(false, "未启用（设置里关闭了麦克风输入）", null, 0);
            return;
        }

        if (asrOptions is null)
        {
            _inputState = new VoiceInputState(false, "未配置 ASR 服务地址", null, 0);
            return;
        }

        try
        {
            _source = _components.OpenSource(inputSetting);
        }
        catch (Exception error)
        {
            _inputState = new VoiceInputState(false, $"打开输入设备失败：{error.Message}", null, 0);
            return;
        }

        _asr = _components.CreateAsr(asrOptions);

        // 唤醒提示音：在输出设备上短促一声。输出未启用（无 sink）时自然没有提示——输入开关照常工作。
        _input = new VoiceInputHalf(
            inputSetting,
            _source,
            _asr,
            _playbackGate,
            async utterance => await EnqueueAsync(utterance).ConfigureAwait(false),
            log: VoiceLog,
            wakeTone: _sink is null ? null : PlayWakeTone,
            onBargeIn: () => _output?.StopPlayback());

        _input.Start();
        _inputLoop = RunInputAsync(_stop!.Token);
        _inputState = new VoiceInputState(true, "运行中", _source.DeviceName, 0);
    }

    /// <summary>
    /// The input half's log line, on stderr like everything else the API reports about itself. Without this
    /// wiring the half's <c>_log</c> falls back to a no-op delegate, so "wake word hit", "probe failed" and
    /// "threshold is not above ambient" were all unreachable — which is what made a wake failure read as
    /// "everything looks fine, it just does not respond".
    /// </summary>
    private static void VoiceLog(string message) => Console.Error.WriteLine(message);

    /// <summary>
    /// 唤醒提示音：短促两音（880→1320 Hz，各 80 ms，能量 0.18）——高音在后的双音比单音更像「叮咚」，
    /// 也更容易和 TTS 的开口区分开。
    /// <para>
    /// 采样率必须跟 TTS 输出同一档：<see cref="WasapiAudioSink"/> 是「一种速率一个
    /// <c>WaveOutEvent</c>」，频率不同就另外开一个流，而旧的那个还在播——同一句就会
    /// 「前半段放到一半，第二段从头重叠」（用户报的双声 bug）。sink 在被首次写入前并不知道
    /// 自己会开成什么速率，所以这里用输出设置里配好的那个值，不用 sink 的当前状态。
    /// </para>
    /// </summary>
    private void PlayWakeTone()
    {
        var sink = _sink;
        if (sink is null)
        {
            return;
        }

        const double toneSeconds = 0.08, firstFreq = 880, secondFreq = 1320, amplitude = 0.18;
        var rate = _toneSampleRate;
        var buffer = new float[(int)(toneSeconds * rate * 2)];
        for (var i = 0; i < buffer.Length; i++)
        {
            var freq = i < buffer.Length / 2 ? firstFreq : secondFreq;
            var phase = i % (int)(buffer.Length / 2);
            buffer[i] = (float)(amplitude * Math.Sin(2 * Math.PI * freq * phase / rate));
        }

        sink.Enqueue(buffer, rate);
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

        // 提示音的采样率跟着这里定：之后任何一路（TTS 块、提示音）都得用这一档，
        // 否则 sink 会为不同速率各开一个设备流，正在播的那段会被从头再放一遍。
        _toneSampleRate = outputSetting.SampleRate;

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
                _inputState = _inputState with
                {
                    Count = _input.Utterances.Count,
                    Rms = _input.RecentRms,
                    AmbientRms = _input.AmbientRms,
                    ThresholdMisconfigured = _input.ThresholdMisconfigured,
                    Awake = _input.Awake,
                };
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
    private async Task EnqueueAsync(VoiceUtterance utterance)
    {
        // Speaking is sending a new message, and a new message is what makes a pending withdrawal final.
        await _context.Withdrawals.CommitAsync().ConfigureAwait(false);

        // Speaking again after talking over the assistant: what it says next answers this, so it is spoken.
        _output?.ResumeAfterInterruption();

        await _context.Queue.EnqueueAsync(new InputItem
        {
            ItemId = "voice-" + utterance.UtteranceId,
            Source = InputSource.Voice,
            Payload = utterance.Transcript,
            IdempotencyKey = utterance.IdempotencyKey,
        }).ConfigureAwait(false);
    }

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

                    if (e.Kind != SessionEventKind.Assistant)
                    {
                        continue;
                    }

                    // What the live text already put in the speaker's mouth is not said again.
                    var rest = _liveSpeech.TakeRemainder(e.Body ?? string.Empty);

                    if (string.IsNullOrWhiteSpace(rest) && !_output!.IsStreaming && _output.Segmenter.IsEmpty)
                    {
                        continue;
                    }

                    _output!.OnText(rest, e.Seq);
                    await _output.FlushTurnAsync(ct).ConfigureAwait(false);
                    await _output.PlayPendingAsync(ct).ConfigureAwait(false);
                    _outputState = _outputState with { Count = _output.Items.Count };
                }

                await SpeakLiveAsync(from, ct).ConfigureAwait(false);
                _output!.ReleaseStreamIfDue();

                // Playback closes the gate; only the conditions being clear may reopen it.
                _output!.TryReleaseGate(
                    pendingBlock: false,
                    asrBusy: _input?.AsrBusy ?? false,
                    nowSeconds: Environment.TickCount64 / 1000.0);

                await Task.Delay(_output.IsStreaming || _liveSpeech.InProgress ? StreamingPollInterval : OutputPollInterval, ct)
                    .ConfigureAwait(false);
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

    /// <summary>
    /// Speaks the sentences of the reply the model is still writing, so the first one is heard long before the
    /// reply is done. The log event for the same text arrives later and only adds the unspoken rest.
    /// </summary>
    private async Task SpeakLiveAsync(long lastSeq, CancellationToken ct)
    {
        if (_live?.Invoke() is not { Text.Length: > 0 } live)
        {
            return;
        }

        var chunk = _liveSpeech.NextChunk(live.ItemId, live.Text);
        if (chunk is null)
        {
            return;
        }

        _output!.BeginStream();
        _output.OnText(chunk, lastSeq);
        await _output.PlayPendingAsync(ct).ConfigureAwait(false);
        _outputState = _outputState with { Count = _output.Items.Count };
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
