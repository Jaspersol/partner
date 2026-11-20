using Partner.Api.Settings;
using Partner.Core.Agent;
using Partner.Core.Agent.Delegation;
using Partner.Core.Agent.Tools;
using Partner.Core.Agent.Web;
using Partner.Desktop.Tools;

namespace Partner.Api.Agents;

/// <summary>A scenario that is actually reachable right now, and who handles it.</summary>
public sealed record AgentRoute(SubAgentSettings Agent, AgentScenario Scenario);

/// <summary>
/// Which scenarios the main agent can delegate, who owns each tool family, and what the prompts say.
/// <para>
/// One rule runs through all of it: <b>a capability has exactly one holder</b>. Desktop tools belong to the
/// desktop sub-agent and never to the main agent (the desktop state is a single shared object; two agents using it
/// would invalidate each other's observations). File tools belong to the file-read sub-agent when there is an
/// enabled one, and to the main agent otherwise — "no scenario configured" means the main agent does the work.
/// The web tools follow the same rule with one difference in the starting state: files are off until somebody
/// says where they may read, while web search is on from the first run and belongs to the main agent until a
/// web-search sub-agent is created and enabled.
/// </para>
/// </summary>
public static class AgentRouting
{
    /// <summary>Whether the capability behind a scenario is switched on.</summary>
    public static bool CapabilityOn(RuntimeSettings settings, string scenario) => scenario switch
    {
        AgentScenarios.Desktop => settings.Desktop.Enabled,
        AgentScenarios.FileRead => settings.Files.Enabled,
        AgentScenarios.WebSearch => settings.Web.Enabled,
        _ => false,
    };

    internal enum NativeToolKind { Unknown, Desktop, File, Web, Information, ScheduleList, ScheduleCreate, ScheduleDelete }

    private sealed record CommonTool(string Name, NativeToolKind Kind, Func<ApiContext, SubAgentHost, RuntimeSettings, ITool> Create);

    private static readonly CommonTool[] MainTools =
    [
        new(ScheduleCreateTool.ToolName, NativeToolKind.ScheduleCreate, (context, _, _) => new ScheduleCreateTool(context)),
        new(ScheduleListTool.ToolName, NativeToolKind.ScheduleList, (context, _, _) => new ScheduleListTool(context)),
        new(ScheduleDeleteTool.ToolName, NativeToolKind.ScheduleDelete, (context, _, _) => new ScheduleDeleteTool(context)),
        new(SessionHistoryTool.ToolName, NativeToolKind.Information, (context, _, _) => new SessionHistoryTool(context.Session, context.Clock)),
        new(AgentDelegateTool.ToolName, NativeToolKind.Information, (_, host, settings) => new AgentDelegateTool(host, DelegationScenarios(settings))),
        new(AgentJobStatusTool.ToolName, NativeToolKind.Information, (_, host, _) => new AgentJobStatusTool(host)),
        new(AgentJobCancelTool.ToolName, NativeToolKind.Information, (_, host, _) => new AgentJobCancelTool(host)),
    ];

    internal static NativeToolKind ClassifyNativeTool(string name)
    {
        if (name == TaskReportTool.ToolName) return NativeToolKind.Information;
        foreach (var binding in MainTools)
            if (binding.Name == name) return binding.Kind;
        foreach (var family in ToolFamilies)
        {
            if (family.AllNames.Contains(name, StringComparer.Ordinal))
                return family.Kind;
        }
        return NativeToolKind.Unknown;
    }

    internal static void RegisterMainTools(InMemoryToolRegistry tools, ApiContext context, SubAgentHost host, RuntimeSettings settings)
    {
        var delegates = Effective(settings).Count > 0;
        foreach (var binding in MainTools)
        {
            if (binding.Kind == NativeToolKind.Information && binding.Name != SessionHistoryTool.ToolName && !delegates) continue;
            var tool = binding.Create(context, host, settings);
            if (tool.Spec.Name != binding.Name)
                throw new InvalidOperationException($"Tool registration does not match the catalogue for '{binding.Name}'.");
            tools.Register(tool);
        }
    }

    internal static TaskReportTool RegisterScenarioTools(
        string scenario, InMemoryToolRegistry tools, ApiContext context, RuntimeSettings settings,
        bool supportsImages, string workspace, string? agentId = null)
    {
        // Validate the scenario before mutating the registry.
        _ = Family(scenario);
        var report = new TaskReportTool();
        tools.Register(report);
        RegisterFamily(scenario, tools, context, settings, supportsImages, workspace, agentId);
        return report;
    }

