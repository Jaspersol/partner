namespace Partner.Core.Agent.Hooks;

/// <summary>
/// Agent lifecycle hook. Implementers usually derive from <see cref="AgentHookBase"/> to omit the constraint lists.
/// </summary>
public interface IAgentHook
{
    /// <summary>The phase this hook is mounted on.</summary>
    AgentPhase Phase { get; }

    /// <summary>Unique name within the phase, used by <see cref="Before"/>/<see cref="After"/> constraints.</summary>
    string Name { get; }

    /// <summary>Ordering weight, smaller runs earlier (default 100). Used only as a tiebreaker without constraint conflicts.</summary>
    int Priority { get; }

    /// <summary>Must run before these hooks (referenced by <see cref="Name"/> within the same phase).</summary>
    IReadOnlyList<string> Before { get; }

    /// <summary>Must run after these hooks (referenced by <see cref="Name"/> within the same phase).</summary>
    IReadOnlyList<string> After { get; }

    /// <summary>Runs the hook.</summary>
    ValueTask<HookOutcome> RunAsync(HookContext ctx, CancellationToken ct);
}
