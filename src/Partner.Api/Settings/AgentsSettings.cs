using System.Text.Json.Serialization;

namespace Partner.Api.Settings;

/// <summary>The main agent's editable part: its prompt. Null means the built-in one.</summary>
public sealed record MainAgentSettings
{
    public string? Prompt { get; init; }
}

/// <summary>
/// A model for one sub-agent when it should not use the main model. Same shape as the main model section.
/// <para>
/// <see cref="ApiKey"/> is write-only exactly as <see cref="ModelSettings.ApiKey"/> is: null keeps the stored
/// key, an empty string clears it, a value replaces it — and it is never echoed back (see
/// <see cref="SubAgentModelView"/>).
/// </para>
/// </summary>
public sealed record SubAgentModelSettings
{
    public string? BaseUrl { get; init; }

    public string? Model { get; init; }

    public string? ApiKey { get; init; }

    public double TimeoutSeconds { get; init; } = 120;

    public int ContextTokens { get; init; } = 32768;

    public int MaxTokens { get; init; } = 4096;

    /// <summary>Decides whether the screenshot tool exists for this sub-agent — the whole point of a separate model.</summary>
    public bool SupportsImages { get; init; }

    public string ReasoningEffort { get; init; } = Partner.Core.Agent.Llm.ReasoningEfforts.Default;

    /// <summary>
    /// Only ever set on the way out: tells the UI a key is stored without sending it. Ignored on the way in and
    /// never written to the file.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ApiKeySet { get; init; }
}

/// <summary>One sub-agent: a prompt, one scenario, and how many rounds it may spend on a job.</summary>
public sealed record SubAgentSettings
{
    /// <summary>Stable name for the agent. <c>desktop</c> is the built-in one; others are generated.</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>One of <see cref="AgentScenarios"/>. Locked to <c>desktop</c> for the built-in agent.</summary>
    public string Scenario { get; init; } = string.Empty;

    /// <summary>Null = the scenario's built-in prompt. Stored as an override so an upgrade to the default reaches people who never edited it.</summary>
    public string? Prompt { get; init; }

    /// <summary>Null = follow the main model (the default).</summary>
    public SubAgentModelSettings? Model { get; init; }

    /// <summary>Rounds per job, counting the first. A round is one full try; see the job engine.</summary>
    public int MaxRounds { get; init; } = AgentsSettings.DefaultMaxRounds;

    [JsonIgnore]
    public bool IsBuiltin => Id == AgentsSettings.DesktopId;
}

/// <summary>
/// The agents section of the settings: the main agent's prompt, and the sub-agents it can hand work to.
/// <para>
/// Value equality is written out by hand for the same reason as <see cref="DesktopSettings"/>: a record holding a
/// list compares it by reference, so two identical defaults would look different and every save would restart
/// the consumer.
/// </para>
/// </summary>
public sealed record AgentsSettings
{
    public const string DesktopId = "desktop";
    public const int DefaultMaxRounds = 5;
    public const int MaxRoundsCeiling = 10;

    public MainAgentSettings Main { get; init; } = new();

    public IReadOnlyList<SubAgentSettings> SubAgents { get; init; } = [BuiltinDesktop()];

    /// <summary>The agent that owns desktop control. It always exists; see <see cref="WithBuiltins"/>.</summary>
    public static SubAgentSettings BuiltinDesktop() => new()
    {
        Id = DesktopId,
        Name = "桌面操控",
        Enabled = true,
        Scenario = AgentScenarios.Desktop,
        MaxRounds = DefaultMaxRounds,
    };

    /// <summary>Who handles a scenario, or null. Disabled agents are included: they still hold the scenario.</summary>
    public SubAgentSettings? Owner(string scenario) =>
        SubAgents.FirstOrDefault(agent => string.Equals(agent.Scenario, scenario, StringComparison.Ordinal));

    /// <summary>
    /// The same settings with the built-in agent present and its fixed parts restored.
    /// <para>
    /// Used when settings are loaded, not when they are saved: a file that lost the built-in agent (edited by
    /// hand, written by an older build) must still start. A save that omits it is rejected instead.
    /// </para>
    /// </summary>
    public AgentsSettings WithBuiltins()
    {
        var agents = SubAgents.Where(agent => agent is not null).ToList();
        var index = agents.FindIndex(agent => agent.Id == DesktopId);
        if (index < 0)
        {
            agents.Insert(0, BuiltinDesktop());
        }
        else
        {
            // Id and scenario are what the built-in agent *is*; the rest is the person's to change.
            agents[index] = agents[index] with { Scenario = AgentScenarios.Desktop };
        }

        return this with { SubAgents = agents };
    }

    public bool Equals(AgentsSettings? other) =>
        other is not null && Main == other.Main && SubAgents.SequenceEqual(other.SubAgents);

    public override int GetHashCode() => HashCode.Combine(Main, SubAgents.Count);
}

/// <summary>
/// Reading files: off by default and fail-closed, like the desktop. Reading is not harmless — what the model
/// reads enters its context and can be repeated — so the directories are a whitelist the person writes.
/// </summary>
public sealed record FilesSettings
{
    public bool Enabled { get; init; }

    /// <summary>Directories the file tools may read under. Empty means nothing is reachable.</summary>
    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();

    public bool Equals(FilesSettings? other) =>
        other is not null && Enabled == other.Enabled && Roots.SequenceEqual(other.Roots, StringComparer.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(Enabled, Roots.Count);
}
