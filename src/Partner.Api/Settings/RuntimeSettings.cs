using Partner.Core.Agent.Llm;
using Partner.Voice.Asr;
using Partner.Voice.Tts;

namespace Partner.Api.Settings;

/// <summary>
/// The runtime's editable configuration.
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
/// The web search switch and its bounds.
/// <para>
/// Defaults to <b>on</b>: this is the only capability in the repository that is on by default, and the only one
/// that sends local data off this computer — every search sends the query to a third-party search service.
/// </para>
/// <para>
/// Reading is governed by the same switch: <c>web_fetch</c> and search are one capability, so turning it off
/// removes both tools. "Fetch only, no search" is not a state — without search results the model would have to
/// guess addresses.
/// </para>
/// </summary>
public sealed record WebSettings
{
    /// <summary>Whether the web capability is exposed to the model. On by default.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Time budget for a whole search, in seconds, counted from the <b>first engine result</b> rather than from
    /// the request: a slow engine must not eat the budget so the earliest answers arrive too late to use.
    /// </summary>
    public int TimeBudgetSeconds { get; init; } = 12;

    /// <summary>Cap on results listed in the answer. Separate from the count requested from the engine (<c>limit</c>).</summary>
    public int MaxResults { get; init; } = 8;

    /// <summary>
    /// http(s) proxy address; empty = direct. Only useful where a proxy is required, so there is no default.
    /// Non-empty must be an absolute http/https address; validated on save.
    /// </summary>
    public string? Proxy { get; init; }

    /// <summary>Ids of engines the user turned off. Unknown ids are ignored (an engine may be removed in a later version).</summary>
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
/// Embeddings default <b>off</b>: the store works FTS-only, and turning the local hashing provider on rewrites
/// every future record into vectors the current store did not have — a ranking change a person did not ask
/// for. The provider here is local and dependency-free (hashed character trigrams), so enabling costs no
/// network call; a model-backed embedding endpoint would be an external dependency and is deliberately not
/// reachable through this setting.
/// </para>
/// </summary>
public sealed record MemorySettings
{
    /// <summary>
    /// Turn on the local hashing embedding provider. Off = recall scores run lexical + FTS only, and the
    /// vector component stays zero.
    /// </summary>
    public bool Embeddings { get; init; }

    public bool Equals(MemorySettings? other) => other is not null && Embeddings == other.Embeddings;

    public override int GetHashCode() => Embeddings.GetHashCode();
}

/// <summary>
/// Desktop control's switch and its bounds.
/// <para>
/// Defaults <b>off</b>: once on, the model can move the pointer and send keystrokes, the only irreversible
/// capability in this repository. Even when on, action tools go through human approval (the governance ask
/// list), so "on" only lets the model <b>ask</b>, not <b>do</b>.
/// </para>
/// </summary>
public sealed record DesktopSettings
{
    /// <summary>Whether the desktop capability is exposed to the model.</summary>
    public bool Enabled { get; init; }

    /// <summary>Base lease lifetime in milliseconds: how long after an observation actions may still rely on it. Bounded by policy.</summary>
    public int LeaseTtlMs { get; init; } = 15_000;

    /// <summary>
    /// Approval gear: <c>none</c> (no approval) / <c>dangerous</c> (dangerous actions only) / <c>all</c> (all).
    /// <para>
    /// "Dangerous" means leaving the GUI: running commands, launching programs, writing the clipboard. Clicks and
    /// typing stay inside the interface a person can see, so the middle gear lets them through.
    /// </para>
    /// </summary>
    public string Approval { get; init; } = ApprovalGears.All;

    /// <summary>Window title substrings an action may target (case-insensitive); empty means no restriction.</summary>
    public IReadOnlyList<string> AllowedWindowTitles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// How far an action may go to reach its target: <c>foreground</c> (default: take the foreground when needed) /
    /// <c>auto</c> (stay in the background when possible, otherwise take over the screen) / <c>background</c>
    /// (never take over the screen; refuse and explain when impossible).
    /// <para>
    /// Coordinate clicks, keys and typing go through <c>SendInput</c>, which is global input and can only take the
    /// foreground and move the real pointer; background channels are the page debug port of browser / Electron
    /// apps (which must be started by <c>desktop_launch</c>) and UI Automation patterns on named controls. Read
    /// fresh on every action and launch, so changes need no restart.
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
/// MCP servers.
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
/// A deny list rather than an allow list, so a skill added later is on by default.
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
    public double SilenceThreshold { get; init; } = 0.003;

