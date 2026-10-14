using Partner.Voice.Asr;
using Partner.Voice.Audio;
using Partner.Voice.Perception;
using Partner.Voice.Playback;
using Partner.Voice.Settings;

namespace Partner.Voice;

/// <summary>One transcribed segment, with everything the queue item needs (design doc §2.2).</summary>
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
/// Input half (design doc §2.8.1–§2.8.10): ring buffer → pause blocking → ASR → one utterance per segment.
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

    private bool _running;
    private bool _wasGated;
    private double _gateCursor;
    private bool _skippedHadSpeech;
    private bool _ambientChecked;

    // 唤醒门状态：_awake=false 时音频照常进环形缓冲，只是不推进停顿判定、不提交 ASR。
    // 唤醒后的窗口由 Detector 自身的静音超时（UtteranceGap/SentenceGap）自然闭合——本半区只负责「回闸」。
    private bool _awake;
    private long _wakeSeq;

    public VoiceInputHalf(
        VoiceInputSetting settings,
        IAudioSource source,
        IAsrClient asr,
        PlaybackGate gate,
        Func<VoiceUtterance, Task> onUtterance,
        Action<string>? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _asr = asr ?? throw new ArgumentNullException(nameof(asr));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _onUtterance = onUtterance ?? throw new ArgumentNullException(nameof(onUtterance));
        _log = log ?? (_ => { });

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
        _log($"voice input started (device={DeviceId} threshold={_settings.SilenceThreshold})");
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

        if (!_ambientChecked && now >= 0.5)
        {
            // Sampled on a quiet tick on purpose: taken unconditionally it lands inside the first sentence
            // and reports *speech* as the noise floor.
            _ambientChecked = true;
            AmbientRms = Ring.Energy(0.5);
            if (AmbientRms >= _settings.SilenceThreshold)
            {
                ThresholdMisconfigured = true;
                _log(
                    $"voice: threshold {_settings.SilenceThreshold:0.####} is not above the ambient level "
                    + $"{AmbientRms:0.####} — silence can never be detected, so no utterance will ever end");
            }
        }

        // While the assistant speaks, input is discarded wholesale (I4: the drop must be visible).
        if (_gate.Engaged)
        {
            if (!_wasGated)
            {
                _wasGated = true;
                _gateCursor = now;
            }

            var energy = Ring.Energy(0.05);
            _skippedHadSpeech |= energy >= _settings.SilenceThreshold;
            return;
        }

        // 唤醒门：未唤醒时只问 KWS，不推进停顿判定。音频仍进环形缓冲——一旦唤醒，
        // 最近的窗口（≤ WindowSeconds）立刻可用，唤醒词后紧跟着的话不会被掐头。
        if (_settings.Wake is not null && !_awake)
        {
            await PollWakeAsync(ct).ConfigureAwait(false);
            return;
        }

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

        var decision = Detector.Step(now, Ring.Energy(0.05));

        // 唤醒会话结束（一段话被提交并被 Detector 闭合）之后回到监听态：下一段要先再喊一次唤醒词。
        if (decision.SegmentEnded)
        {
            if (_settings.Wake is not null && _awake)
            {
                _awake = false;
                _log("voice: wake session closed — waiting for the next wake word");
            }

            Detector.OnSegmentEnded(now);
            return;
        }

        if (decision.Submit)
        {
            await SubmitAsync(decision, ct).ConfigureAwait(false);
        }
    }

    private async Task PollWakeAsync(CancellationToken ct)
    {
        try
        {
            var hit = await _settings.Wake!.PollAsync(_wakeSeq, ct).ConfigureAwait(false);
            _wakeSeq = Math.Max(_wakeSeq, hit.Seq);
            if (hit.IsHit && !_awake)
            {
                _awake = true;
                // 唤醒瞬间相当于一段新语音的开始：重置判定，让唤醒词后的话从干净状态进停顿机。
                Detector.OnGateReleased(Ring.Seconds);
                _log($"voice: wake word \"{hit.Keyword}\" ({hit.Confidence:0.00}) — listening");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // KWS 失联必须可见，但该半区不能死：下一 tick 会再问，服务恢复后自动跟回。
            _log($"voice: wake poll failed: {ex.Message}");
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
        _log("voice input stopped");
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

        if (Ring.SpeechFraction(_settings.SilenceThreshold, duration) < MinVoicedFraction)
        {
            // Pure silence or noise: not worth a request.
            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Skipped);
            return;
        }

        AsrBusy = true;
        PendingBlock = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var text = await _asr.TranscribeAsync(audio, ct).ConfigureAwait(false);
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
                await _onUtterance(utterance).ConfigureAwait(false);
            }

            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Queued);

            // 唤醒会话在整段静默闭合（G2）的那一刻结束：一句说完了，回闸等下一次唤醒。
            // 中途提交（WindowCap/G1）不算——说话人还没说完。
            if (_awake && decision.Trigger == BlockTrigger.UtteranceGap && decision.Pause)
            {
                _awake = false;
                _log("voice: wake session closed after the utterance — waiting for the next wake word");
            }
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
