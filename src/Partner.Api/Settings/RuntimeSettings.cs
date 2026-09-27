namespace Partner.Api.Settings;

/// <summary>
/// The runtime's editable configuration (design doc §2.5.5, §2.8.11–§2.8.12, §2.9.5).
/// <para>
/// Field names mirror the option records that actually consume them — <c>Partner.Voice.Asr.AsrOptions</c>,
/// <c>Partner.Voice.Tts.TtsOptions</c>, <c>Partner.Core.Agent.Llm.HttpLlmOptions</c> — so the correspondence
/// is checkable by reading the two side by side rather than by remembering a mapping.
/// </para>
/// </summary>
public sealed record RuntimeSettings
{
    public InputSettings Input { get; init; } = new();

    public OutputSettings Output { get; init; } = new();

    public AsrSettings Asr { get; init; } = new();

    public TtsSettings Tts { get; init; } = new();

    public ModelSettings Model { get; init; } = new();

    public DesktopSettings Desktop { get; init; } = new();

    public static RuntimeSettings Default { get; } = new();
}

/// <summary>
/// 桌面操控（M9）的开关与边界。
/// <para>
/// 默认**关闭**：打开之后模型就能移动指针、发按键，这是本仓库里唯一不可逆的能力。即使打开，动作类工具
/// 也走人工批准（治理的 ask 列表），所以「打开」只是让模型能**问**，不是让它能**做**。
/// </para>
/// </summary>
public sealed record DesktopSettings
{
    /// <summary>是否把桌面能力暴露给模型。</summary>
    public bool Enabled { get; init; }

    /// <summary>租约基准有效期（毫秒）：观察之后多久之内还能据此动作。受策略的上下限约束。</summary>
    public int LeaseTtlMs { get; init; } = 15_000;

    /// <summary>
    /// 审批挡位：<c>none</c>（不审批）/ <c>dangerous</c>（危险指令审批）/ <c>all</c>（全部审批）。
    /// <para>
    /// 「危险」指离开 GUI 的能力：执行命令、启动程序、写剪贴板。点击与打字留在人能看见的界面里，
    /// 所以中间挡位放它们过去——参考项目举的危险例子是「删除文件」，不是「按了个按钮」。
    /// </para>
    /// </summary>
    public string Approval { get; init; } = ApprovalGears.All;

    /// <summary>允许动作的窗口标题子串（不区分大小写）；空表示不限。</summary>
    public IReadOnlyList<string> AllowedWindowTitles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Value equality that actually compares the titles. A record holding a list compares it by reference, so
    /// two defaults — each with its own empty array — would look different, and every settings save would
    /// restart the consumer (resetting the counters of a turn in flight).
    /// </summary>
    public bool Equals(DesktopSettings? other) =>
        other is not null
        && Enabled == other.Enabled
        && LeaseTtlMs == other.LeaseTtlMs
        && Approval == other.Approval
        && AllowedWindowTitles.SequenceEqual(other.AllowedWindowTitles, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Enabled, LeaseTtlMs, Approval, AllowedWindowTitles.Count);
}

/// <summary>The three approval gears, in the order the UI offers them.</summary>
public static class ApprovalGears
{
    /// <summary>Nothing is asked; the model acts as soon as it is enabled.</summary>
    public const string None = "none";

    /// <summary>Only actions that leave the GUI are asked: commands, launching, clipboard writes.</summary>
    public const string Dangerous = "dangerous";

    /// <summary>Every desktop action waits for a human. The default.</summary>
    public const string All = "all";

    public static IReadOnlyList<string> Values { get; } = new[] { None, Dangerous, All };

    public static string Normalize(string? value) =>
        Values.FirstOrDefault(gear => string.Equals(gear, value, StringComparison.OrdinalIgnoreCase)) ?? All;
}

/// <summary>Mirrors <c>VoiceInputSetting</c>.</summary>
public sealed record InputSettings
{
    public bool Enabled { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>Fixed RMS gate; must sit above the room noise floor or silence is never detected.</summary>
    public double SilenceThreshold { get; init; } = 0.03;

    /// <summary>Absolute silence that ends the utterance.</summary>
    public double UtteranceGapMs { get; init; } = 1200;

    /// <summary>Silence that ends a sentence (streaming mode only).</summary>
    public double SentenceGapMs { get; init; } = 450;

    public double WindowSeconds { get; init; } = 8;

    /// <summary>Streaming ASR (one sentence per request) versus batch (one request per utterance).</summary>
    public bool AsrStream { get; init; }

    /// <summary>Forced split length for text with no punctuation.</summary>
    public int MaxSplitChars { get; init; } = 40;
}

/// <summary>Mirrors <c>VoiceOutputSetting</c>.</summary>
public sealed record OutputSettings
{
    public bool Enabled { get; init; }

