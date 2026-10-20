namespace Partner.Core.Scheduler;

/// <summary>Result of one delivery pass.</summary>
public sealed record DeliveryOutcome(
    int Delivered,
    int Failed,
    int SkippedAlreadyDelivered,
    DateTimeOffset? NextDueAt);

/// <summary>
/// The durable scheduler (design doc §2.4). Its only job is: <b>when a task comes due, enqueue one
/// <c>InputItem(source: schedule)</c></b>. It never runs an agent itself — that would bypass the queue's
/// ordering and idempotency guarantees.
/// <para>
/// Semantics it must uphold:
/// <list type="bullet">
///   <item><b>State-driven recompute.</b> The due set is filtered from persisted state; the timer is an
///   optimization, not the source of truth. A missed wake-up degrades to "late", never "lost".</item>
///   <item><b>Receipt before advance.</b> A receipt is committed first; if that write fails, the task is
///   <i>not</i> advanced and stays due.</item>
///   <item><b>Dedup</b> on <c>(TaskId, OccurrenceAt)</c>.</item>
///   <item><b>Failure keeps the obligation.</b> A failed delivery increments the attempt counter and keeps
///   the same due instant.</item>
///   <item><b>Clock rollback safe.</b> A future instant is never fired early.</item>
///   <item><b>One occurrence → one item.</b></item>
/// </list>
/// </para>
/// </summary>
public interface IScheduler : IAsyncDisposable
{
    /// <summary>Registers a new task or updates an existing one (matched by <c>TaskId</c>).</summary>
    Task<ScheduleTask> ScheduleAsync(ScheduleTask task, CancellationToken ct = default);

    /// <summary>Pauses or resumes a task without losing its next occurrence.</summary>
    Task<ScheduleTask> SetStatusAsync(string taskId, ScheduleTaskStatus status, CancellationToken ct = default);

    /// <summary>Forgets a task for good. False when there was no such task. Receipts stay: they are the dedup record.</summary>
    Task<bool> RemoveAsync(string taskId, CancellationToken ct = default);

    Task<IReadOnlyList<ScheduleTask>> ListAsync(CancellationToken ct = default);

    /// <summary>Earliest instant among schedulable tasks, or null when none are pending.</summary>
    DateTimeOffset? NextDueAt { get; }

    /// <summary>One delivery pass over everything currently due. Safe to call repeatedly.</summary>
    Task<DeliveryOutcome> DeliverDueAsync(CancellationToken ct = default);

    /// <summary>Recomputes the next due instant and wakes the driver if it is waiting.</summary>
    Task RequestDriveAsync(CancellationToken ct = default);

    /// <summary>The driver loop: deliver, then wait for the next due instant (OS-level wait, no event loop).</summary>
    Task RunAsync(CancellationToken ct = default);
}