    /// <summary>Play a tone on the output device when the wake word fires. Shares the input card; it is feedback for the wake gate.</summary>
    public bool WakeTone { get; init; } = true;

    /// <summary>Speaking over the assistant stops it. Off by default; for headphones (a speaker is heard by the mic).</summary>
    public bool BargeIn { get; init; }

    /// <summary>Absolute silence that ends the utterance.</summary>
    public double UtteranceGapMs { get; init; } = 1200;

    /// <summary>Grace period after the wake word for the user to start speaking (ms). The session is not closed on the gap before speech starts.</summary>
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
    /// <summary>Defaults to the managed local KWS+ASR engine (port 45103), so enabling it needs no further wiring.</summary>
    public string? BaseUrl { get; init; } = "http://127.0.0.1:45103";

    /// <summary>Endpoint protocol; defaults to <see cref="AsrProtocols.SenseVoice"/>, what the local engine speaks.</summary>
    public string Protocol { get; init; } = AsrProtocols.SenseVoice;

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
    /// <summary>Defaults to the managed local TTS engine (port 45102).</summary>
    public string? BaseUrl { get; init; } = "http://127.0.0.1:45102";

    /// <summary>Endpoint protocol; defaults to <see cref="TtsProtocols.Local"/>, the managed local engine.</summary>
    public string Protocol { get; init; } = TtsProtocols.Local;

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

    /// <summary>
    /// Transcript of <see cref="SpeakerRefPath"/> (what is actually said in the clip). The cloner aligns the
    /// text with the reference audio's codec codes, so a wrong or missing transcript is what turns cloning
    /// into gibberish. Blank = zero-shot clone (timbre only).
    /// </summary>
    public string? SpeakerRefText { get; init; }

    public double TimeoutSeconds { get; init; } = 600;

    public int ChunkMs { get; init; } = 100;
}

/// <summary>
/// Process management for the local voice engines.
/// <para>
/// Two independent switches, one executable each: <c>tts</c> manages the OpenAI-compatible TTS service process,
/// <c>kws</c> manages the KWS+VAD+ASR recognition service. Off = kill the process = actually release VRAM/RAM:
/// ONNX memory pools grow and <b>do not return</b> memory, so only process exit reliably frees it. The host
/// sends no requests itself; the HTTP client addresses still point at the engine ports via
/// <see cref="TtsSettings"/> / <see cref="AsrSettings"/>.
/// </para>
/// <para>
/// The command line expands the <c>{port}</c> placeholder: the engine needs to know which port it listens on and
/// the host needs to know which to connect to, so one field feeds both and the two cannot silently drift.
/// </para>
/// </summary>
public sealed record LocalSettings
{
    /// <summary>Managed local TTS engine process. The service address is configured in the tts section (openai protocol).</summary>
    public LocalEngineSettings Tts { get; init; } = new()
    {
        Command = "engines/tts/python/python.exe",
        Arguments = ["tts-server.py", "--port", "{port}"],
        WorkingDirectory = "engines/tts",
        Port = 45102,
    };

    /// <summary>Managed local KWS+VAD+ASR engine process. The service address is configured in the asr section.</summary>
    public LocalEngineSettings Kws { get; init; } = new()
    {
        Command = "engines/kws-asr/python/python.exe",
        Arguments = ["asr-server.py", "--port", "{port}"],
        WorkingDirectory = "engines/kws-asr",
        Port = 45103,
    };

    public bool Equals(LocalSettings? other) =>
        other is not null && Tts == other.Tts && Kws == other.Kws;

    public override int GetHashCode() => HashCode.Combine(Tts, Kws);
}

/// <summary>One local engine process: executable, argument template, port. Empty Command = not managed.</summary>
public sealed record LocalEngineSettings : IEquatable<LocalEngineSettings>
{
    public bool Enabled { get; init; }

    public string? Command { get; init; }

    /// <summary>Argument template; the <c>{port}</c> placeholder is replaced with the port value before launch.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public string? WorkingDirectory { get; init; }

    public int Port { get; init; }

    /// <summary>Grace period after process start before the first requests may be sent; slower engines will see connection failures and retries.</summary>
    public int ReadyTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Wake words (display names); when the engine supports it the whole list is replaced: the engine converts to
    /// pinyin, persists it and rebuilds the recognition stream. KWS only; empty = not sent, the engine keeps its
    /// default list — pure TTS engines have no word list.
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
    /// <summary>
    /// Which wire protocol the endpoint speaks: <see cref="LlmProtocols.OpenAi"/> (the historical default) or
    /// <see cref="LlmProtocols.Anthropic"/>.
    /// </summary>
    public string Protocol { get; init; } = LlmProtocols.OpenAi;

