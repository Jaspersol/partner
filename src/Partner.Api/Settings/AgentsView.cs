using Partner.Api.Agents;
using Partner.Core.Agent;

namespace Partner.Api.Settings;

public sealed record MainAgentView(string? Prompt, string DefaultPrompt, string EffectivePrompt, int PromptTokens);

public sealed record SubAgentModelView(
    string? BaseUrl,
    string? Model,
    bool ApiKeySet,
    double TimeoutSeconds,
    int ContextTokens,
    int MaxTokens,
    bool SupportsImages,
    string ReasoningEffort);

public sealed record SubAgentView(
    string Id,
    string Name,
    bool Enabled,
    string Scenario,
    bool Builtin,
    string? Prompt,
    string DefaultPrompt,
    string EffectivePrompt,
    int PromptTokens,
    int MaxRounds,
    SubAgentModelView? Model);

/// <summary>One scenario and where it stands: who holds it, whether its capability is on, whether it is reachable.</summary>
public sealed record ScenarioView(
    string Key,
    string Name,
    string When,
    string? OwnerId,
    bool CapabilityEnabled,
    bool Available);

public sealed record AgentLimits(int MaxRounds, int DefaultMaxRounds, int PromptTokens);

/// <summary>
/// Everything the prompts page needs in one read: the stored values, the defaults they override, the prompts as the
/// model will actually see them, and what is wrong.
/// <para>
/// The effective prompts are included so the page shows the generated parts (the delegation block, the report
/// contract) that a person cannot edit but should be able to read. Sub-agent API keys are never in here — only
/// whether one is stored.
/// </para>
/// </summary>
public sealed record AgentsView(
    MainAgentView Main,
    IReadOnlyList<SubAgentView> SubAgents,
    IReadOnlyList<ScenarioView> Scenarios,
    IReadOnlyList<string> Problems,
    AgentLimits Limits,
    FilesSettings Files,
    bool DesktopEnabled,
    bool WebEnabled)
{
    public static AgentsView From(RuntimeSettings settings)
    {
        var effective = AgentRouting.Effective(settings).Select(route => route.Scenario.Key).ToHashSet(StringComparer.Ordinal);
        var main = AgentRouting.MainPrompt(settings);

        return new AgentsView(
            new MainAgentView(settings.Agents.Main.Prompt, AgentPrompts.MainDefault, main, PromptSize.Estimate(main)),
            settings.Agents.SubAgents.Select(agent => SubAgent(agent)).ToList(),
            AgentScenarios.All.Select(scenario =>
            {
                var owner = settings.Agents.Owner(scenario.Key);
                return new ScenarioView(
                    scenario.Key,
                    scenario.Name,
                    scenario.When,
                    owner?.Id,
                    AgentRouting.CapabilityOn(settings, scenario.Key),
                    effective.Contains(scenario.Key));
            }).ToList(),
            AgentRouting.Problems(settings),
            new AgentLimits(AgentsSettings.MaxRoundsCeiling, AgentsSettings.DefaultMaxRounds, PromptSize.Limit(settings.Model)),
            settings.Files,
            settings.Desktop.Enabled,

            // The prompts page says "this capability is off, switch it on in settings" instead of leaving a
            // scenario that looks configured and does nothing.
            settings.Web.Enabled);
    }

    private static SubAgentView SubAgent(SubAgentSettings agent)
    {
        var prompt = AgentRouting.SubAgentPrompt(agent);

        return new SubAgentView(
            agent.Id,
            agent.Name,
            agent.Enabled,
            agent.Scenario,
            agent.IsBuiltin,
            agent.Prompt,
            AgentScenarios.Find(agent.Scenario)?.DefaultPrompt ?? string.Empty,
            prompt,
            PromptSize.Estimate(prompt),
            agent.MaxRounds,
            agent.Model is null
                ? null
                : new SubAgentModelView(
                    agent.Model.BaseUrl,
                    agent.Model.Model,
                    !string.IsNullOrEmpty(agent.Model.ApiKey),
                    agent.Model.TimeoutSeconds,
                    agent.Model.ContextTokens,
                    agent.Model.MaxTokens,
                    agent.Model.SupportsImages,
                    agent.Model.ReasoningEffort));
    }
}
