namespace Partner.Core.Agent.Hooks;

/// <summary>
/// Phases of one agent turn where hooks can be mounted.
/// </summary>
public enum AgentPhase
{
    /// <summary>Before input dispatch.</summary>
    PreDispatch,

    /// <summary>After input dispatch.</summary>
    PostDispatch,

    /// <summary>Before building the agent (assembling prompt/tools).</summary>
    PreAgentBuild,

    /// <summary>After building the agent.</summary>
    PostAgentBuild,

    /// <summary>Before executing the agent (calling the model).</summary>
    PreExecute,

    /// <summary>After the response is produced.</summary>
    PostResponse,

    /// <summary>When an error occurs this turn.</summary>
    OnError,

    /// <summary>Cleanup phase that runs whether the turn succeeds or fails.</summary>
    Finally,
}
