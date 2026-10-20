using Partner.Core.Agent.Llm;
using Partner.Voice.Asr;
using Partner.Voice.Tts;

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

    public LocalSettings Local { get; init; } = new();

    public ModelSettings Model { get; init; } = new();

    public DesktopSettings Desktop { get; init; } = new();

    public McpSettings Mcp { get; init; } = new();
    public SkillSettings Skills { get; init; } = new();

    public MemorySettings Memory { get; init; } = new();

    private readonly AgentsSettings? _agents;

    /// <summary>
    /// The main agent's prompt and the sub-agents it can delegate to. See <see cref="AgentsSettings"/>.
    /// <para>
    /// Unlike the other sections, an <b>absent</b> agents section means "leave it as it is", not "reset it": it has
    /// its own page and its own endpoint, and a client that PUTs the settings document without it (the settings
    /// page, a script, an older build) must not wipe the prompts and sub-agents somebody wrote there.
    /// <see cref="AgentsSpecified"/> is what lets the store tell the two apart.
    /// </para>
    /// </summary>
    public AgentsSettings Agents
    {
        get => _agents ?? new AgentsSettings();
        init => _agents = value;
    }

    /// <summary>Whether the document carried an agents section at all.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AgentsSpecified => _agents is not null;

    /// <summary>Whether the file tools exist, and where they may read.</summary>
    public FilesSettings Files { get; init; } = new();

    /// <summary>Whether the web tools exist, how long they may take, and which engines are switched off.</summary>
    public WebSettings Web { get; init; } = new();

    public static RuntimeSettings Default { get; } = new();
}

/// <summary>
/// 联网搜索的开关与边界。
/// <para>
/// 默认**打开**：这是本仓库里唯一默认开启的联网能力，也是唯一会让本机地址离开这台电脑的能力——
/// 每次检索都把查询词发给第三方搜索服务。这个取舍是本项目的所有者明确要求的，与设计文档里
/// 「默认关闭、由用户显式打开」相反（见 <c>WebOptions.Default</c> 的注释）。设置页必须把这一点写在开关旁边，
/// 而不是只给一个滑块。
/// </para>
/// <para>
/// 读取不受此开关影响：<c>web_fetch</c> 与检索同属一个能力，关掉开关，两个工具都不存在。
/// 「只取不搜」不是一种状态——没有检索结果，模型就只剩下自己猜地址这一条路。
/// </para>
/// </summary>
public sealed record WebSettings
{
    /// <summary>是否把联网能力暴露给模型。默认打开。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 整次检索的时间预算（秒），从**第一个引擎返回结果**开始计时，而不是从发请求开始：
    /// 一个慢引擎不该把预算吃掉，导致先答上来的结果反而来不及用。
    /// </summary>
    public int TimeBudgetSeconds { get; init; } = 12;

    /// <summary>答案里列出的结果条数上限。与向引擎索取的条数（<c>limit</c> 参数）是两回事。</summary>
    public int MaxResults { get; init; } = 8;

    /// <summary>
    /// http(s) 代理地址；空 = 直连。只在需要穿代理的环境里才有用，因此没有默认值。
    /// 非空时必须是绝对的 http/https 地址，保存时校验。
    /// </summary>
    public string? Proxy { get; init; }

