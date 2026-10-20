namespace Partner.Core.Queue;

/// <summary>
/// Consistent snapshot of the queue.
/// </summary>
/// <param name="HeadSeq">The next <c>Seq</c> to be assigned.</param>
/// <param name="RunningItemId">The item currently running; <c>null</c> when none.</param>
/// <param name="Depth">Number of queued items waiting (excluding the running one).</param>
/// <param name="Paused">Reserved; always <c>false</c> for the single-input queue.</param>
public sealed record QueueState(long HeadSeq, string? RunningItemId, int Depth, bool Paused);
