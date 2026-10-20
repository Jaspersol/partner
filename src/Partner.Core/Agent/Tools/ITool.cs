namespace Partner.Core.Agent.Tools;

/// <summary>
/// A tool the model can call.
/// <para>
/// An implementation only executes: argument parsing and failures are returned through <see cref="ToolResult"/>,
/// never thrown to abort the turn. Whether a call is permitted is decided by <see cref="IToolGovernance"/>
/// <b>before</b> the call.
/// </para>
/// </summary>
public interface ITool
{
    /// <summary>The tool description (the registry and the model both use it).</summary>
    ToolSpec Spec { get; }

    /// <param name="argumentsJson">The raw argument JSON from the model.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default);
}

/// <summary>
/// A tool that knows how long it legitimately needs. The runner's per-call timeout is one number for every
/// tool; a tool with its own time budget would otherwise be cancelled by that number and lose results it had
/// already collected.
/// </summary>
public interface ITimedTool : ITool
{
    /// <summary>The longest this call may run. Only ever extends the runner's default, never shortens it.</summary>
    TimeSpan Timeout { get; }
}
