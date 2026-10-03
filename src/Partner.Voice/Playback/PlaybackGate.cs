namespace Partner.Voice.Playback;

/// <summary>
/// The seven signals the gate waits on (design doc §2.8.4 I3). Six of them are "is anything still in
/// flight"; the seventh — a submitted-but-not-yet-absorbed block — is the one that cannot be inferred from
/// the others, and leaving it out reopens the gate in the middle of the segment's own transcription, so the
/// assistant transcribes itself.
/// </summary>
public sealed record GateConditions(
    bool PendingBlock,
    bool IsPlaying,
    bool PendingAudio,
    bool SentencesEmpty,
    bool SegmenterEmpty,
    bool TtsBusy,
    bool AsrBusy)
{
    public bool AllClear =>
        !PendingBlock && !IsPlaying && !PendingAudio && SentencesEmpty && SegmenterEmpty && !TtsBusy && !AsrBusy;

    /// <summary>Why the gate is still closed — without this, "it never reopened" is undiagnosable.</summary>
    public IReadOnlyList<string> Blockers
    {
        get
        {
            var blockers = new List<string>();
            if (PendingBlock)
            {
                blockers.Add("pending-block");
            }

            if (IsPlaying)
            {
                blockers.Add("playing");
            }

            if (PendingAudio)
            {
                blockers.Add("pending-audio");
            }

            if (!SentencesEmpty)
            {
                blockers.Add("sentences");
            }

            if (!SegmenterEmpty)
            {
                blockers.Add("segmenter");
            }

            if (TtsBusy)
            {
                blockers.Add("tts-busy");
            }

            if (AsrBusy)
            {
                blockers.Add("asr-busy");
            }

            return blockers;
        }
    }
}

/// <summary>
/// The playback gate (design doc §2.8.2): while the assistant is speaking, input is discarded wholesale —
/// the assistant's own voice would otherwise be transcribed. Discarding must be visible: a silent drop reads
/// exactly like a dead pipeline.
/// </summary>
public sealed class PlaybackGate
{
    public bool Engaged { get; private set; }

    /// <summary>Audio discarded while gated, in seconds.</summary>
    public double SkippedSeconds { get; private set; }

    /// <summary>True when the discarded audio contained speech (the case worth reporting).</summary>
    public bool SkippedHadSpeech { get; private set; }

    public int ReleaseAttempts { get; private set; }

    public string? LastBlockers { get; private set; }

    public void Engage()
    {
        Engaged = true;
    }

    /// <summary>Returns true when every condition is clear; otherwise records why it is still closed.</summary>
    public bool TryRelease(GateConditions conditions, double nowSeconds)
    {
        ArgumentNullException.ThrowIfNull(conditions);

        if (!Engaged)
        {
            return true;
        }

        ReleaseAttempts++;

        if (!conditions.AllClear)
        {
            LastBlockers = string.Join(",", conditions.Blockers);
            return false;
        }

        Engaged = false;
        LastBlockers = null;
        LastReleasedAt = nowSeconds;
        return true;
    }

    /// <summary>Records the audio dropped while gated.</summary>
    public void NoteSkipped(double seconds, bool hadSpeech)
    {
        if (seconds <= 0)
        {
            return;
        }

        SkippedSeconds += seconds;
        SkippedHadSpeech |= hadSpeech;
    }

    /// <summary>Resets the per-segment counters after the gate has reopened.</summary>
    public void ResetSkipped()
    {
        SkippedSeconds = 0;
        SkippedHadSpeech = false;
    }

    public double? LastReleasedAt { get; private set; }
}
