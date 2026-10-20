namespace Partner.Core.Agent.Tools;

/// <summary>The governance decision for one tool call.</summary>
public enum ToolDecision
{
    /// <summary>Execute directly.</summary>
    Allow,

    /// <summary>Requires human approval before it runs.</summary>
    Ask,

    /// <summary>Do not execute.</summary>
    Deny,
}

/// <summary>
/// A governance decision and its reason. <see cref="Reason"/> must be non-empty on every branch — it is
/// written to logs/events to explain the decision after the fact.
/// </summary>
/// <param name="Decision">The decision.</param>
/// <param name="Reason">The non-empty reason.</param>
public sealed record GovernanceDecision(ToolDecision Decision, string Reason);

/// <summary>
/// Tool governance: decides allow, ask, or deny <b>before</b> the tool actually runs.
/// Implementations must have no side effects; <paramref name="argumentsJson"/> is available for finer
/// argument-based decisions.
/// </summary>
public interface IToolGovernance
{
    /// <param name="toolName">The tool name the model requested.</param>
    /// <param name="argumentsJson">The raw argument JSON from the model.</param>
    GovernanceDecision Decide(string toolName, string argumentsJson);
}

/// <summary>
/// Allow-list governance: fail-closed by default; any tool not explicitly listed is denied.
/// <para>
/// Order: <c>ask</c> → <see cref="ToolDecision.Ask"/>; else <c>allowed</c> → <see cref="ToolDecision.Allow"/>;
/// else <see cref="ToolDecision.Deny"/>. Ask wins over allow when a name is in both lists, so a
/// "needs approval" setting cannot be bypassed silently. Tool names are compared ordinally (case-sensitive).
/// </para>
/// </summary>
public sealed class AllowListGovernance : IToolGovernance
{
    private readonly HashSet<string> _allowed;
    private readonly HashSet<string> _ask;

    /// <param name="allowed">Tool names that may run without approval.</param>
    /// <param name="ask">Tool names that need human approval; <see langword="null"/> means empty.</param>
    public AllowListGovernance(IEnumerable<string> allowed, IEnumerable<string>? ask = null)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        _allowed = new HashSet<string>(allowed, StringComparer.Ordinal);
        _ask = new HashSet<string>(ask ?? [], StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public GovernanceDecision Decide(string toolName, string argumentsJson)
    {
        // Same precedence as PolicyToolGovernance: deny > ask > allow > deny by default.
        // The stricter entry wins: a name in both ask and allow is treated as ask, so a "needs approval"
        // setting cannot be bypassed silently.
        if (_ask.Contains(toolName))
        {
            return new GovernanceDecision(ToolDecision.Ask, "需要人工批准（治理询问列表）");
        }

        if (_allowed.Contains(toolName))
        {
            return new GovernanceDecision(ToolDecision.Allow, "在治理白名单中");
        }

        return new GovernanceDecision(ToolDecision.Deny, "not registered in the governance allow-list");
    }
}
