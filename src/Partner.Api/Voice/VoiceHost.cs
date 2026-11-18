using System.Collections.Concurrent;
using Partner.Core.Agent.Tools;
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
/// Maps editable settings onto the option records the voice halves consume. Field names are identical on both
/// sides on purpose, so this reads as a copy rather than a translation — and a rename on one side fails to
/// compile here instead of silently dropping a setting.
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
        };

        // The wake gate only applies when the local engine host is enabled: the host does not run the KWS model,
        // it is a service. The address follows the asr section (a companion endpoint of the same engine); the port
        // is configured on the engine-host side.
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
            MaxSplitChars = settings.MaxSplitChars,
            MinSentenceChars = settings.MinSentenceChars,
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
            SpeakerRefText = settings.SpeakerRefText,
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
            ChunkMs = settings.ChunkMs,
        };
    }
}

/// <summary>
/// Everything the voice host reaches outside itself: audio devices and the two service clients. One seam, so the
/// whole pipeline can be driven with synthetic audio and a scripted ASR/TTS — the only way to test pause
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
        TtsProtocols.Normalize(options.Protocol) == TtsProtocols.Native
            ? new HttpTtsClient(options)
            : new OpenAiTtsClient(options);
}

/// <summary>What one half is doing. The device and the reason are reported, never assumed.</summary>
public sealed record VoiceHalfState(bool Running, string Reason, string? Device, int Count);

/// <summary>
/// The input half adds what only a microphone can answer: how loud it is right now. A dead device, a quiet room
/// and speech below the gate's threshold would otherwise look identical in the UI — the level, the floor it was
/// measured against, and whether the gate considers it noise tell them apart.
/// </summary>
public sealed record VoiceInputState(
    bool Running,
    string Reason,
    string? Device,
    int Count,
    double Rms = 0,
    double AmbientRms = 0,
    bool ThresholdMisconfigured = false,
    bool Awake = false,
    bool AwaitingApproval = false);

public sealed record VoiceState(VoiceInputState Input, VoiceHalfState Output)
{
    public static VoiceState Idle { get; } = new(
        new VoiceInputState(false, "未启用", null, 0),
        new VoiceHalfState(false, "未启用", null, 0));
}

/// <summary>
/// Hosts the two voice halves inside the API process.
/// <para>
/// Same process is not a shortcut: the input queue has a single in-process consumer and no cross-process lock, so
/// a separate voice process would both consume and produce against the same log.
/// </para>
/// <para>
/// Each half reports its own state. Opening a device can fail (no microphone, device in use), and that must read
/// as "voice input is off, because X" rather than as a silently dead feature.
/// </para>
/// </summary>
public sealed class VoiceHost : IAsyncDisposable
{
    /// <summary>
    /// Capture runs on the frame cadence the pipeline expects; the output half only needs to notice new text, so
    /// it polls far less often.
    /// </summary>
    private static readonly TimeSpan InputTickInterval = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan OutputPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>While a reply is being written the live text is read much more often: it sets how soon the first word is heard.</summary>
    private static readonly TimeSpan StreamingPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>How soon a new pending approval is noticed. The request itself lives for 120 seconds.</summary>
    private static readonly TimeSpan ApprovalPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly ApiContext _context;
    private readonly IVoiceComponents _components;

    /// <summary>The reply the model is writing right now; null when the host has no stream to listen to.</summary>
    private readonly Func<LiveAssistant?>? _live;
    private readonly LiveSpeech _liveSpeech = new();
    private readonly SemaphoreSlim _reconfigure = new(1, 1);
    private readonly object _outputLifecycle = new();
    private readonly PlaybackGate _playbackGate = new();

    // Approval state crosses three loops without sharing mutable objects: the watcher and the input loop only
    // enqueue cues, and the output loop (the only one that touches VoiceOutputHalf) drains them.
    private readonly ConcurrentQueue<ApprovalCue> _approvalCues = new();
    private readonly ConcurrentDictionary<string, PendingApproval> _announced = new(StringComparer.Ordinal);

