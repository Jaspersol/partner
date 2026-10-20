namespace Partner.Voice.Tts;

/// <summary>TTS protocol: raw PCM stream from the native service (default) or the OpenAI standard speech endpoint.</summary>
public static class TtsProtocols
{
    /// <summary>Native endpoint: <c>POST /tts</c>, response is a raw float32 PCM stream.</summary>
    public const string Native = "native";

    /// <summary>OpenAI standard: <c>POST /audio/speech</c>, response is a complete WAV.</summary>
    public const string OpenAi = "openai";

    /// <summary>
    /// Local speech engine (engines/tts). Wire format is the same as <see cref="OpenAi"/> (<c>/audio/speech</c>), but
    /// model, voice and speed are fixed by the engine and need no settings; a separate value lets the UI offer only
    /// "local / generic standard endpoint".
    /// </summary>
    public const string Local = "local";

    public static IReadOnlyList<string> Values { get; } = new[] { Native, OpenAi, Local };

    public static string Normalize(string? value) =>
        Values.FirstOrDefault(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase)) ?? Native;
}

/// <summary>
/// TTS service connection parameters.
/// </summary>
public sealed record TtsOptions
{
    /// <summary>Service base URL, e.g. <c>http://127.0.0.1:45102</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Protocol: <see cref="TtsProtocols.Native"/> (default) or <see cref="TtsProtocols.OpenAi"/>.</summary>
    public string Protocol { get; init; } = TtsProtocols.Native;

    /// <summary>OpenAI protocol model name (e.g. <c>tts-1</c>); unused by the native protocol.</summary>
    public string? Model { get; init; }

    /// <summary>OpenAI protocol voice name (e.g. <c>alloy</c>); the native protocol clones from <see cref="SpeakerRefPath"/>.</summary>
    public string? Voice { get; init; }

    /// <summary>OpenAI protocol speaking speed; unused by the native protocol. &lt;=0 is treated as 1.0.</summary>
    public double Speed { get; init; } = 1.0;

    public string Lang { get; init; } = "zh";

    public int SampleRate { get; init; } = 24_000;

    /// <summary>Reference audio for voice cloning; read and base64-encoded **once** at construction.</summary>
    public string? SpeakerRefPath { get; init; }

    /// <summary>
    /// What the reference recording actually says. Voice cloning uses it to align bit-for-bit with the reference
    /// audio's codec; empty = zero-shot cloning (voice vector only). A wrong value is worse than empty: it
    /// synthesizes gibberish.
    /// </summary>
    public string? SpeakerRefText { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(600);

    /// <summary>Target chunk size handed to the player.</summary>
    public int ChunkMs { get; init; } = 100;
}
