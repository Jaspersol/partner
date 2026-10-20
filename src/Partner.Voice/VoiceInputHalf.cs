using Partner.Voice.Asr;
using Partner.Voice.Audio;
using Partner.Voice.Perception;
using Partner.Voice.Playback;
using Partner.Voice.Settings;

namespace Partner.Voice;

/// <summary>One transcribed segment, with everything the queue item needs.</summary>
public sealed record VoiceUtterance
{
    public required string UtteranceId { get; init; }

    public required string DeviceId { get; init; }

    /// <summary>Process boot id: the stream clock resets on restart, so the key must include it.</summary>
    public required string BootId { get; init; }

    public required double SegStartMs { get; init; }

    public required double SegEndMs { get; init; }

    public required string Transcript { get; init; }

    public required BlockTrigger Trigger { get; init; }

    public required double AsrLatencyMs { get; init; }

    /// <summary><c>voice:{deviceId}:{bootId}:{segStartMs}</c> — see <see cref="BootId"/> for why.</summary>
    public string IdempotencyKey => $"voice:{DeviceId}:{BootId}:{SegStartMs:0}";
}

/// <summary>
/// Input half: ring buffer → pause blocking → ASR → one utterance per segment.
/// <para>
/// <see cref="TickAsync"/> is public on purpose: the whole pipeline can be driven deterministically from a
/// synthetic source, which is the only way to test pause boundaries without a microphone.
/// </para>
/// </summary>
public sealed class VoiceInputHalf : IDisposable
{
    /// <summary>Shorter than this is not worth an ASR request.</summary>
    public const double MinBlockSeconds = 0.3;

    /// <summary>A block must contain at least this much speech, else it is noise.</summary>
    public const double MinVoicedFraction = 0.15;

    private readonly VoiceInputSetting _settings;
    private readonly IAudioSource _source;
    private readonly IAsrClient _asr;
    private readonly PlaybackGate _gate;
    private readonly Func<VoiceUtterance, Task> _onUtterance;
    private readonly Action<string> _log;
    private readonly List<VoiceUtterance> _utterances = new();

    /// <summary>Callback that plays the wake tone (host hands in the output sink's Enqueue); null = enter silently.</summary>
    private readonly Action? _wakeTone;

    private bool _running;
    private bool _wasGated;
    private readonly Action? _onBargeIn;
    private double? _loudSince;
    private bool _bargedIn;
    private double _gateCursor;
    private bool _skippedHadSpeech;
    private bool _ambientChecked;

    // Wake gate state: while _awake is false audio still enters the ring, but the pause detector does not
    // advance and nothing is submitted to ASR. The wake session closes on the G2 tick (utterance gap); a
    // mid-utterance submission (WindowCap/G1) does not close it.
    private bool _awake;