    /// <summary>被用户手动关掉的引擎 id。不存在的 id 会被忽略（引擎可能在新版本里被移除）。</summary>
    public IReadOnlyList<string> DisabledEngines { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Value equality that actually compares the list. Same reason as <see cref="DesktopSettings.Equals"/>:
    /// a record holding a list compares it by reference, so two defaults would look different and every save
    /// would restart the consumer.
    /// </summary>
    public bool Equals(WebSettings? other) =>
        other is not null
        && Enabled == other.Enabled
        && TimeBudgetSeconds == other.TimeBudgetSeconds
        && MaxResults == other.MaxResults
        && string.Equals(Proxy, other.Proxy, StringComparison.Ordinal)
        && DisabledEngines.SequenceEqual(other.DisabledEngines, StringComparer.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(Enabled, TimeBudgetSeconds, MaxResults, Proxy, DisabledEngines.Count);

    /// <summary>The plain record the web layer reads. The dependency runs one way, so it cannot see this type.</summary>
    public Partner.Core.Agent.Web.WebOptions ToOptions() => new(
        Enabled,
        TimeBudgetSeconds,
        MaxResults,
        string.IsNullOrWhiteSpace(Proxy) ? null : Proxy,
        new HashSet<string>(DisabledEngines, StringComparer.Ordinal));
}

/// <summary>
/// The memory store's electrical wiring.
/// <para>
/// Embeddings default <b>off</b>, and stay off by default: the store works FTS-only (design doc's offline
/// degradation), and turning the local hashing provider on rewrites every future record into vectors the
/// current store did not have — a ranking change a person did not ask for. The provider here is local and
/// dependency-free (hashed character trigrams), so enabling costs no network call; a model-backed embedding
/// endpoint would be an external dependency and is deliberately not reachable through this setting.
/// </para>
/// </summary>
public sealed record MemorySettings
{
    /// <summary>
    /// Turn on the local hashing embedding provider. Off = exactly the pre-setting behaviour: recall scores
    /// run lexical + FTS only, and the vector component stays zero.
    /// </summary>
    public bool Embeddings { get; init; }

    public bool Equals(MemorySettings? other) => other is not null && Embeddings == other.Embeddings;

    public override int GetHashCode() => Embeddings.GetHashCode();
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
    /// 动作能走多远去够到目标：<c>foreground</c>（默认：该抢前台就抢）/ <c>auto</c>（能在后台完成就在后台，
    /// 不行再接管屏幕）/ <c>background</c>（绝不接管屏幕，做不到就拒绝并说明）。
    /// <para>
    /// 坐标点击、按键、打字走的 <c>SendInput</c> 是全局输入，只能抢前台、动真实指针；后台通道是浏览器 / Electron
    /// 程序的页面调试端口（要由 <c>desktop_launch</c> 启动）和点名控件的界面自动化 pattern。每次动作和启动时现读，
    /// 改了不用重启。
    /// </para>
    /// </summary>
    public string InputMode { get; init; } = Partner.Desktop.Input.InputModes.Foreground;

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
        && InputMode == other.InputMode
        && AllowedWindowTitles.SequenceEqual(other.AllowedWindowTitles, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Enabled, LeaseTtlMs, Approval, InputMode, AllowedWindowTitles.Count);
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

/// <summary>
/// MCP servers (design doc §2.6.3).
/// <para>
/// Off by default, and more strongly so than the desktop: an enabled server <b>starts a child process</b> the
/// moment the consumer starts, and that process gets whatever it is configured with. Enabling this is not
/// "let the model ask a tool" — it is "let something run".
/// </para>
/// <para>
/// So the tools reach the model through the same governance as everything else: an MCP tool is a
/// <c>ToolDecision.Ask</c> unless the gear says otherwise, and the settings below say which.
/// </para>
/// </summary>
public sealed record McpSettings
{
    /// <summary>Whether configured servers are started and their tools offered to the model.</summary>
    public bool Enabled { get; init; }

    /// <summary>The servers to start. Empty is the default, and means this capability does nothing at all.</summary>
    public IReadOnlyList<McpServerSettings> Servers { get; init; } = Array.Empty<McpServerSettings>();

    /// <summary>
    /// Which MCP tools may run without asking. Same reading as the desktop gear: a tool is asked for unless it
    /// is on the allow list, so an unconfigured server is still ask-everything rather than run-anything.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Value equality that compares the lists. Same reason as <see cref="DesktopSettings.Equals"/>: a record
    /// holding a list compares it by reference, so two defaults would look different and every settings save
    /// would restart the consumer — which here means killing child processes.
    /// </summary>
    public bool Equals(McpSettings? other) =>
        other is not null
        && Enabled == other.Enabled
        && Servers.SequenceEqual(other.Servers)
        && AllowedTools.SequenceEqual(other.AllowedTools, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Enabled, Servers.Count, AllowedTools.Count);
}

/// <summary>One MCP server, in the shape the settings file stores it.</summary>
public sealed record McpServerSettings : IEquatable<McpServerSettings>
{
    public required string Name { get; init; }

    public required string Command { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public string? WorkingDirectory { get; init; }

    /// <summary>Per-call timeout, seconds. A server that never answers must not park the turn.</summary>
    public int CallTimeoutSeconds { get; init; } = 30;

    public bool Equals(McpServerSettings? other) =>
        other is not null
        && Name == other.Name
        && Command == other.Command
        && Arguments.SequenceEqual(other.Arguments, StringComparer.Ordinal)
        && WorkingDirectory == other.WorkingDirectory
        && CallTimeoutSeconds == other.CallTimeoutSeconds;

    public override int GetHashCode() =>
        HashCode.Combine(Name, Command, Arguments.Count, WorkingDirectory, CallTimeoutSeconds);
}

/// <summary>
/// Which workspace skills are switched off.
/// <para>
/// Stored here rather than written into each <c>SKILL.md</c>: a skill is a file in the workspace that
/// somebody else may be editing, and "I do not want this one right now" is a decision about this installation
/// rather than a fact about the skill. Keeping them apart also means replacing a skill file does not silently
/// re-enable something a person deliberately turned off.
/// </para>
/// <para>
/// A deny list rather than an allow list, so a skill added later is on by default: a new skill that first had
/// to be switched on to do anything would be an unpleasant surprise.
/// </para>
/// </summary>
public sealed record SkillSettings
{
    /// <summary>Skill ids to skip when loading. An id that no longer exists is simply never read.</summary>
    public IReadOnlyList<string> Disabled { get; init; } = Array.Empty<string>();

    public bool Equals(SkillSettings? other) =>
        other is not null && Disabled.SequenceEqual(other.Disabled, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Disabled.Count);
}

/// <summary>Mirrors <c>VoiceInputSetting</c>.</summary>
public sealed record InputSettings
{
    public bool Enabled { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>Fixed RMS gate; must sit above the room noise floor or silence is never detected.</summary>
    public double SilenceThreshold { get; init; } = 0.03;

    /// <summary>唤醒命中时在输出设备上播放一声提示（叮）。跟输入同卡，因为它是唤醒门的附属反馈。</summary>
    public bool WakeTone { get; init; } = true;

    /// <summary>Absolute silence that ends the utterance.</summary>
    public double UtteranceGapMs { get; init; } = 1200;

    /// <summary>唤醒后等用户开口的宽限期（毫秒）。开口前不按判句间隔关会话。</summary>
    public double WakeTimeoutMs { get; init; } = 5000;

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

    /// <summary>Endpoint protocol: <see cref="AsrProtocols.ChatAudio"/> (default) or <see cref="AsrProtocols.OpenAi"/>.</summary>
    public string Protocol { get; init; } = AsrProtocols.ChatAudio;

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

    /// <summary>Endpoint protocol: <see cref="TtsProtocols.Native"/> (default) or <see cref="TtsProtocols.OpenAi"/>.</summary>
    public string Protocol { get; init; } = TtsProtocols.Native;

    /// <summary>OpenAI protocol's model name (e.g. <c>tts-1</c>); unused by native.</summary>
    public string? Model { get; init; }

    /// <summary>OpenAI protocol's voice (e.g. <c>alloy</c>); native clones instead via <see cref="SpeakerRefPath"/>.</summary>
    public string? Voice { get; init; }

    /// <summary>OpenAI protocol's speech rate; unused by native.</summary>
    public double Speed { get; init; } = 1.0;

    public string Lang { get; init; } = "zh";

    public int SampleRate { get; init; } = 24_000;

    /// <summary>Reference audio for voice cloning; read once when the client is built.</summary>
    public string? SpeakerRefPath { get; init; }

    public double TimeoutSeconds { get; init; } = 600;

    public int ChunkMs { get; init; } = 100;
}

/// <summary>
/// 本地语音引擎的进程管理（本地音频.md 的部署形态）。
/// <para>
/// 两个独立的开关，各自一个可执行文件：<c>tts</c> 管 OpenAI 兼容的 TTS 服务进程，
/// <c>kws</c> 管 KWS+VAD+ASR 的识别服务进程。关掉 = 杀进程 = 真正释放显存/内存——
/// 调研文档的核心教训是 ONNX 的内存池扩张后<b>不归还</b>，只有进程退出才可靠，所以「不占资源」的
/// 单位是进程，不是客户端对象。宿主自己不发请求，HTTP 客户端的地址仍由 <see cref="TtsSettings"/> /
/// <see cref="AsrSettings"/> 指向该引擎的端口。
/// </para>
/// <para>
/// 命令行按占位符 <c>{port}</c> 展开：引擎需要知道自己听哪个端口，宿主需要知道去连哪，
/// 一个字段同时喂两边，避免两处配置静默漂移。
/// </para>
/// </summary>
public sealed record LocalSettings
{
    /// <summary>托管本地 TTS 引擎进程。服务地址在 tts 段里配（协议 openai 时）。</summary>
    public LocalEngineSettings Tts { get; init; } = new();

    /// <summary>托管本地 KWS+VAD+ASR 引擎进程。服务地址在 asr 段里配。</summary>
    public LocalEngineSettings Kws { get; init; } = new();

    public bool Equals(LocalSettings? other) =>
        other is not null && Tts == other.Tts && Kws == other.Kws;

    public override int GetHashCode() => HashCode.Combine(Tts, Kws);
}

/// <summary>一个本地引擎进程：可执行文件、参数模板、端口。Command 空 = 不托管。</summary>
public sealed record LocalEngineSettings : IEquatable<LocalEngineSettings>
{
    public bool Enabled { get; init; }

    public string? Command { get; init; }

    /// <summary>参数模板；<c>{port}</c> 占位符换成下面端口的实际值再启动。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public string? WorkingDirectory { get; init; }

    public int Port { get; init; }

    /// <summary>进程启动后到第一批请求可以发出去的宽限；启动慢于它的引擎会吃连接失败与重试。</summary>
    public int ReadyTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// 唤醒词（显示名），引擎支持时整表替换：引擎端负责转拼音、落盘并重建识别流。仅 KWS 语义；
    /// 空 = 不下发，引擎保留自己的默认词表—— Bazı 引擎（纯 TTS）没有词表概念。
    /// </summary>
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    public bool Equals(LocalEngineSettings? other) =>
        other is not null
        && Enabled == other.Enabled
        && Command == other.Command
        && Arguments.SequenceEqual(other.Arguments, StringComparer.Ordinal)
        && WorkingDirectory == other.WorkingDirectory
        && Port == other.Port
        && ReadyTimeoutSeconds == other.ReadyTimeoutSeconds
        && Keywords.SequenceEqual(other.Keywords, StringComparer.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(Enabled, Command, Arguments.Count, WorkingDirectory, Port, ReadyTimeoutSeconds, Keywords.Count);
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

    /// <summary>
    /// 默认思考挡位，随 <c>reasoning_effort</c> 出现在每次补全里——包括用户没有为这一条消息另选的情况。
    /// <para>
    /// 它必须出现在 <see cref="ReasoningGears"/> 里（保存时校验），否则会话里的下拉预选不到任何一项。
    /// 单独一条消息可以用别的挡位，见 <see cref="InputItem.ReasoningEffort"/>。
    /// </para>
    /// <para>
    /// 值本身**不被**限制在 <see cref="ReasoningEfforts.Values"/>：端点可以自造词，拒掉一个服务端本会
    /// 认的词比原样发出去更糟。<c>off</c>（或留空）表示**不发这个字段**，而不是发一个值。
    /// </para>
    /// </summary>
    public string ReasoningEffort { get; init; } = ReasoningEfforts.Default;

    /// <summary>
    /// 挡位清单：UI 里能选的那些词，按展示顺序。用户在设置里增删改。
    /// <para>
    /// 这是**菜单**不是约束——<see cref="ReasoningEffort"/> 仍然是自由文本，删掉一个词只会让它不再出现在
    /// 菜单里，已经用这个词发出去的消息不受影响。清单为空时 UI 回退到内置默认清单，否则会给出空菜单。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ReasoningGears { get; init; } = ReasoningEfforts.Values;

    /// <summary>
    /// 挡位的显示文字：词 → 界面上写的字。只影响显示，发给模型的永远是词本身。
    /// 没有登记的词由 UI 回退到内置名称（常见六个词）或原词。
    /// </summary>
    public IReadOnlyDictionary<string, string> ReasoningLabels { get; init; } = new Dictionary<string, string>();
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

    public string ReasoningEffort { get; init; } = ReasoningEfforts.Default;

    /// <summary>The gears the UI offers, in order. See <see cref="ModelSettings.ReasoningGears"/>.</summary>
    public IReadOnlyList<string> ReasoningGears { get; init; } = ReasoningEfforts.Values;

    /// <summary>
    /// 挡位的显示文字：词 → 界面上写的字。只影响显示，发给模型的永远是词本身。
    /// 没有登记的词由 UI 回退到内置名称（常见六个词）或原词。
    /// </summary>
    public IReadOnlyDictionary<string, string> ReasoningLabels { get; init; } = new Dictionary<string, string>();
}

public sealed record SettingsView
{
    public InputSettings Input { get; init; } = new();

    public OutputSettings Output { get; init; } = new();

    public AsrSettings Asr { get; init; } = new();

    public TtsSettings Tts { get; init; } = new();

    public LocalSettings Local { get; init; } = new();

    public ModelSettingsView Model { get; init; } = new();

    public DesktopSettings Desktop { get; init; } = new();

    public McpSettings Mcp { get; init; } = new();

    public SkillSettings Skills { get; init; } = new();

    public MemorySettings Memory { get; init; } = new();

    /// <summary>Sub-agent API keys are masked here: the response says whether one is stored, never what it is.</summary>
    public AgentsSettings Agents { get; init; } = new();

    public FilesSettings Files { get; init; } = new();

    public WebSettings Web { get; init; } = new();

    public static SettingsView From(RuntimeSettings settings) => new()
    {
        Input = settings.Input,
        Output = settings.Output,
        Asr = settings.Asr,
        Tts = settings.Tts,
        Local = settings.Local,
        Model = new ModelSettingsView
        {
            BaseUrl = settings.Model.BaseUrl,
            Model = settings.Model.Model,
            ApiKeySet = !string.IsNullOrEmpty(settings.Model.ApiKey),
            TimeoutSeconds = settings.Model.TimeoutSeconds,
            ContextTokens = settings.Model.ContextTokens,
            MaxTokens = settings.Model.MaxTokens,
            SupportsImages = settings.Model.SupportsImages,
            ReasoningEffort = ReasoningEfforts.Normalize(settings.Model.ReasoningEffort),
            // Normalized here too, so the list the editor writes back is exactly what it was handed.
            ReasoningGears = settings.Model.ReasoningGears
                .Select(ReasoningEfforts.Normalize)
                .Where(gear => gear.Length > 0)
                .ToArray(),
            ReasoningLabels = settings.Model.ReasoningLabels,
        },
        // Without these the settings response drops the sections, and the UI — which refills its form from the
        // response — flips the switches back to off the moment it is saved.
        Desktop = settings.Desktop,

        Mcp = settings.Mcp,

        // Without this the settings response drops the section, and the UI — which refills its form from the
        // response — forgets which skills were switched off the moment it is saved.
        Skills = settings.Skills,

        Memory = settings.Memory,

        // Without these the settings response drops the sections, and the UI — which refills its form from the
        // response — would write the defaults back on its next save and erase the agents and the file whitelist.
        Agents = settings.Agents with
        {
            SubAgents = settings.Agents.SubAgents
                .Select(agent => agent with
                {
                    Model = agent.Model is null
                        ? null
                        : agent.Model with { ApiKey = null, ApiKeySet = !string.IsNullOrEmpty(agent.Model.ApiKey) },
                })
                .ToList(),
        },
        Files = settings.Files,

        // Same reason as the two above: absent from the response means the UI writes the default back on its
        // next save, which would silently re-enable an engine somebody switched off and drop the proxy.
        Web = settings.Web,
    };
}
