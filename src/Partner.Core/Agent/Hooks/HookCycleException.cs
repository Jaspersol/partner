namespace Partner.Core.Agent.Hooks;

/// <summary>
/// The <see cref="IAgentHook.Before"/>/<see cref="IAgentHook.After"/> constraints of hooks in one phase form a
/// cycle. Detected at <see cref="HookRegistry.Register"/>, and the hook is not registered.
/// </summary>
public sealed class HookCycleException : InvalidOperationException
{
    /// <summary>Constructs the exception from a message (containing at least one hook name in the cycle).</summary>
    public HookCycleException(string message)
        : base(message)
    {
    }
}