    /// <summary>The ring time at which the speaker last went quiet; a block that ended at or before it is not
    /// allowed to answer an approval, because it may be the prompt's own voice coming back through the mic.</summary>
    private double _quietAtSeconds;
    private bool _wasSpeaking;

    private VoiceInputHalf? _input;
    private VoiceOutputHalf? _output;
    private IAudioSource? _source;
    private IAudioSink? _sink;
    private IAsrClient? _asr;
    private ITtsClient? _tts;
    private CancellationTokenSource? _inputStop;
    private CancellationTokenSource? _outputStop;
    private Task? _inputLoop;
    private Task? _outputLoop;
    private Task? _approvalLoop;
    private VoiceInputState _inputState = VoiceState.Idle.Input;
    private VoiceHalfState _outputState = VoiceState.Idle.Output;
    private LocalEngineHost? _localEngines;
    private Settings.RuntimeSettings? _appliedSettings;
    private bool _inputBlocked;
    private bool _outputBlocked;
    private bool _disposed;
    private int _outputFaulted;

    /// <summary>The playback sample rate configured in output settings. The wake tone is synthesized at this rate
    /// so the sink does not split it onto another stream.</summary>
    private int _toneSampleRate = 24_000;

    private VoiceHost(ApiContext context, IVoiceComponents components, Func<LiveAssistant?>? live)
    {
        _context = context;
        _components = components;
        _live = live;
    }

    public VoiceState State
    {
        get
        {
            var input = _inputState;
            var output = _outputState;
            var settings = _appliedSettings;
            if (settings is not null && _localEngines is not null)
            {
                var engines = _localEngines.StateFor(settings.Local);
                if (settings.Input.Enabled && settings.Local.Kws.Enabled && !engines.Kws.Running)
                    input = new VoiceInputState(false, $"本地 KWS/ASR：{engines.Kws.Reason}", null, 0);
                if (settings.Output.Enabled && settings.Local.Tts.Enabled && UsesEngine(settings.Tts.BaseUrl, settings.Local.Tts.Port) && !engines.Tts.Running)
                    output = new VoiceHalfState(false, $"本地 TTS：{engines.Tts.Reason}", null, 0);
            }
            return new VoiceState(input, output);
        }
    }

    internal void TrackLocalEngines(LocalEngineHost engines) => _localEngines = engines;

    /// <summary>Whether saving settings should retry a failed playback device.</summary>
    public bool OutputFaulted => Volatile.Read(ref _outputFaulted) != 0;

    /// <summary>
    /// Whether the speaker is currently producing sound, counting everything already queued. The pet window polls
    /// this to hold its idle animation: a model that starts waving while the pet is mid-sentence looks broken.
    /// </summary>
    public bool IsSpeaking
    {
        get
        {
            VoiceOutputHalf? output;
            lock (_outputLifecycle) output = _output;
            if (output is null || Volatile.Read(ref _outputFaulted) != 0) return false;
            try { return output.IsPlaying || output.PendingAudio || output.TtsBusy || output.IsStreaming; }
            catch (Exception error) { FailOutput(output, error); return false; }
        }
    }

    /// <summary>Device names as the host's components see them; empty when enumeration is unavailable.</summary>
    public IReadOnlyList<string> InputDevices => _components.ListInputDevices();

    public IReadOnlyList<string> OutputDevices => _components.ListOutputDevices();

    public static VoiceHost Create(ApiContext context, IVoiceComponents components, Func<LiveAssistant?>? live = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(components);

        return new VoiceHost(context, components, live);
    }