    public VoiceInputHalf(
        VoiceInputSetting settings,
        IAudioSource source,
        IAsrClient asr,
        PlaybackGate gate,
        Func<VoiceUtterance, Task> onUtterance,
        Action<string>? log = null,
        Action? wakeTone = null,
        Action? onBargeIn = null)
    {
        _onBargeIn = onBargeIn;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _asr = asr ?? throw new ArgumentNullException(nameof(asr));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _onUtterance = onUtterance ?? throw new ArgumentNullException(nameof(onUtterance));
        _log = log ?? (_ => { });
        // The tone belongs to the output device: the input half takes an action, not a sink type.
        _wakeTone = settings.WakeTone ? wakeTone : null;

        Ring = new AudioRingBuffer(settings.WindowSeconds + 2.0, source.SampleRate);
        Detector = new PauseBlockingDetector(settings);
        DeviceId = source.DeviceName;
        BootId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Environment.ProcessId}";
    }

    public AudioRingBuffer Ring { get; }

    public PauseBlockingDetector Detector { get; }

    public string DeviceId { get; }

    public string BootId { get; }

    /// <summary>An ASR request is in flight (one of the gate's seven conditions).</summary>
    public bool AsrBusy { get; private set; }

    /// <summary>A block has been submitted but its transcript has not been absorbed yet.</summary>
    public bool PendingBlock { get; private set; }

    public bool IsRunning => _running;

    /// <summary>Set when the ambient level already sits at or above the gate: silence can never be detected.</summary>
    public bool ThresholdMisconfigured { get; private set; }

    public double AmbientRms { get; private set; }

    /// <summary>Input level (RMS) over the last 0.5 s.</summary>
    public double RecentRms { get; private set; }

    /// <summary>Speech was detected while the gate was closed (reported when the gate reopens).</summary>
    public bool DetectedSpeechWhileGated => _skippedHadSpeech;

    /// <summary>The wake gate is currently open (only meaningful when <see cref="VoiceInputSetting.Wake"/> is set).</summary>
    public bool Awake => _awake;

    public IReadOnlyList<VoiceUtterance> Utterances => _utterances;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        // The source is a pure device adapter; the ring is written here so any source works the same way.
        _source.Start(samples => Ring.Write(samples));
        _running = true;
        _log(
            $"voice: input started (device={DeviceId} threshold={_settings.SilenceThreshold} "
            + $"wake={(_settings.Wake is null ? "off" : "on")})");
    }

    public async Task RunAsync(TimeSpan? pollInterval = null, CancellationToken ct = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);

        while (!ct.IsCancellationRequested)
        {
            await TickAsync(ct).ConfigureAwait(false);
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One tick: gate handling, then the pause state machine, then at most one ASR call.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var now = Ring.Seconds;
        Detector.NoteNow(now);

        // A transcription that finished since the last tick is taken first, so its result (and the detector's cursor)
        // are settled before anything else in this tick looks at them.
        await AbsorbAsrAsync(ct).ConfigureAwait(false);

        // Refresh the level meter every 100 ms: computing a 0.5 s window on every tick wastes CPU. It is
        // refreshed inside and outside the gate — the level during playback must be reported too.
        if (_levelTicks == 0)
        {
            RecentRms = Ring.Energy(LevelWindowSeconds);
        }

        _levelTicks = (_levelTicks + 1) % LevelRefreshTicks;

        if (!_ambientChecked && now >= 0.5)
        {
            // Sampled on a quiet tick on purpose: taken unconditionally it lands inside the first sentence
            // and reports *speech* as the noise floor. When no quiet window shows up within
            // AmbientProbeSeconds the report is made anyway, on the quietest window seen.
            var level = Ring.Energy(0.5);
            _quietestRms = Math.Min(_quietestRms, level);
            var quiet = level < _settings.SilenceThreshold;

            if (quiet || now >= AmbientProbeSeconds)
            {
                _ambientChecked = true;
                AmbientRms = _quietestRms;
                if (AmbientRms >= _settings.SilenceThreshold)
                {
                    ThresholdMisconfigured = true;
                    _log(
                        $"voice: threshold {_settings.SilenceThreshold:0.####} is not above the ambient level "
                        + $"{AmbientRms:0.####} — silence can never be detected, so no utterance will ever end "
                        + $"(no quiet window in the first {AmbientProbeSeconds:0} s)");
                }
            }
        }

        // While the assistant speaks, input is discarded wholesale (the drop must be visible).
        if (_gate.Engaged)
        {
            if (!_wasGated)
            {
                _wasGated = true;
                _gateCursor = now;
            }

            var energy = Ring.Energy(0.05);
            _skippedHadSpeech |= energy >= _settings.SilenceThreshold;
            NoteBargeIn(now, energy);
            return;
        }

        // Wake gate: while not awake, only probe KWS and do not advance the pause detector. Audio still enters
        // the ring, so once awake the recent window (≤ WindowSeconds) is immediately available and speech
        // right after the wake word is not cut off.
        if (_settings.Wake is not null && !_awake)
        {
            await PollWakeAsync(ct).ConfigureAwait(false);
            return;
        }

        _loudSince = null;
        _bargedIn = false;

        if (_wasGated)
        {
            // Playback finished: drop the whole played span and resume immediately.
            _wasGated = false;
            var skipped = Math.Max(0, now - _gateCursor);
            _gate.NoteSkipped(skipped, _skippedHadSpeech);
            if (_skippedHadSpeech)
            {
                _log($"voice: discarded {skipped:0.0}s of audio captured while the assistant was speaking");
            }

            _skippedHadSpeech = false;
            Detector.OnGateReleased(now);
        }

        var decision = Detector.Step(now, Ring.Energy(0.05), suppressSpeechOnset: now < _toneUntil);

        // A wake session ends when a block is submitted and the detector closes it: the next segment needs a new wake word.
        if (decision.SegmentEnded)
        {
            if (_settings.Wake is not null && _awake)
            {
                _awake = false;
                _log($"voice: wake session closed — {SessionSummary()}; waiting for the next wake word");
            }

            Detector.OnSegmentEnded(now);
            return;
        }

        if (decision.Submit)
        {
            await SubmitAsync(decision, ct).ConfigureAwait(false);
        }

        // Wake session close is decoupled from the submission outcome: G2 (utterance gap) ends the utterance
        // even when the block is not worth transcribing. Closing only on the SubmitAsync success path leaves
        // _awake stuck true for a silent block, so the wake probe never runs again and the gate stays open.
        if (_awake && decision.Trigger == BlockTrigger.UtteranceGap && decision.Pause)
        {
            _awake = false;
            _log($"voice: wake session closed after the utterance — {SessionSummary()}; waiting for the next wake word");
        }
    }

    /// <summary>
    /// The half-line log written when a session closes: it must state "nothing submitted" when no character
    /// reached ASR, so a dropped command is distinguishable from a transcribed one.
    /// </summary>
    private string SessionSummary() => _sessionChars > 0
        ? $"{_sessionChars} chars submitted"
        : $"nothing submitted (ASR calls {_sessionAsrCalls})";

    private async Task PollWakeAsync(CancellationToken ct)
    {
        // The probe reads increments only: overlapping windows replay the same audio into one engine stream,
        // poison its streaming state, and a real utterance then never hits.
        var delta = Ring.ReadSince(_wakeCursor, 2.0);
        if (delta is null || delta.Length < 16_000 * 0.10)
        {
            if (delta is null)
            {
                // Ring already evicted the window: move the cursor to the current stream position and restart as a new stream.
                _wakeCursor = Ring.Seconds;
                _wakeNeedsLead = true;
            }

            return; // ask only on a full increment: 100 ms increments cap the HTTP rate at ≤10/s.
        }

        // Lead audio is sent only when the engine's stream is new. The zipformer KWS decoder needs a little left
        // context to settle its state; sending lead on every frame replays the previous increment and the same
        // word is heard twice, which can wake the engine on a single utterance.
        var lead = _wakeNeedsLead
            ? Ring.ReadSince(Math.Max(0, _wakeCursor - WakeLeadSeconds), WakeLeadSeconds)
              ?? Array.Empty<float>()
            : Array.Empty<float>();
        _wakeNeedsLead = false;

        var frame = new float[lead.Length + delta.Length];
        lead.CopyTo(frame, 0);
        delta.CopyTo(frame, lead.Length);

        _wakeCursor = Ring.Seconds; // advance only after sending: unsent increments wait in place for the next tick.

        try
        {
            var hit = await _settings.Wake!.PollAsync(frame, ct).ConfigureAwait(false);

            if (_wakeFailure is not null)
            {
                _wakeFailure = null;
                _log("voice: wake probe recovered");
            }

            if (hit.IsHit && !_awake)
            {
                _awake = true;
                _sessionChars = 0;
                _sessionAsrCalls = 0;

                // The engine opens a new stream after a hit (a stream keeps the matched prefix state),
                // so the next probe must send lead audio again.
                _wakeNeedsLead = true;

                // A wake hit starts a new segment: reset detection so speech after the wake word enters the
                // pause detector from a clean state. Use OnWakeHit rather than OnGateReleased: the wake word is
                // itself "speech heard", so the segment must be dirty or a silent session never satisfies G2.
                // The cursor backs off WakeCatchUpSeconds because the hit is reported late and the command is
                // usually already underway before the hit point. A full stretch of speech in that window means
                // the command and wake word were spoken in one breath, so the grace period is skipped.
                var catchUpStart = Math.Max(0, Ring.Seconds - WakeCatchUpSeconds);
                var heardSpeechAtHit =
                    Ring.SpeechFraction(_settings.SilenceThreshold, WakeCatchUpSeconds) >= WakeCatchUpSpeechFraction;
                Detector.OnWakeHit(catchUpStart, heardSpeechAtHit);

                // The tone plays through the speaker and, without headphones, the microphone picks it up,
                // which the pause detector reads as speech onset. Open the suppression window first.
                if (_wakeTone is not null)
                {
                    _toneUntil = Ring.Seconds + ToneEchoSeconds;
                    _wakeTone();
                }

                _log($"voice: wake word \"{hit.Keyword}\" ({hit.Confidence:0.00}) — listening");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A lost KWS must be visible, but this half must not die: the next tick retries and recovers when
            // the service returns. Log only the first failure and changes of reason, not one line per tick.
            if (!string.Equals(_wakeFailure, ex.Message, StringComparison.Ordinal))
            {
                _wakeFailure = ex.Message;
                _log($"voice: wake poll failed: {ex.Message}");
            }

            // The engine may have just restarted: its stream is gone, so lead must be sent again.
            _wakeNeedsLead = true;
        }
    }

    /// <summary>Stream position last read by the probe (Ring.Seconds); the probe reads increments from it, without overlap.</summary>
    private double _wakeCursor;

    /// <summary>
    /// Whether the next probe carries lead audio. True only when the engine's stream is new (first frame, stream
    /// change on hit, engine restart, cursor resync); sending it on every frame makes the stream hear each
    /// utterance twice.</summary>
    private bool _wakeNeedsLead = true;

    /// <summary>Last logged probe failure reason; the same reason is not logged twice.</summary>
    private string? _wakeFailure;

    /// <summary>
    /// End of the tone echo suppression window (stream-clock seconds). Inside it, sound does not count as speech
    /// onset, so the grace period is not ended by the tone.
    /// <para>
    /// Window length = tone 0.16 s (880/1320 Hz for 80 ms each) + WASAPI output latency + room reverberation.
    /// It is anchored to the stream position at the hit, not to the next tick, because a tick-based count drifts
    /// with probe cadence and queue latency.
    /// </para>
    /// <para>
    /// Do not implement this with <see cref="PlaybackGate"/>: its <c>OnGateReleased</c> clears
    /// <c>_awaitingSpeech</c> and moves the cursor forward, undoing both the post-wake pause and the hit catch-up.
    /// </para>
    /// </summary>
    private const double ToneEchoSeconds = 0.6;

    private double _toneUntil;

    /// <summary>Characters submitted by this wake session: distinguishes "said N chars" from "nothing submitted" when it closes.</summary>
    private int _sessionChars;

    /// <summary>Number of ASR calls actually made in this wake session; 0 means no request was ever sent.</summary>
    private int _sessionAsrCalls;

    /// <summary>Level meter refresh period in ticks: 100 ms at a 20 ms tick.</summary>
    private const int LevelRefreshTicks = 5;

    /// <summary>Level meter sampling window (seconds): shorter is jumpy, longer drags the previous sentence into silence.</summary>
    private const double LevelWindowSeconds = 0.5;

    /// <summary>Longest wait for the ambient probe (seconds): if no frame drops below the threshold in that time, report a misconfiguration.</summary>
    private const double AmbientProbeSeconds = 5.0;

    /// <summary>Quietest frame seen during the ambient probe; used to conclude when no quiet frame ever appears.</summary>
    private double _quietestRms = double.MaxValue;

    private int _levelTicks;

    /// <summary>
    /// Wake probe lead audio length (seconds). The KWS decoder needs left context to settle its state; 1.0 s is
    /// the measured minimum that hits reliably. Replaying it on the same stream position is harmless.
    /// </summary>
    private const double WakeLeadSeconds = 1.0;

    /// <summary>
    /// How far the block start backs off on a wake hit (seconds), covering the KWS chunk-level detection latency
    /// (chunk-16 is about 320 ms, plus probe queueing).
    /// <para>
    /// Without the backoff, audio before the hit point is lost and a short command spoken right after the wake
    /// word leaves a silent block that fails <c>SpeechFraction</c> and never reaches ASR.
    /// </para>
    /// <para>
    /// The cost is that the wake word tail (≤ this value) may be transcribed too. A partial wake word is better
    /// than a lost command.
    /// </para>
    /// </summary>
    private const double WakeCatchUpSeconds = 0.45;

    /// <summary>
    /// Minimum voiced fraction in the catch-up window that counts as "a full stretch of speech", deciding whether
    /// the wake grace period still applies after a hit.
    /// <para>
    /// 0.5 rather than the submission gate's <see cref="MinVoicedFraction"/> (0.15): the wake word tail itself
    /// sits at the head of the window (about 29% with chunk-16's 320 ms latency), and 0.15 would treat "said the
    /// word and stopped" as speech, disabling the grace period. 0.5 lets "wake word tail + command" reach it
    /// while "tail + silence" does not.
    /// </para>
    /// </summary>
    private const double WakeCatchUpSpeechFraction = 0.5;


    /// <summary>
    /// While the assistant speaks, sustained sound well above the silence threshold means the person is talking
    /// over it. "Well above" (twice the threshold) and "sustained" (<see cref="VoiceInputSetting.BargeInMs"/>) are
    /// both there to keep the assistant's own voice, coming back through the room, from interrupting it.
    /// Once per speaking turn: the host stops the playback and the gate opens by the normal path.
    /// </summary>
    private void NoteBargeIn(double now, double energy)
    {
        if (!_settings.BargeIn || _onBargeIn is null || _bargedIn || now < _toneUntil)
        {
            return;
        }

        if (energy < _settings.SilenceThreshold * 2)
        {
            _loudSince = null;
            return;
        }

        _loudSince ??= now;
        if ((now - _loudSince.Value) * 1000 >= _settings.BargeInMs)
        {
            _bargedIn = true;
            _log("voice: the person spoke over the assistant — stopping playback");
            _onBargeIn();
        }
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _source.Stop();
        _log("voice: input stopped");
    }

    public void Dispose()
    {
        Stop();
        _source.Dispose();
        _asr.Dispose();
    }

    private async Task SubmitAsync(PauseDecision decision, CancellationToken ct)
    {
        var start = decision.BlockStart;
        var end = decision.BlockEnd;
        var duration = end - start;

        if (duration < MinBlockSeconds)
        {
            _log(
                $"voice: block dropped — {duration:0.00}s is under the {MinBlockSeconds:0.0}s minimum "
                + "(no ASR request)");
            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Skipped);
            return;
        }

        var audio = Ring.ReadSince(start, _settings.WindowSeconds);
        if (audio is null)
        {
            _log($"voice: audio cursor evicted, dropped {duration:0.0}s and resynchronized");
            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Skipped);
            return;
        }

        // Pure silence or noise: not worth a request. The log line is the only record of why a block was dropped.
        var speechFraction = Ring.SpeechFraction(_settings.SilenceThreshold, duration);
        if (speechFraction < MinVoicedFraction)
        {
            _log(
                $"voice: block dropped — speech fraction {speechFraction:0.00} is under "
                + $"{MinVoicedFraction:0.00} across {duration:0.00}s (no ASR request)");
            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Skipped);
            return;
        }

        // One request at a time, and the tick does not wait for it. The audio of a block that arrives while
        // another is being transcribed is not lost: Busy keeps the cursor and the owed block, and the next tick
        // asks again once the first has been absorbed.
        if (_inFlight is not null)
        {
            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Busy);
            return;
        }

        AsrBusy = true;
        PendingBlock = true;
        _sessionAsrCalls++;
        _inFlight = new InFlightAsr(decision, start, end, duration, System.Diagnostics.Stopwatch.StartNew(), _asr.TranscribeAsync(audio, ct));

        // A fast ASR (or a scripted one) is already done: absorbing it now keeps one block, one tick.
        await AbsorbAsrAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A transcription that has been started and whose result has not been taken yet.</summary>
    private sealed record InFlightAsr(
        PauseDecision Decision,
        double Start,
        double End,
        double Duration,
        System.Diagnostics.Stopwatch Watch,
        Task<string> Text);

    private InFlightAsr? _inFlight;

    /// <summary>
    /// Takes the result of the transcription in flight, if it has finished. Runs at the top of every tick, on the
    /// tick's own thread: the detector and the utterance list are only ever touched there, which is what lets the
    /// request itself run beside the ticks without a lock.
    /// </summary>
    private async Task AbsorbAsrAsync(CancellationToken ct)
    {
        if (_inFlight is not { } job || !job.Text.IsCompleted)
        {
            return;
        }

        _inFlight = null;
        var decision = job.Decision;
        var start = job.Start;
        var end = job.End;
        var duration = job.Duration;
        var watch = job.Watch;

        try
        {
            var text = await job.Text.ConfigureAwait(false);
            watch.Stop();

            if (!string.IsNullOrWhiteSpace(text))
            {
                var utterance = new VoiceUtterance
                {
                    UtteranceId = Guid.NewGuid().ToString("n"),
                    DeviceId = DeviceId,
                    BootId = BootId,
                    SegStartMs = start * 1000,
                    SegEndMs = end * 1000,
                    Transcript = text.Trim(),
                    Trigger = decision.Trigger,
                    AsrLatencyMs = watch.Elapsed.TotalMilliseconds,
                };

                _utterances.Add(utterance);
                _sessionChars += utterance.Transcript.Length;
                await _onUtterance(utterance).ConfigureAwait(false);
            }

            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Queued);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed transcription must be visible; it must not become an empty utterance.
            _log($"voice: transcription failed for {duration:0.0}s of audio: {ex.Message}");
            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Skipped);
        }
        finally
        {
            AsrBusy = false;
            PendingBlock = false;
        }
    }
}
