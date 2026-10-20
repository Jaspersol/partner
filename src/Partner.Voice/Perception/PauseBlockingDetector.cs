using Partner.Voice.Settings;

namespace Partner.Voice.Perception;

public enum BlockTrigger
{
    None,

    /// <summary>G1: a sentence-level pause (streaming mode only).</summary>
    SentenceGap,

    /// <summary>G2: the utterance is over.</summary>
    UtteranceGap,

    /// <summary>Continuous speech hit <c>windowSeconds</c>: cut so the block cannot grow forever.</summary>
    WindowCap,

    /// <summary>The caller asked for the current block (used when a stop must flush rather than discard).</summary>
    Manual,
}

/// <summary>What one tick decided. The caller performs the submission and reports the outcome back.</summary>
public sealed record PauseDecision
{
    public required bool Submit { get; init; }

    public required double BlockStart { get; init; }

    public required double BlockEnd { get; init; }

    /// <summary>Pause-triggered block: its transcript gets a sentence boundary appended.</summary>
    public required bool Pause { get; init; }

    public required BlockTrigger Trigger { get; init; }

    /// <summary>The segment is over: flush, release playback, close the gate.</summary>
    public required bool SegmentEnded { get; init; }

    public static PauseDecision None(double cursor) => new()
    {
        Submit = false,
        BlockStart = cursor,
        BlockEnd = cursor,
        Pause = false,
        Trigger = BlockTrigger.None,
        SegmentEnded = false,
    };
}

/// <summary>
/// The pause/blocking state machine. Time and energy are passed in, so the whole thing is deterministic and
/// testable without audio hardware.
/// </summary>
public sealed class PauseBlockingDetector
{
    /// <summary>Compensation for the energy window + poll cadence.</summary>
    public const double SilenceOffsetMs = 100;

    /// <summary>
    /// Minimum accumulated voiced time (ms) in one sound cluster for it to count as speech onset.
    /// <para>
    /// Must be cumulative, not consecutive: Mandarin syllables cross the threshold for only 60–190 ms and drop
    /// below it between syllables. Requiring 200 ms of continuous sound lets a whole command miss the grace
    /// period, and the block is then diluted below <c>SpeechFraction</c> and dropped as noise.
    /// </para>
    /// </summary>
    public const double WakeOnsetMs = 200;

    /// <summary>
    /// Longest silence allowed inside one sound cluster (seconds): shorter gaps are syllable gaps and do not
    /// reset the accumulation; longer ones start a new cluster.
    /// </summary>
    public const double OnsetBridgeSeconds = 0.3;

    /// <summary>
    /// How far the block start backs off before the cluster start when the grace period ends (seconds):
    /// consonant onsets are below threshold, so without this margin the first syllable is cut off.
    /// </summary>
    public const double OnsetPreRollSeconds = 0.3;

    /// <summary>Energy window length passed in by the caller (ms): when one window crosses the threshold, speech may have started at its head.</summary>
    public const double EnergyWindowMs = 50;

    /// <summary>Upper bound on the time one tick may add to the accumulation (seconds), so a stalled tick cannot fill it at once.</summary>
    private const double MaxTickSeconds = 0.1;

    private readonly VoiceInputSetting _settings;
    private readonly double _sentenceGapMs;
    private readonly double _utteranceGapMs;
    private readonly double _windowSeconds;
    private readonly double _wakeTimeoutMs;

    private double? _quietSince;
    private bool _dirty;
    private bool _called;
    private bool _leftover;
    private bool? _heldPause;

    /// <summary>
    /// The current segment's sound so far comes only from the wake word: no real utterance has started yet.
    /// <para>
    /// Kept separate from <see cref="_dirty"/> because the two need different silence lengths: after the wake
    /// word the user may pause to think, and closing the session on <see cref="VoiceInputSetting.UtteranceGapMs"/>
    /// would shut the gate before they speak.
    /// </para>
    /// </summary>
    private bool _awaitingSpeech;

    /// <summary>Start of the current sound cluster during the grace period (stream clock); see <see cref="OnsetBridgeSeconds"/>.</summary>
    private double? _onsetClusterStart;

    /// <summary>Accumulated voiced seconds in the current cluster; reaching <see cref="WakeOnsetMs"/> counts as speech onset.</summary>
    private double _onsetVoicedSeconds;

    /// <summary>Stream time of the last above-threshold tick outside the suppression window, used to tell whether a gap exceeds <see cref="OnsetBridgeSeconds"/>.</summary>
    private double? _onsetLastLoud;

    public PauseBlockingDetector(VoiceInputSetting settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sentenceGapMs = settings.SentenceGapMs;
        _utteranceGapMs = settings.UtteranceGapMs;
        _windowSeconds = settings.WindowSeconds;
        _wakeTimeoutMs = settings.WakeTimeoutMs;
        Cursor = 0;
    }

    /// <summary>Start of the pending audio block (stream seconds).</summary>
    public double Cursor { get; private set; }

