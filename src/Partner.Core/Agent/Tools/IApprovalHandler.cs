namespace Partner.Core.Agent.Tools;

/// <summary>
/// Human approval channel: called by the turn runner when governance decides <see cref="ToolDecision.Ask"/>.
/// </summary>
public interface IApprovalHandler
{
    /// <param name="toolName">The tool awaiting approval.</param>
    /// <param name="argumentsJson">The raw argument JSON from the model.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when approved.</returns>
    Task<bool> RequestAsync(string toolName, string argumentsJson, CancellationToken ct = default);
}

/// <summary>
/// Default approval channel for headless environments: never approves. No side effects, never blocks.
/// </summary>
public sealed class DenyAllApprovalHandler : IApprovalHandler
{
    /// <summary>Creates an approval channel that always denies.</summary>
    public DenyAllApprovalHandler()
    {
    }

    /// <inheritdoc />
    public Task<bool> RequestAsync(string toolName, string argumentsJson, CancellationToken ct = default)
        => Task.FromResult(false);
}
