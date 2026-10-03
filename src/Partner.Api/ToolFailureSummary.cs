namespace Partner.Api;

/// <summary>One row of the tool-failure summary, as the wire carries it.</summary>
/// <param name="Tool">The tool's name, as the model called it.</param>
/// <param name="Reason">The grouped reason: what stayed the same across every occurrence.</param>
/// <param name="Count">How many times this happened.</param>
/// <param name="LastSeen">When it last happened, ISO 8601.</param>
/// <param name="LastMessage">The newest full message behind that group, so the row is diagnosable not just countable.</param>
/// <param name="ItemId">
/// The queue item it last happened in, or null when the caller did not know it. The join key to the session
/// log's <c>ToolResult</c> event, whose <c>at</c> is the same moment kept on disk. Not the queue log: a tool
/// failing does not finish the turn, so the item stays <c>Running</c> and has no <c>finishedAt</c> to compare.
/// </param>
public sealed record ToolFailureView(
    string Tool,
    string Reason,
    int Count,
    string LastSeen,
    string LastMessage,
    string? ItemId);

/// <param name="Distinct">How many different failures were seen.</param>
/// <param name="Occurrences">How many failures in total, counting repeats.</param>
public sealed record ToolFailureTotalsView(int Distinct, int Occurrences);

/// <param name="Failures">The rows, most frequent first.</param>
/// <param name="Totals">How much there was to count.</param>
public sealed record ToolFailureSummary(IReadOnlyList<ToolFailureView> Failures, ToolFailureTotalsView Totals);
