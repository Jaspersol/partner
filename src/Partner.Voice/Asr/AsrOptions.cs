using System.Text.RegularExpressions;

namespace Partner.Voice.Asr;

/// <summary>ASR protocol: Qwen chat-audio (default), the OpenAI standard endpoint, or a local sherpa-onnx service.</summary>
public static class AsrProtocols
{
    /// <summary>Qwen3-ASR: <c>POST /v1/chat/completions</c> with inline base64 audio.</summary>
    public const string ChatAudio = "chat-audio";

    /// <summary>OpenAI standard: <c>POST /audio/transcriptions</c> with a multipart WAV upload.</summary>
    public const string OpenAi = "openai";

    /// <summary>Local sherpa-onnx service: <c>POST /recognize</c> with raw float32 PCM, returning <c>{ "text": … }</c>.</summary>
    public const string SenseVoice = "sensevoice";

    public static IReadOnlyList<string> Values { get; } = new[] { ChatAudio, OpenAi, SenseVoice };

    public static string Normalize(string? value) =>
        Values.FirstOrDefault(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase)) ?? ChatAudio;
}

/// <summary>
/// ASR service connection parameters.
/// </summary>
public sealed record AsrOptions
{
    /// <summary>Service base URL, e.g. <c>http://127.0.0.1:45101</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Protocol: <see cref="AsrProtocols.ChatAudio"/> (default) or <see cref="AsrProtocols.OpenAi"/>.</summary>
    public string Protocol { get; init; } = AsrProtocols.ChatAudio;

    public string Model { get; init; } = "qwen3-asr";

    /// <summary>Prefilled as the assistant turn so the model skips emitting metadata.</summary>
    public string? Language { get; init; } = "Chinese";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Deliberately short: a stalled block is retried whole, and a long stall would overflow the audio ring.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public int SampleRate { get; init; } = 16_000;
}

/// <summary>
/// ASR output metadata handling: without a prefill the model emits a <c>language X&lt;asr_text&gt;</c> prefix.
/// </summary>
public static class AsrMetadata
{
    private static readonly Regex Prefix = new(
        @"^\s*language\s+[^\s<]+<asr_text>\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Removes a leading "language X&lt;asr_text&gt;" prefix (the model emits it when not prefilled).</summary>
    public static string Strip(string text) => Prefix.Replace(text, string.Empty);
}

/// <summary>Read timeout (or connect timeout): the whole audio block must be retried.</summary>
public sealed class AsrTimeoutException : Exception
{
    public AsrTimeoutException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>The ASR service returned a non-2xx status code.</summary>
public sealed class AsrRequestException : Exception
{
    public AsrRequestException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