    /// <summary>True once speech has been heard in the current segment.</summary>
    public bool Dirty => _dirty;

    /// <summary>The previous submission was cut short by the ring cap: audio is still owed.</summary>
    public bool HasLeftover => _leftover;

    /// <summary>A block was refused by a full backlog and is still owed.</summary>
    public bool HasHeldPause => _heldPause is not null;

    /// <summary>
    /// One tick. <paramref name="nowSeconds"/> is the stream clock, <paramref name="energy"/> the RMS.
    /// <para>
    /// When <paramref name="suppressSpeechOnset"/> is true, sound in this tick does not count as speech onset:
    /// the wake tone plays through the output device and, without headphones, the microphone picks it up well
    /// above the threshold. Without suppression the grace period would end at the wake instant and the session
    /// would close about 1.1 s after the tone, so no command reaches ASR. The audio still counts toward the
    /// block; it just does not clear <see cref="_awaitingSpeech"/>.
    /// </para>
    /// </summary>
    public PauseDecision Step(double nowSeconds, double energy, bool suppressSpeechOnset = false)
    {
        // The tick time is recorded here so callers cannot forget it: it is what decides whether a
        // submission left audio behind.
        var tickSeconds = Math.Clamp(nowSeconds - (_prevStep ?? nowSeconds), 0, MaxTickSeconds);
        _prevStep = nowSeconds;
        _lastNow = nowSeconds;

        var quiet = energy < _settings.SilenceThreshold;
        double silenceMs;

        // Step order matters: an owed block is retried before anything moves the cursor.
        if (_heldPause is not null)
        {
            return new PauseDecision
            {
                Submit = true,
                BlockStart = Cursor,
                BlockEnd = nowSeconds,
                Pause = _heldPause.Value,
                Trigger = BlockTrigger.Manual,
                SegmentEnded = false,
            };
        }

        if (quiet)
        {
            _quietSince ??= nowSeconds;
            silenceMs = ((nowSeconds - _quietSince.Value) * 1000) + SilenceOffsetMs;

            // Nothing awake and nothing owed: the audio behind the cursor is silence only, so drop it.
            if (!_dirty && !_leftover)
            {
                Cursor = nowSeconds;
            }
        }
        else
        {
            _quietSince = null;
            _called = false;
            silenceMs = 0;
            _dirty = true;

            // Real speech: the grace period ends and normal sentence-gap logic takes over.
            // Requires accumulated voiced time in one cluster, not a single energy window: see WakeOnsetMs.
            if (_awaitingSpeech && !suppressSpeechOnset)
            {
                NoteOnsetCandidate(nowSeconds, tickSeconds);
            }
        }

        // Long unbroken speech: submit anyway so the block cannot grow past the window.
        if (!quiet
            && (_leftover || nowSeconds - Cursor >= _windowSeconds)
            && _heldPause is null)
        {
            return new PauseDecision
            {
                Submit = true,
                BlockStart = Cursor,
                BlockEnd = nowSeconds,
                Pause = false,
                Trigger = BlockTrigger.WindowCap,
                SegmentEnded = false,
            };
        }

        // G1 (streaming only): one complete sentence per request. Not a sentence boundary while the grace period
        // is active — that silence is the user composing the sentence, and the cut block would be pure silence.
        if (_settings.AsrStream
            && quiet
            && !_awaitingSpeech
            && silenceMs >= _sentenceGapMs
            && !_called
            && _heldPause is null)
        {
            return new PauseDecision
            {
                Submit = true,
                BlockStart = Cursor,
                BlockEnd = nowSeconds,
                Pause = true,
                Trigger = BlockTrigger.SentenceGap,
                SegmentEnded = false,
            };
        }

        // G2: the utterance is over in both modes. During the grace period use the wake timeout instead of the
        // sentence gap: the silence between the tone and speech is not "finished talking", and closing the gate
        // on the sentence gap shuts it before the user speaks.
        var gapMs = _awaitingSpeech ? _wakeTimeoutMs : _utteranceGapMs;
        if (quiet && silenceMs >= gapMs && _dirty && _heldPause is null)
        {
            var submit = _leftover || !_settings.AsrStream;

            return new PauseDecision
            {
                Submit = submit,
                BlockStart = Cursor,
                BlockEnd = nowSeconds,
                Pause = true,
                Trigger = BlockTrigger.UtteranceGap,

                // The segment only ends once nothing is owed.
                SegmentEnded = !submit,
            };
        }

        return PauseDecision.None(Cursor);
    }