    public string? DeviceId { get; init; }

    public string? Voice { get; init; }

    public double Speed { get; init; } = 1.0;

    public double GapMs { get; init; }

    public double DrainMs { get; init; } = 400;

    public int SampleRate { get; init; } = 24_000;

    public bool MuteDuringTts { get; init; } = true;
}

/// <summary>Mirrors <c>AsrOptions</c>. An absent <see cref="BaseUrl"/> means speech input is not configured.</summary>
public sealed record AsrSettings
{
    public string? BaseUrl { get; init; }

    public string Model { get; init; } = "qwen3-asr";

    /// <summary>Prefilled as the assistant turn so the model skips emitting metadata.</summary>
    public string? Language { get; init; } = "Chinese";

    public double ConnectTimeoutMs { get; init; } = 2000;

    public double ReadTimeoutMs { get; init; } = 3000;

    public int SampleRate { get; init; } = 16_000;
}

/// <summary>Mirrors <c>TtsOptions</c>. An absent <see cref="BaseUrl"/> means speech output is not configured.</summary>
public sealed record TtsSettings
{
    public string? BaseUrl { get; init; }

    public string Lang { get; init; } = "zh";

    public int SampleRate { get; init; } = 24_000;

    /// <summary>Reference audio for voice cloning; read once when the client is built.</summary>
    public string? SpeakerRefPath { get; init; }

    public double TimeoutSeconds { get; init; } = 600;

    public int ChunkMs { get; init; } = 100;
}

/// <summary>Mirrors <c>HttpLlmOptions</c>. The key is write-only; see <see cref="ModelSettingsView"/>.</summary>
public sealed record ModelSettings
{
    public string? BaseUrl { get; init; }

    public string? Model { get; init; }

    /// <summary>
    /// Never serialised into a response: the settings endpoint is reachable by anything holding the bearer
    /// token, and a key echoed back is a key leaked. On write, absent means "keep", empty means "clear".
    /// </summary>
    public string? ApiKey { get; init; }

    public double TimeoutSeconds { get; init; } = 120;

    /// <summary>The model's context window in tokens. The prompt budget is this minus <see cref="MaxTokens"/>.</summary>
    public int ContextTokens { get; init; } = 32768;

    /// <summary>Output ceiling for one completion.</summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>
    /// Whether this model can look at pictures.
    /// <para>
    /// On, the applications UI Automation cannot see (Electron, games, canvases) are handled by **showing the
    /// model a screenshot** rather than by OCR: a vision model reads the UI better than any local recogniser.
    /// Off, the screenshot tool is not registered at all — sending an image to a text-only model fails the
    /// request, so this has to be a deliberate claim about the configured model.
    /// </para>
    /// </summary>
    public bool SupportsImages { get; init; }
}

/// <summary>What the API returns: the same shape, minus the secret, plus whether one is stored.</summary>
public sealed record ModelSettingsView
{
    public string? BaseUrl { get; init; }


    public string? Model { get; init; }

    public bool ApiKeySet { get; init; }

    public double TimeoutSeconds { get; init; } = 120;

    public int ContextTokens { get; init; } = 32768;

    public int MaxTokens { get; init; } = 4096;

    public bool SupportsImages { get; init; }
}

public sealed record SettingsView
{
    public InputSettings Input { get; init; } = new();

    public OutputSettings Output { get; init; } = new();

    public AsrSettings Asr { get; init; } = new();

    public TtsSettings Tts { get; init; } = new();

    public ModelSettingsView Model { get; init; } = new();

    public DesktopSettings Desktop { get; init; } = new();

    public static SettingsView From(RuntimeSettings settings) => new()
    {
        Input = settings.Input,
        Output = settings.Output,
        Asr = settings.Asr,
        Tts = settings.Tts,
        Model = new ModelSettingsView
        {
            BaseUrl = settings.Model.BaseUrl,
            Model = settings.Model.Model,
            ApiKeySet = !string.IsNullOrEmpty(settings.Model.ApiKey),
            TimeoutSeconds = settings.Model.TimeoutSeconds,
            ContextTokens = settings.Model.ContextTokens,
            MaxTokens = settings.Model.MaxTokens,
            SupportsImages = settings.Model.SupportsImages,
        },

        // Without this the settings response drops the section, and the UI — which refills its form from the
        // response — flips the switch back to off the moment it is saved.
        Desktop = settings.Desktop,
    };
}