    // The same family binding supplies status names and the existing factory. The factory remains the
    // authority for construction; checking its output prevents status and the running table from drifting.
    private sealed record ToolFamily(
        string Scenario,
        NativeToolKind Kind,
        IReadOnlyList<string> AllNames,
        Action<InMemoryToolRegistry, ApiContext, RuntimeSettings, bool, string, string?> Register)
    {
        public IEnumerable<string> Names(RuntimeSettings settings, bool supportsImages) => AllNames
            .Where(name => name is not ("file_write" or "file_edit") || FileWriteAvailable(settings))
            .Where(name => name != "desktop_screenshot" || supportsImages)
            .Where(name => name != "desktop_exec" || settings.Desktop.ExecEnabled);
    }

    private static readonly ToolFamily[] ToolFamilies =
    [
        new(AgentScenarios.Desktop, NativeToolKind.Desktop,
            DesktopToolCatalog.All,
            (tools, context, settings, supportsImages, _, _) => DesktopToolSet.Register(tools, context, settings, supportsImages)),
        new(AgentScenarios.FileRead, NativeToolKind.File,
            ["file_list", "file_read", "file_write", "file_edit"],
            (tools, context, settings, _, workspace, agentId) => FileToolSet.Register(tools, context.Settings, settings, workspace, agentId)),
        new(AgentScenarios.WebSearch, NativeToolKind.Web,
            [WebSearchTool.Name, WebFetchTool.Name],
            (tools, context, _, _, _, _) => WebToolSet.Register(tools, context)),
    ];

    private static ToolFamily Family(string scenario) =>
        ToolFamilies.FirstOrDefault(family => family.Scenario == scenario)
        ?? throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown agent scenario");

    /// <summary>The native tools exposed to a job, including its report tool.</summary>
    public static IReadOnlyList<string> ScenarioTools(string scenario, RuntimeSettings settings, bool supportsImages) =>
        Family(scenario).Names(settings, supportsImages).Prepend(TaskReportTool.ToolName).ToArray();

    internal static void RegisterFamily(
        string scenario, InMemoryToolRegistry tools, ApiContext context, RuntimeSettings settings,
        bool supportsImages, string workspace, string? agentId = null)
    {
        var family = Family(scenario);
        var count = tools.Specs.Count;
        family.Register(tools, context, settings, supportsImages, workspace, agentId);
        var registered = tools.Specs.Skip(count).Select(spec => spec.Name);
        if (!family.Names(settings, supportsImages).ToHashSet(StringComparer.Ordinal).SetEquals(registered))
            throw new InvalidOperationException($"Tool registration does not match the catalogue for '{scenario}'.");
    }

    /// <summary>The scenarios the main agent may delegate: owned by an enabled sub-agent, capability on.</summary>
    public static IReadOnlyList<AgentRoute> Effective(RuntimeSettings settings) =>
        AgentScenarios.All
            .Select(scenario => (scenario, owner: settings.Agents.Owner(scenario.Key)))
            .Where(pair => pair.owner is { Enabled: true } && CapabilityOn(settings, pair.scenario.Key))
            .Select(pair => new AgentRoute(pair.owner!, pair.scenario))
            .ToList();

    public static AgentRoute? Find(RuntimeSettings settings, string scenario) =>
        Effective(settings).FirstOrDefault(route => route.Scenario.Key == scenario);

    /// <summary>The main agent reads files itself only when files are on and no enabled sub-agent has taken them.</summary>
    public static bool MainOwnsFiles(RuntimeSettings settings) =>
        settings.Files.Enabled && settings.Agents.Owner(AgentScenarios.FileRead) is not { Enabled: true };

    internal static bool FileWriteAvailable(RuntimeSettings settings) => OperatingSystem.IsWindows()
        && settings.Files.Enabled && settings.Files.WriteEnabled
        && settings.Files.Roots.Count > 0 && settings.Files.WriteRoots.Count > 0;

    /// <summary>
    /// The same rule for the web tools: the main agent searches and fetches only while no enabled sub-agent has
    /// taken the scenario. Unlike files this is <b>on by default</b> and there is no built-in web sub-agent, so
    /// out of the box the tools are the main agent's — which is what "web search on by default" has to mean for it
    /// to be true of something.
    /// </summary>
    public static bool MainOwnsWeb(RuntimeSettings settings) =>
        settings.Web.Enabled && settings.Agents.Owner(AgentScenarios.WebSearch) is not { Enabled: true };

