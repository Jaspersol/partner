namespace Partner.Voice.Tts;

/// <summary>
/// TTS 服务连接参数（设计文档 §2.8.10）。
/// </summary>
public sealed record TtsOptions
{
    /// <summary>服务基址，例如 <c>http://127.0.0.1:45102</c>。</summary>
    public required string BaseUrl { get; init; }

    public string Lang { get; init; } = "zh";

    public int SampleRate { get; init; } = 24_000;

    /// <summary>Reference audio for voice cloning; read and base64-encoded **once** at construction.</summary>
    public string? SpeakerRefPath { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(600);

    /// <summary>Target chunk size handed to the player.</summary>
    public int ChunkMs { get; init; } = 100;
}
