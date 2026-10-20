namespace Partner.Voice.Tts;

/// <summary>TTS 协议：自建服务的裸 PCM 流（默认）还是 OpenAI 标准的 speech 端点。</summary>
public static class TtsProtocols
{
    /// <summary>自建端点：<c>POST /tts</c>，响应是裸 float32 PCM 流。</summary>
    public const string Native = "native";

    /// <summary>OpenAI 标准：<c>POST /audio/speech</c>，响应是完整 WAV。</summary>
    public const string OpenAi = "openai";

    /// <summary>
    /// 本机朗读引擎（engines/tts）。线上格式和 <see cref="OpenAi"/> 一样（<c>/audio/speech</c>），但模型、音色、
    /// 语速都由引擎自己定，设置里不用填；单独成一个值，是为了让界面只摆「本地 / 通用标准接口」两个选项。
    /// </summary>
    public const string Local = "local";

    public static IReadOnlyList<string> Values { get; } = new[] { Native, OpenAi, Local };

    public static string Normalize(string? value) =>
        Values.FirstOrDefault(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase)) ?? Native;
}

/// <summary>
/// TTS 服务连接参数（设计文档 §2.8.10）。
/// </summary>
public sealed record TtsOptions
{
    /// <summary>服务基址，例如 <c>http://127.0.0.1:45102</c>。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>协议：<see cref="TtsProtocols.Native"/>（默认）或 <see cref="TtsProtocols.OpenAi"/>。</summary>
    public string Protocol { get; init; } = TtsProtocols.Native;

    /// <summary>OpenAI 协议的模型名（如 <c>tts-1</c>）；native 协议不用。</summary>
    public string? Model { get; init; }

    /// <summary>OpenAI 协议的音色名（如 <c>alloy</c>）；native 协议用 <see cref="SpeakerRefPath"/> 克隆。</summary>
    public string? Voice { get; init; }

    /// <summary>OpenAI 协语的语速；native 协议不用。&lt;=0 按 1.0 处理。</summary>
    public double Speed { get; init; } = 1.0;

    public string Lang { get; init; } = "zh";

    public int SampleRate { get; init; } = 24_000;

    /// <summary>Reference audio for voice cloning; read and base64-encoded **once** at construction.</summary>
    public string? SpeakerRefPath { get; init; }

    /// <summary>
    /// 参考录音里实际说的话。克隆音色用它和参考音频的 codec 逐位对齐；留空 = 零样本克隆
    /// （只用音色向量）。填错比留空更糟：会直接合成出乱读。
    /// </summary>
    public string? SpeakerRefText { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(600);

    /// <summary>Target chunk size handed to the player.</summary>
    public int ChunkMs { get; init; } = 100;
}