    public string? BaseUrl { get; init; }

    public string? Model { get; init; }

    /// <summary>
    /// Mark the system prompt as cacheable (<c>cache_control</c>). For endpoints that support prompt caching;
    /// a server that does not know the field may reject the request, so it is off until asked for.
    /// </summary>
    public bool PromptCache { get; init; }

    /// <summary>
    /// Never serialised into a response: the settings endpoint is reachable by anything holding the bearer
    /// token, and a key echoed back is a key leaked. On write, absent means "keep", empty means "clear".
    /// </summary>
    public string? ApiKey { get; init; }

    public double TimeoutSeconds { get; init; } = 120;

    /// <summary>The model's context window in tokens. The prompt budget is this minus <see cref="MaxTokens"/>.</summary>
    public int ContextTokens { get; init; } = 128000;

    /// <summary>Output ceiling for one completion.</summary>
    public int MaxTokens { get; init; } = 32000;

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
    /// Default reasoning gear, sent as <c>reasoning_effort</c> with every completion — including when the user
    /// has not picked one for this message.
    /// <para>
    /// It must appear in <see cref="ReasoningGears"/> (validated on save), or the session dropdown cannot preselect
    /// any item. A single message may use another gear; see <see cref="InputItem.ReasoningEffort"/>.
    /// </para>
    /// <para>
    /// The value itself is <b>not</b> restricted to <see cref="ReasoningEfforts.Values"/>: an endpoint may invent
    /// its own words, and rejecting a word the server would accept is worse than sending it as-is. <c>off</c> (or
    /// empty) means the field is <b>not sent</b>, not sent with a value.
    /// </para>
    /// </summary>
    public string ReasoningEffort { get; init; } = ReasoningEfforts.Default;

    /// <summary>
    /// Gear list: the words the UI offers, in display order. The user adds and removes them in settings.
    /// <para>
    /// This is a <b>menu</b>, not a constraint — <see cref="ReasoningEffort"/> is still free text; removing a word
    /// only removes it from the menu, and messages already sent with it are unaffected. When the list is empty the
    /// UI falls back to the built-in defaults rather than showing an empty menu.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ReasoningGears { get; init; } = ReasoningEfforts.Values;

    /// <summary>
    /// Display text per gear: word → label shown in the UI. Display only; the word itself is always what is sent
    /// to the model. Unregistered words fall back to the built-in name (the six common ones) or the word itself.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReasoningLabels { get; init; } = new Dictionary<string, string>();
}


/// <summary>What the API returns: the same shape, minus the secret, plus whether one is stored.</summary>
public sealed record ModelSettingsView
{
    public string Protocol { get; init; } = LlmProtocols.OpenAi;

    public string? BaseUrl { get; init; }

    public bool PromptCache { get; init; }


    public string? Model { get; init; }

    public bool ApiKeySet { get; init; }

    public double TimeoutSeconds { get; init; } = 120;

    public int ContextTokens { get; init; } = 128000;

    public int MaxTokens { get; init; } = 32000;

    public bool SupportsImages { get; init; }

    public string ReasoningEffort { get; init; } = ReasoningEfforts.Default;

    /// <summary>The gears the UI offers, in order. See <see cref="ModelSettings.ReasoningGears"/>.</summary>
    public IReadOnlyList<string> ReasoningGears { get; init; } = ReasoningEfforts.Values;

    /// <summary>
    /// Display text per gear: word → label shown in the UI. Display only; the word itself is always what is sent
    /// to the model. Unregistered words fall back to the built-in name (the six common ones) or the word itself.
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
            Protocol = LlmProtocols.Normalize(settings.Model.Protocol),
            BaseUrl = settings.Model.BaseUrl,
            Model = settings.Model.Model,
            ApiKeySet = !string.IsNullOrEmpty(settings.Model.ApiKey),
            TimeoutSeconds = settings.Model.TimeoutSeconds,
            ContextTokens = settings.Model.ContextTokens,
            MaxTokens = settings.Model.MaxTokens,
            SupportsImages = settings.Model.SupportsImages,
            PromptCache = settings.Model.PromptCache,
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
