using System.Text.RegularExpressions;

namespace Partner.Voice.Asr;

/// <summary>
/// ASR 服务（Qwen3-ASR OpenAI 兼容端点）连接参数（设计文档 §2.8.10）。
/// </summary>
public sealed record AsrOptions
{
    /// <summary>服务基址，例如 <c>http://127.0.0.1:45101</c>。</summary>
    public required string BaseUrl { get; init; }

    public string Model { get; init; } = "qwen3-asr";

    /// <summary>Prefilled as the assistant turn so the model skips emitting metadata.</summary>
    public string? Language { get; init; } = "Chinese";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Deliberately short: a stalled block is retried whole, and a long stall would overflow the audio ring.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public int SampleRate { get; init; } = 16_000;
}

/// <summary>
/// ASR 输出元数据处理：模型未预填时会先吐 <c>language X&lt;asr_text&gt;</c> 前缀。
/// </summary>
public static class AsrMetadata
{
    private static readonly Regex Prefix = new(
        @"^\s*language\s+[^\s<]+<asr_text>\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Removes a leading "language X&lt;asr_text&gt;" prefix (the model emits it when not prefilled).</summary>
    public static string Strip(string text) => Prefix.Replace(text, string.Empty);
}

/// <summary>读取超时（或连接超时）：整块音频需要重试。</summary>
public sealed class AsrTimeoutException : Exception
{
    public AsrTimeoutException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>ASR 服务返回了非 2xx 状态码。</summary>
public sealed class AsrRequestException : Exception
{
    public AsrRequestException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
