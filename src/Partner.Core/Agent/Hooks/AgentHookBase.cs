namespace Partner.Core.Agent.Hooks;

/// <summary>
/// Convenience base for <see cref="IAgentHook"/>: default priority 100, no <see cref="Before"/>/<see cref="After"/>
/// constraints.
/// </summary>
public abstract class AgentHookBase : IAgentHook
{
    /// <inheritdoc />
    public abstract AgentPhase Phase { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public virtual int Priority => 100;

    /// <inheritdoc />
    public virtual IReadOnlyList<string> Before => Array.Empty<string>();

    /// <inheritdoc />
    public virtual IReadOnlyList<string> After => Array.Empty<string>();

    /// <inheritdoc />
    public abstract ValueTask<HookOutcome> RunAsync(HookContext ctx, CancellationToken ct);
}
