using Partner.Voice.Asr;

namespace Partner.Voice.Settings;

/// <summary>Voice input settings (control is settings, not buttons).</summary>
public sealed record VoiceInputSetting
{
    public bool Enabled { get; init; }

    /// <summary>
    /// Wake gate (local KWS): when non-null the microphone keeps listening but <b>does not submit to ASR</b>
    /// until <see cref="IWakeChecker"/> reports a hit. null = no gate (plain voice input).
    /// </summary>
    public IWakeChecker? Wake { get; init; }

    /// <summary>
    /// Tone played on the output device at the wake hit. null/off = enter the session silently.
    /// No callback dependency: the host hands the sink to <see cref="VoiceInputHalf"/>, which enqueues directly on the hit tick.
    /// </summary>
    public bool WakeTone { get; init; } = true;

    /// <summary>Input device name (partial, case/space-insensitive match) or null for the system default.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Fixed RMS gate. Must sit above the room noise floor or silence is never detected.</summary>
    public double SilenceThreshold { get; init; } = 0.003;

    /// <summary>
    /// Speaking over the assistant stops it. Off by default: without echo cancellation a speaker in the room is
    /// heard by the microphone, and the assistant would interrupt itself. Meant for headphones.
    /// </summary>
    public bool BargeIn { get; init; }

    /// <summary>How long sound well above the silence threshold must last, while the assistant speaks, to count as interrupting.</summary>
    public double BargeInMs { get; init; } = 600;

    /// <summary>G2: absolute silence that ends the utterance.</summary>
    public double UtteranceGapMs { get; init; } = 1200;

    /// <summary>
    /// Maximum silence after a wake hit, before the user speaks, that keeps the session open.
    /// <para>
    /// Separate from <see cref="UtteranceGapMs"/>: the silence between the tone and speech is "thinking what to say",
    /// not "finished talking". Closing the session on the sentence gap shuts the gate before the user speaks — everything
    /// said afterwards only feeds the wake probe and never reaches ASR. Once real speech is heard, the normal
    /// <see cref="UtteranceGapMs"/> sentence logic resumes.
    /// </para>
    /// </summary>
    public double WakeTimeoutMs { get; init; } = 5000;

    /// <summary>G1: silence that ends a sentence (streaming mode only).</summary>
    public double SentenceGapMs { get; init; } = 450;

    /// <summary>Longest block sent in one request; also sizes the ring (capacity = this + 2s).</summary>
    public double WindowSeconds { get; init; } = 8;

    /// <summary>Streaming ASR (one sentence per request) versus batch (one request per utterance).</summary>
    public bool AsrStream { get; init; }

    public static VoiceInputSetting Default { get; } = new();
}

/// <summary>Voice output settings (the output half exists only when enabled).</summary>
public sealed record VoiceOutputSetting
{
    public bool Enabled { get; init; }

    public string? DeviceId { get; init; }

    public string? Voice { get; init; }

    public double Speed { get; init; } = 1.0;

    /// <summary>Silence inserted between segments; forced to 0 when a cloning reference is used.</summary>
    public double GapMs { get; init; }

    /// <summary>Device-buffer drain hold-off after the last write.</summary>
    public double DrainMs { get; init; } = 400;

    /// <summary>Playback sample rate; must match what the TTS service emits.</summary>
    public int SampleRate { get; init; } = 24_000;

    /// <summary>While playing, input is discarded so the assistant never transcribes itself.</summary>
    public bool MuteDuringTts { get; init; } = true;

    /// <summary>Forced split length for a sentence that carries no punctuation.</summary>
    public int MaxSplitChars { get; init; } = 40;

    /// <summary>Shortest fragment spoken as its own sentence; shorter pieces are read with what follows.</summary>
    public int MinSentenceChars { get; init; } = 10;

    public static VoiceOutputSetting Default { get; } = new();
}
