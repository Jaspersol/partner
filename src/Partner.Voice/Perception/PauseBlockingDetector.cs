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
/// The pause/blocking state machine (design doc §2.8.2 and §2.8.3). Time and energy are passed in, so the
/// whole thing is deterministic and testable without audio hardware.
/// <para>
/// The two knobs mean "the pause you actually make": the measured silence is short by one energy window
/// plus the poll cadence, so a fixed offset is added back.
/// </para>
/// </summary>
public sealed class PauseBlockingDetector
{
    /// <summary>Compensation for the energy window + poll cadence (design doc: 100 ms).</summary>
    public const double SilenceOffsetMs = 100;

    private readonly VoiceInputSetting _settings;
    private readonly double _sentenceGapMs;
    private readonly double _utteranceGapMs;
    private readonly double _windowSeconds;

    private double? _quietSince;
    private bool _dirty;
    private bool _called;
    private bool _leftover;
    private bool? _heldPause;

    public PauseBlockingDetector(VoiceInputSetting settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sentenceGapMs = settings.SentenceGapMs;
        _utteranceGapMs = settings.UtteranceGapMs;
        _windowSeconds = settings.WindowSeconds;
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

    public bool HasSpeechHeard => _dirty;

    /// <summary>One tick. <paramref name="nowSeconds"/> is the stream clock, <paramref name="energy"/> the RMS.</summary>
    public PauseDecision Step(double nowSeconds, double energy)
    {
        // The tick time is recorded here so callers cannot forget it: it is what decides whether a
        // submission left audio behind.
        _lastNow = nowSeconds;

        var quiet = energy < _settings.SilenceThreshold;
        double silenceMs;

        // Step order matters (design doc §2.8.3): an owed block is retried before anything moves the cursor.
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

        // G1 (streaming only): one complete sentence per request.
        if (_settings.AsrStream
            && quiet
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

        // G2: the utterance is over in both modes.
        if (quiet && silenceMs >= _utteranceGapMs && _dirty && _heldPause is null)
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
        _quietSince = null;
        _called = false;
    }

    /// <summary>Called when the gate reopens: the audio spoken during playback is discarded wholesale.</summary>
    public void OnGateReleased(double nowSeconds)
    {
        Cursor = nowSeconds;
        _heldPause = null;
        _leftover = false;
        _quietSince = null;
        _called = false;
    }

    /// <summary>The tick needs the current stream position to compute leftover; set it on every step.</summary>
    public void NoteNow(double nowSeconds) => _lastNow = nowSeconds;

    private double _lastNow;
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