    /// <summary>Applies settings and readiness, restarting only the voice halves whose dependencies changed.</summary>
    public async Task<VoiceState> ReconfigureAsync(Settings.RuntimeSettings settings, LocalEnginesState? engines = null, bool retryFaulted = true)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _reconfigure.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = _appliedSettings;
            var outputBlocked = settings.Local.Tts.Enabled && UsesEngine(settings.Tts.BaseUrl, settings.Local.Tts.Port)
                && engines is not null && !engines.Tts.Running;
            var inputBlocked = settings.Local.Kws.Enabled && engines is not null && !engines.Kws.Running;
            var changeOutput = previous is null || previous.Output != settings.Output || previous.Tts != settings.Tts
                || previous.Local.Tts != settings.Local.Tts || _outputBlocked != outputBlocked || (retryFaulted && OutputFaulted);
            var changeInput = previous is null || previous.Input != settings.Input || previous.Asr != settings.Asr
                || previous.Local.Kws != settings.Local.Kws || _inputBlocked != inputBlocked;
            if (changeOutput) await StopOutputAsync().ConfigureAwait(false);
            if (changeInput) await StopInputAsync().ConfigureAwait(false);
            _appliedSettings = settings;
            _outputBlocked = outputBlocked;
            _inputBlocked = inputBlocked;
            if (changeOutput)
            {
                _outputStop = new CancellationTokenSource();
                Volatile.Write(ref _outputFaulted, 0);
                lock (_outputLifecycle)
                {
                    if (outputBlocked && settings.Output.Enabled)
                        _outputState = new VoiceHalfState(false, $"本地 TTS：{engines!.Tts.Reason}", null, 0);
                    else StartOutput(settings);
                    StartApproval();
                }
            }
            if (changeInput)
            {
                _inputStop = new CancellationTokenSource();
                if (inputBlocked && settings.Input.Enabled)
                    _inputState = new VoiceInputState(false, $"本地 KWS/ASR：{engines!.Kws.Reason}", null, 0);
                else StartInput(settings);
            }
            return State;
        }
        finally
        {
            _reconfigure.Release();
        }
    }


    private static bool UsesEngine(string? address, int port) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.IsLoopback && uri.Port == port;

    /// <summary>
    /// Watches the approval queue and queues a spoken prompt for every new request. It runs only when voice
    /// output does: without a speaker there is no prompt to answer, so intercepting "批准执行" would approve
    /// something the person was never asked about aloud.
    /// </summary>
    private void StartApproval()
    {
        _announced.Clear();
        _approvalCues.Clear();
        Volatile.Write(ref _quietAtSeconds, 0);
        _wasSpeaking = false;

        if (_output is null)
        {
            return;
        }

        _approvalLoop = RunApprovalAsync(_outputStop!.Token);
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

        // The wake tone plays briefly on the output device. With output disabled (no sink) there is no tone;
        // input toggling still works.
        _input = new VoiceInputHalf(
            inputSetting,
            _source,
            _asr,
            _playbackGate,
            async utterance => await EnqueueAsync(utterance).ConfigureAwait(false),
            log: VoiceLog,
            wakeTone: PlayWakeTone,
            onBargeIn: InterruptOutput);

        try
        {
            _input.Start();
        }
        catch (Exception error)
        {
            // The half owns the source and the client, so disposing it releases both; disposing them here as
            // well would be a second release of the same device.
            _input.Dispose();
            _input = null;
            _source = null;
            _asr = null;
            _inputState = new VoiceInputState(false, $"启动输入设备失败：{error.Message}", null, 0);
            return;
        }

        _inputLoop = RunInputAsync(_inputStop!.Token);
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
    /// The wake tone: two short notes (880 then 1320 Hz, 80 ms each, amplitude 0.18). A rising pair reads as a
    /// doorbell and is easier to tell apart from the start of TTS.
    /// <para>
    /// The sample rate must match TTS output: <see cref="WasapiAudioSink"/> opens one <c>WaveOutEvent</c> per
    /// rate, and a second rate starts another stream while the first is still playing, so the same sentence
    /// repeats from the start. The sink does not know its rate before the first write, so use the configured
    /// output rate rather than the sink's current state.
    /// </para>
    /// </summary>
    private void PlayWakeTone()
    {
        if (Volatile.Read(ref _outputFaulted) != 0) return;
        IAudioSink? sink;
        VoiceOutputHalf? output;
        lock (_outputLifecycle) { sink = _sink; output = _output; }
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

        try { sink.Start(); sink.Enqueue(buffer, rate); }
        catch (Exception error) { if (output is not null) FailOutput(output, error); }
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
            Volatile.Write(ref _outputFaulted, 1);
            _outputState = new VoiceHalfState(false, $"打开输出设备失败：{error.Message}", null, 0);
            return;
        }

        _tts = _components.CreateTts(ttsOptions);
        try
        {
            _sink.Start();
        }
        catch (Exception error)
        {
            Volatile.Write(ref _outputFaulted, 1);
            _sink.Dispose();
            _sink = null;
            _tts.Dispose();
            _tts = null;
            _outputState = new VoiceHalfState(false, $"启动输出设备失败：{error.Message}", null, 0);
            return;
        }

        // The tone sample rate is fixed here: every later path (TTS chunks, tone) must use this rate, or the
        // sink opens one device stream per rate and replays what is currently playing.
        _toneSampleRate = outputSetting.SampleRate;

        _output = new VoiceOutputHalf(outputSetting, _sink, _tts, _playbackGate);
        _outputState = new VoiceHalfState(true, "运行中", _sink.DeviceName, 0);
        _outputLoop = RunOutputAsync(_outputStop!.Token);
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
                    AwaitingApproval = _input.ApprovalListening,
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
        // An answer to an approval is not a new message: it is consumed here, before the withdrawal is
        // committed and before anything reaches the model. The next assistant text is a new reply, so the
        // interruption state is cleared on both paths.
        if (TryAnswerApproval(utterance))
        {
            _output?.ResumeAfterInterruption();
            return;
        }

        // In wake mode, approval listening bypasses the wake word for the answer only. Everything else spoken
        // while it was on was captured without a wake word, so it must not reach the message queue — that is
        // exactly the rule the wake gate normally enforces. Without a wake word configured there is no such
        // rule, and ordinary speech keeps behaving as a message.
        if (utterance.ApprovalListening && utterance.WakeWord is null && _input is { WakeEnabled: true })
        {
            VoiceLog("voice: ignored non-command speech captured while waiting for approval (no wake word)");
            return;
        }

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
        var output = _output!;
        var from = (await _context.Session.ProjectAsync(ct).ConfigureAwait(false)).LastSeq;

        while (!ct.IsCancellationRequested && Volatile.Read(ref _outputFaulted) == 0)
        {
            try
            {
                DrainApprovalCues();

                // Approval speech is not tied to a session event, so nothing else in this loop would synthesize
                // it. Playing here also means a prompt queued while the loop was idle is heard within one poll.
                await _output!.PlayPendingAsync(ct, DrainApprovalCues).ConfigureAwait(false);
                _outputState = _outputState with { Count = _output.TotalCount };

                await foreach (var e in _context.Session.ReadAsync(from + 1, 100, ct).ConfigureAwait(false))
                {
                    from = e.Seq;

                    if (e.Kind != SessionEventKind.Assistant)
                    {
                        continue;
                    }

                    // What the live text already put in the speaker's mouth is not said again.
                    var rest = _liveSpeech.TakeRemainder(e.Body ?? string.Empty);

                    if (string.IsNullOrWhiteSpace(rest) && !_output!.IsStreaming && _output.SentencesEmpty)
                    {
                        continue;
                    }

                    _output!.OnText(rest, e.Seq);

                    // The answer's confirmation or a newly arrived prompt goes in before this reply is
                    // synthesized, so the reply cannot start speaking over the decision it just caused.
                    DrainApprovalCues();
                    await _output.FlushTurnAsync(ct, DrainApprovalCues).ConfigureAwait(false);
                    _outputState = _outputState with { Count = _output.TotalCount };
                }

                await SpeakLiveAsync(from, ct).ConfigureAwait(false);
                DrainApprovalCues();
                _output!.ReleaseStreamIfDue();

                // Playback closes the gate; only the conditions being clear may reopen it.
                _output!.TryReleaseGate(
                    pendingBlock: _input?.PendingBlock ?? false,
                    asrBusy: _input?.AsrBusy ?? false,
                    nowSeconds: Environment.TickCount64 / 1000.0);

                TrackQuiet();
                UpdateApprovalListening();

                await Task.Delay(_output.IsStreaming || _liveSpeech.InProgress ? StreamingPollInterval : OutputPollInterval, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                FailOutput(output, error);
                return;
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
        await _output.PlayPendingAsync(ct, DrainApprovalCues).ConfigureAwait(false);
        _outputState = _outputState with { Count = _output.TotalCount };
    }

    /// <summary>
    /// Notices new approval requests and queues a prompt for each. Reading the queue is deliberately a poll:
    /// approvals are owned by the API context, not by the voice host, and a subscription would have to be torn
    /// down and rebuilt on every settings change.
    /// </summary>
    private async Task RunApprovalAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pending = _context.Approvals.Pending;
                var live = new HashSet<string>(pending.Select(request => request.ApprovalId), StringComparer.Ordinal);

                foreach (var id in _announced.Keys)
                {
                    if (live.Contains(id))
                    {
                        continue;
                    }

                    if (_announced.TryRemove(id, out _))
                    {
                        _approvalCues.Enqueue(new DropCue(id));
                    }
                }

                var now = _context.Clock.GetUtcNow();
                foreach (var request in pending)
                {
                    if (request.ExpiresAt <= now)
                    {
                        continue;
                    }

                    if (_announced.TryAdd(request.ApprovalId, request))
                    {
                        _approvalCues.Enqueue(new AnnounceCue(request));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                VoiceLog($"voice: approval watch failed: {error.Message}");
            }

            await Task.Delay(ApprovalPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Turns queued approval cues into speech. Only the output loop calls this: <see cref="VoiceOutputHalf"/>
    /// stays single-threaded, and a prompt that arrives while a sentence is being synthesized waits for the
    /// next sentence boundary instead of touching the item list from another thread.
    /// </summary>
    private void DrainApprovalCues()
    {
        var output = _output;
        if (output is null)
        {
            return;
        }

        while (_approvalCues.TryDequeue(out var cue))
        {
            switch (cue)
            {
                case AnnounceCue announce:
                    if (!_announced.ContainsKey(announce.Request.ApprovalId)
                        || announce.Request.ExpiresAt <= _context.Clock.GetUtcNow()
                        || !IsPending(announce.Request.ApprovalId))
                    {
                        break;
                    }

                    output.Say(
                        ApprovalSpeech.Prompt(announce.Request),
                        SpeechKind.ApprovalPrompt,
                        announce.Request.ApprovalId,
                        priority: true,
                        forceGate: true);
                    SpeakingStarted();
                    VoiceLog($"voice: approval prompt queued ({announce.Request.ApprovalId}, {announce.Request.ToolName})");
                    break;

                case DropCue drop:
                    output.DropTag(drop.ApprovalId, "请求已处理");
                    break;

                case ResultCue result:
                    output.Say(result.Text, SpeechKind.ApprovalResult, priority: true, forceGate: true);
                    SpeakingStarted();
                    break;
            }
        }
    }

    private bool IsPending(string approvalId) =>
        _context.Approvals.Pending.Any(request => string.Equals(request.ApprovalId, approvalId, StringComparison.Ordinal));

    /// <summary>Approval speech was handed to the output half: nothing captured before it goes quiet may answer it.</summary>
    private void SpeakingStarted()
    {
        _wasSpeaking = true;
        Volatile.Write(ref _quietAtSeconds, double.PositiveInfinity);
    }

    /// <summary>
    /// Records the ring time at which the speaker last fell silent. An utterance's segment start is compared
    /// against this, so a transcript that contains the prompt's own voice cannot approve the prompt.
    /// </summary>
    private void TrackQuiet()
    {
        if (IsSpeaking)
        {
            _wasSpeaking = true;
            return;
        }

        // Both conditions matter: without an output half there is nothing to have gone quiet from, and without a
        // previous "speaking" tick there is no transition to record.
        if (_output is null || !_wasSpeaking)
        {
            return;
        }

        // The input half starts after the approval watcher. Without this guard a prompt spoken in that gap
        // would clear the flag with no ring clock to record, leaving the quiet timestamp at infinity and
        // blocking every later voice answer.
        if (_input is not { } input)
        {
            return;
        }

        _wasSpeaking = false;
        Volatile.Write(ref _quietAtSeconds, input.Ring.Seconds);
    }

    /// <summary>
    /// Turns approval listening on while a request is pending and nothing is queued or playing. The playback
    /// gate already discards audio during speech; this flag additionally lets the input bypass a wake word, so
    /// "批准执行" works for as long as the person is waiting. Requiring the output to be fully idle keeps the
    /// prompt's own audio from being transcribed into an answer.
    /// </summary>
    private void UpdateApprovalListening()
    {
        var input = _input;
        var output = _output;
        if (input is null || output is null || Volatile.Read(ref _outputFaulted) != 0)
        {
            return;
        }

        var listening = _context.Approvals.Pending.Count > 0 && IsQuiet(output);
        input.SetApprovalListening(listening);
    }

    /// <summary>
    /// Output-loop view of "nothing is queued or playing". The sentence list is safe to read there and only
    /// there; the input thread uses <see cref="IsAudiblyQuiet"/>, which reads scalar flags instead.
    /// </summary>
    private bool IsQuiet(VoiceOutputHalf output) =>
        IsAudiblyQuiet(output) && output.SentencesEmpty;

    /// <summary>The scalar part of the quiet test: safe to read from the input loop while the output loop
    /// mutates its item list.</summary>
    private bool IsAudiblyQuiet(VoiceOutputHalf output) =>
        !output.IsPlaying
        && !output.PendingAudio
        && !output.TtsBusy
        && !output.IsStreaming
        && !_playbackGate.Engaged;

    /// <summary>
    /// Reads an utterance as an answer when it was captured while an approval was pending and the speaker was
    /// idle. With a wake word configured, an utterance that came through a wake session is equally deliberate.
    /// The capture-time flag matters: ASR can finish after playback starts, and a block that began in silence
    /// is still the person's answer.
    /// </summary>
    private bool TryAnswerApproval(VoiceUtterance utterance)
    {
        var input = _input;
        var output = _output;
        if (input is null || output is null || Volatile.Read(ref _outputFaulted) != 0)
        {
            return false;
        }

        if (!utterance.ApprovalListening && !input.WakeEnabled)
        {
            return false;
        }

        if (!IsAudiblyQuiet(output))
        {
            return false;
        }

        // The block must have collected audio after the speaker last went quiet. Its start can be much earlier
        // (the detector cursor includes the silence before speech), so the end is the reliable side. A
        // transcript produced from the prompt's own audio ends at or before the quiet transition and is
        // rejected here.
        if (utterance.SegEndMs / 1000.0 <= Volatile.Read(ref _quietAtSeconds))
        {
            return false;
        }

        var intent = ApprovalSpeech.Classify(utterance.Transcript, utterance.WakeWord);
        if (intent == ApprovalIntent.None)
        {
            return false;
        }

        var pending = _context.Approvals.Pending;
        if (pending.Count == 0)
        {
            return false;
        }

        var target = pending[0];
        var approved = intent == ApprovalIntent.Approve;

        if (target.ExpiresAt <= _context.Clock.GetUtcNow())
        {
            QueueResult(ApprovalSpeech.Expired());
            return true;
        }

        if (!_context.Approvals.Answer(target.ApprovalId, approved))
        {
            QueueResult(ApprovalSpeech.Settled());
            return true;
        }

        // Answer() unblocks the waiting turn; the request may still be visible for a moment, so count only the
        // other ids rather than trusting Pending.Count.
        var remaining = _context.Approvals.Pending.Count(request =>
            !string.Equals(request.ApprovalId, target.ApprovalId, StringComparison.Ordinal));
        QueueResult(ApprovalSpeech.Result(target, approved, remaining));
        VoiceLog($"voice: approval {(approved ? "granted" : "denied")} by speech ({target.ApprovalId}, {target.ToolName})");
        return true;
    }

    private void QueueResult(string text) => _approvalCues.Enqueue(new ResultCue(text));

    private abstract record ApprovalCue;

    private sealed record AnnounceCue(PendingApproval Request) : ApprovalCue;

    private sealed record DropCue(string ApprovalId) : ApprovalCue;

    private sealed record ResultCue(string Text) : ApprovalCue;

    private void InterruptOutput()
    {
        if (Volatile.Read(ref _outputFaulted) != 0) return;
        VoiceOutputHalf? output;
        lock (_outputLifecycle) output = _output;
        try { output?.StopPlayback(); }
        catch (Exception error) { if (output is not null) FailOutput(output, error); }
    }

    private void FailOutput(VoiceOutputHalf output, Exception error)
    {
        lock (_outputLifecycle)
        {
            if (!ReferenceEquals(output, _output) || Interlocked.Exchange(ref _outputFaulted, 1) != 0) return;
            output.Enabled = false;
            try { output.StopPlayback(); }
            catch (Exception stopError) { VoiceLog($"voice: stopping failed output: {stopError.Message}"); }
            _input?.SetApprovalListening(false);
            _playbackGate.TryRelease(new GateConditions(false, false, false, true, true, false, false), Environment.TickCount64 / 1000.0);
            _playbackGate.ResetSkipped();
            _outputState = _outputState with { Running = false, Reason = $"播放出错：{error.GetBaseException().Message}", Count = output.TotalCount };
            VoiceLog(_outputState.Reason);
        }
    }

    private static async Task AwaitLoopAsync(Task? loop)
    {
        if (loop is null) return;
        try { await loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task StopInputAsync()
    {
        if (_inputStop is null) return;
        await _inputStop.CancelAsync().ConfigureAwait(false);
        await AwaitLoopAsync(_inputLoop).ConfigureAwait(false);
        _input?.Dispose();
        _source?.Dispose();
        _asr?.Dispose();
        _inputStop.Dispose();
        _inputStop = null;
        _inputLoop = null;
        _input = null;
        _source = null;
        _asr = null;
    }

    private async Task StopOutputAsync()
    {
        if (_outputStop is null) return;
        await _outputStop.CancelAsync().ConfigureAwait(false);
        await AwaitLoopAsync(_outputLoop).ConfigureAwait(false);
        await AwaitLoopAsync(_approvalLoop).ConfigureAwait(false);
        lock (_outputLifecycle)
        {
            _input?.SetApprovalListening(false);
            _playbackGate.TryRelease(new GateConditions(false, false, false, true, true, false, false), Environment.TickCount64 / 1000.0);
            _playbackGate.ResetSkipped();
            _output?.Dispose();
            _sink?.Dispose();
            _tts?.Dispose();
            _outputStop.Dispose();
            _outputStop = null;
            _outputLoop = null;
            _approvalLoop = null;
            _output = null;
            _sink = null;
            _tts = null;
            _approvalCues.Clear();
            _announced.Clear();
            _wasSpeaking = false;
            Volatile.Write(ref _quietAtSeconds, 0);
        }
    }

    private async Task StopAsync()
    {
        await StopInputAsync().ConfigureAwait(false);
        await StopOutputAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _reconfigure.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _reconfigure.Release();
        }
    }
}
