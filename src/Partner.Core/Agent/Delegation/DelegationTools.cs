namespace Partner.Core.Agent.Delegation;

/// <summary>
/// Names the history replay and the delegation tools both have to agree on. Kept in Core so the context
/// assembler can recognise a hand-off without referring to the API project that implements the tool.
/// </summary>
public static class DelegationTools
{
    /// <summary>The tool that hands work to a sub-agent.</summary>
    public const string Delegate = "agent_delegate";
}