    /// <summary>
    /// Whether the main agent's tool table would be different. Prompts, names, round counts and sub-agent models
    /// are not part of it — they are read when a turn or a job starts — so editing one must not restart the
    /// consumer (a restart resets the counters and stops the MCP child processes).
    /// </summary>
    public static bool ToolTableDiffers(RuntimeSettings before, RuntimeSettings after) =>
        before.Desktop.Enabled != after.Desktop.Enabled
        || before.Desktop.ExecEnabled != after.Desktop.ExecEnabled
        || before.Files.Enabled != after.Files.Enabled
        || FileWriteAvailable(before) != FileWriteAvailable(after)
        || !Effective(before).Select(route => route.Scenario.Key).SequenceEqual(Effective(after).Select(route => route.Scenario.Key))
        || MainOwnsFiles(before) != MainOwnsFiles(after)
        // Only the web switch and who owns the tools are here. The proxy, the budget, the result count and the
        // engine list are read through a delegate on every search, so editing them must not restart anything —
        // a restart would drop the in-process answer cache and its ledger.
        || before.Web.Enabled != after.Web.Enabled
        || MainOwnsWeb(before) != MainOwnsWeb(after);

    /// <summary>The main agent's prompt: the person's text (or the default), then the generated delegation block.</summary>
    public static string MainPrompt(RuntimeSettings settings)
    {
        var body = settings.Agents.Main.Prompt is { } custom && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : AgentPrompts.MainDefault;
        var block = AgentPrompts.DelegationBlock(DelegationScenarios(settings));

        var write = MainOwnsFiles(settings) && FileWriteAvailable(settings) ? "\n\n" + AgentPrompts.FileWriteGuidance : "";
        return body + "\n\n" + AgentPrompts.FileToolBoundary + write + (block.Length == 0 ? "" : "\n\n" + block);
    }

    /// <summary>A sub-agent's prompt: the person's text (or the scenario's default), then the report contract.</summary>
    public static string SubAgentPrompt(SubAgentSettings agent, RuntimeSettings settings)
    {
        var body = agent.Prompt is { } custom && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : AgentScenarios.Find(agent.Scenario)?.DefaultPrompt ?? string.Empty;

        var ownsWrite = agent.Scenario == AgentScenarios.FileRead && FileWriteAvailable(settings)
            && settings.Agents.Owner(AgentScenarios.FileRead) is { Enabled: true } owner
            && string.Equals(owner.Id, agent.Id, StringComparison.Ordinal);
        var write = ownsWrite ? "\n\n" + AgentPrompts.FileWriteGuidance : "";
        return body + "\n\n" + AgentPrompts.FileToolBoundary + write + "\n\n" + AgentPrompts.ReportContract(agent.MaxRounds);
    }

    public static IReadOnlyList<DelegationScenario> DelegationScenarios(RuntimeSettings settings) =>
        Effective(settings).Select(route => new DelegationScenario(route.Scenario.Key, route.Scenario.Name, route.Scenario.When)).ToList();

    /// <summary>
    /// Things that are configured but will not work, in words a person can act on. Not validation errors: a
    /// disabled capability is a legitimate state, it just deserves to be said out loud.
    /// </summary>
    public static IReadOnlyList<string> Problems(RuntimeSettings settings)
    {
        var problems = new List<string>();

        if (settings.Desktop.Enabled && settings.Agents.Owner(AgentScenarios.Desktop) is not { Enabled: true })
        {
            problems.Add("桌面操控已开启，但桌面操控子 agent 没有启用：桌面工具不会提供给任何人");
        }

        foreach (var agent in settings.Agents.SubAgents.Where(agent => agent.Enabled))
        {
            if (!CapabilityOn(settings, agent.Scenario))
            {
                var what = AgentScenarios.Find(agent.Scenario)?.Name ?? agent.Scenario;
                problems.Add($"「{agent.Name}」暂不可用：{what}的能力没有开启");
            }
        }

        if (settings.Files.Enabled)
        {
            foreach (var root in settings.Files.Roots.Where(root => !Directory.Exists(root)))
            {
                problems.Add($"允许读取的目录不存在：{root}");
            }
        }

        return problems;
    }
}
