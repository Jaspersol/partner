namespace Partner.Voice.Settings;

/// <summary>Voice input settings (design doc §2.8.12 — control is settings, not buttons).</summary>
public sealed record VoiceInputSetting
{
    public bool Enabled { get; init; }

    /// <summary>Input device name (partial, case/space-insensitive match) or null for the system default.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Fixed RMS gate. Must sit above the room noise floor or silence is never detected (R11).</summary>
    public double SilenceThreshold { get; init; } = 0.03;

    /// <summary>G2: absolute silence that ends the utterance.</summary>
    public double UtteranceGapMs { get; init; } = 1200;

    /// <summary>G1: silence that ends a sentence (streaming mode only).</summary>
    public double SentenceGapMs { get; init; } = 450;

    /// <summary>Longest block sent in one request; also sizes the ring (capacity = this + 2s).</summary>
    public double WindowSeconds { get; init; } = 8;

    /// <summary>Streaming ASR (one sentence per request) versus batch (one request per utterance).</summary>
    public bool AsrStream { get; init; }

    /// <summary>Forced split length for text with no punctuation.</summary>
    public int MaxSplitChars { get; init; } = 40;

    public static VoiceInputSetting Default { get; } = new();
}

/// <summary>Voice output settings (design doc §2.8.11 — the output half exists only when enabled).</summary>
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

    public static VoiceOutputSetting Default { get; } = new();
}
