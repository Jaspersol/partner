namespace Partner.Core.Agent.Hooks;

/// <summary>
/// <see cref="IAgentHook"/> 的便捷基类：默认优先级 100、无 <see cref="Before"/>/<see cref="After"/> 约束。
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