    /// <summary>Reports the outcome of a submission so the cursor and the owed-block state can advance.</summary>
    public void OnSubmitted(double end, bool pause, SubmissionOutcome outcome)
    {
        switch (outcome)
        {
            case SubmissionOutcome.Queued:
            case SubmissionOutcome.Skipped:
                Cursor = end;
                _leftover = end < _lastNow;
                _called = true;
                _heldPause = null;
                if (outcome == SubmissionOutcome.Queued)
                {
                    _dirty = true;
                }

                break;

            case SubmissionOutcome.Busy:
                // Refused: keep the cursor and the block's pause flag, retry next tick.
                _heldPause = pause;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    /// <summary>Called when the segment ends: flush happened, playback released, gate closed.</summary>
    public void OnSegmentEnded(double nowSeconds)
    {
        Cursor = nowSeconds;
        _leftover = false;
        _heldPause = null;
        _dirty = false;
        _awaitingSpeech = false;
        ResetOnset();
        _quietSince = null;
        _called = false;
    }

    /// <summary>Called when the gate reopens: the audio spoken during playback is discarded wholesale.</summary>
    public void OnGateReleased(double nowSeconds)
    {
        Cursor = nowSeconds;
        _heldPause = null;
        _leftover = false;
        _awaitingSpeech = false;
        ResetOnset();
        _quietSince = null;
        _called = false;
    }

    /// <summary>
    /// Called when the wake word hits: a new segment starts here. Identical to
    /// <see cref="OnGateReleased"/> except that it marks the segment as speech-bearing — the wake word
    /// <b>is</b> something the user said. Without it a session where the user says nothing after the word
    /// never satisfies G2 (<c>_dirty</c> stays false), so the gate stays open, the wake probe stops running,
    /// and the tone sounds exactly once.
    /// <para>
    /// It also arms the wake grace period (<see cref="_awaitingSpeech"/>): until the user actually says
    /// something, the silence after the word is measured against <see cref="VoiceInputSetting.WakeTimeoutMs"/>
    /// rather than the sentence gap, so a "wake word, then think for a moment, then speak" flow is not cut off
    /// about 1.1 s after the tone.
    /// </para>
    /// <para>
    /// When <paramref name="heardSpeechAtHit"/> is true the grace period is skipped: the hit is reported with
    /// latency, so a wake word and command spoken in one breath leave no sound after the hit point — which looks
    /// exactly like "said the word, still thinking". The grace period would then stretch the block to 5 s of
    /// silence, dilute <c>SpeechFraction</c> below threshold, and still drop the command. The caller checks for
    /// speech in the audio before the hit point: if present, the command is already underway and the normal
    /// sentence gap applies.
    /// </para>
    /// </summary>
    public void OnWakeHit(double nowSeconds, bool heardSpeechAtHit = false)
    {
        OnGateReleased(nowSeconds);
        _dirty = true;
        _awaitingSpeech = !heardSpeechAtHit;
    }

    /// <summary>The tick needs the current stream position to compute leftover; set it on every step.</summary>
    public void NoteNow(double nowSeconds) => _lastNow = nowSeconds;

    /// <summary>
    /// One above-threshold tick during the grace period: joins the current cluster (or starts one), and reaching
    /// <see cref="WakeOnsetMs"/> counts as speech onset.
    /// <para>
    /// On onset the block start moves back to the cluster start (with <see cref="OnsetPreRollSeconds"/> of
    /// margin): before it the block holds only the wake word tail, the tone and the user's thinking silence —
    /// nothing worth transcribing, and keeping it dilutes <c>SpeechFraction</c> until the command is dropped as
    /// noise; a long enough pause also pushes the block past <c>WindowSeconds</c> and cuts the command mid-way.
    /// </para>
    /// </summary>
    private void NoteOnsetCandidate(double nowSeconds, double tickSeconds)
    {
        if (_onsetLastLoud is null || nowSeconds - _onsetLastLoud.Value > OnsetBridgeSeconds)
        {
            // New cluster: this tick's energy window may be speech throughout, so the start is the window head and the duration starts at one window.
            _onsetClusterStart = nowSeconds - (EnergyWindowMs / 1000);
            _onsetVoicedSeconds = EnergyWindowMs / 1000;
        }
        else
        {
            _onsetVoicedSeconds += tickSeconds;
        }

        _onsetLastLoud = nowSeconds;

        if (_onsetVoicedSeconds * 1000 >= WakeOnsetMs)
        {
            _awaitingSpeech = false;
            Cursor = Math.Max(Cursor, _onsetClusterStart!.Value - OnsetPreRollSeconds);
            ResetOnset();
        }
    }

    private void ResetOnset()
    {
        _onsetClusterStart = null;
        _onsetVoicedSeconds = 0;
        _onsetLastLoud = null;
    }

    private double _lastNow;

    /// <summary>Stream time of the previous <see cref="Step"/>: voiced time accumulates over real tick intervals, not tick count.</summary>
    private double? _prevStep;
}

public enum SubmissionOutcome
{
    /// <summary>The worker accepted the block; the cursor may advance.</summary>
    Queued,

    /// <summary>Not worth a request (too short, no speech, evicted); the cursor may advance.</summary>
    Skipped,

    /// <summary>The backlog is full: the audio is still owed and must be retried.</summary>
    Busy,
}
