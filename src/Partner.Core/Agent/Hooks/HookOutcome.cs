namespace Partner.Core.Agent.Hooks;

/// <summary>
/// A hook's verdict on the current phase's flow.
/// </summary>
public enum HookOutcome
{
    /// <summary>Continue running the remaining hooks in this phase.</summary>
    Continue,

    /// <summary>Stop this phase immediately; later hooks do not run.</summary>
    ShortCircuit,

    /// <summary>Mark the turn as skipping agent execution; later hooks in this phase still run (sticky until <c>ResetTurn</c>).</summary>
    SkipAgent,
}
