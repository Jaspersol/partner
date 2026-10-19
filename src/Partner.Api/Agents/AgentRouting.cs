using Partner.Api.Settings;
using Partner.Core.Agent;

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

    /// <summary>
    /// The same rule for the web tools: the main agent searches and fetches only while no enabled sub-agent has
    /// taken the scenario. Unlike files this is <b>on by default</b> and there is no built-in web sub-agent, so
    /// out of the box the tools are the main agent's — which is what "联网搜索默认打开" has to mean for it to be
    /// true of something.
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
        || before.Files != after.Files
        || !Effective(before).Select(route => route.Scenario.Key).SequenceEqual(Effective(after).Select(route => route.Scenario.Key))
        || MainOwnsFiles(before) != MainOwnsFiles(after)
        // Only the web switch and who owns the tools are here. The proxy, the budget, the result count and the
        // engine list are read through a delegate on every search, so editing them must not restart anything —
        // a restart would drop the in-process answer cache and the ledger beside it.
        || before.Web.Enabled != after.Web.Enabled
        || MainOwnsWeb(before) != MainOwnsWeb(after);

    /// <summary>The main agent's prompt: the person's text (or the default), then the generated delegation block.</summary>
    public static string MainPrompt(RuntimeSettings settings)
    {
        var body = settings.Agents.Main.Prompt is { } custom && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : AgentPrompts.MainDefault;
        var block = AgentPrompts.DelegationBlock(DelegationScenarios(settings));

        return block.Length == 0 ? body : body + "\n\n" + block;
    }

    /// <summary>A sub-agent's prompt: the person's text (or the scenario's default), then the report contract.</summary>
    public static string SubAgentPrompt(SubAgentSettings agent)
    {
        var body = agent.Prompt is { } custom && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : AgentScenarios.Find(agent.Scenario)?.DefaultPrompt ?? string.Empty;

        return body + "\n\n" + AgentPrompts.ReportContract(agent.MaxRounds);
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
