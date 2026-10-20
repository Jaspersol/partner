using Partner.Core.Contracts;

namespace Partner.Core.Queue;

/// <summary>
/// Single-input queue: strict FIFO, idempotent enqueue, bounded capacity, single consumer, crash-recoverable.
/// </summary>
public interface IInputQueue : IAsyncDisposable
{
    /// <summary>
    /// Enqueues and returns the item's <c>Seq</c>. Waits when the queue is full; never drops and never throws
    /// a capacity error. Idempotent: when <see cref="InputItem.IdempotencyKey"/> already exists (including for a
    /// consumed item) nothing is enqueued and the existing item's <c>Seq</c> is returned.
    /// </summary>
    ValueTask<long> EnqueueAsync(InputItem item, CancellationToken ct = default);

    /// <summary>
    /// Takes the head item and marks it running. Waits when nothing is available; returns <c>null</c> when
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    ValueTask<InputItem?> TakeAsync(CancellationToken ct = default);

    /// <summary>Current snapshot.</summary>
    QueueState Snapshot();

    /// <summary>
    /// Items still in the queue: queued plus the one running.
    /// <para>
    /// This is the queue <em>itself</em>, not everything it ever accepted. An item leaves the set as soon as it
    /// settles (see <see cref="CompleteAsync"/>), so the answer is in memory and costs one lock; reading the
    /// log instead would read the whole file on every poll.
    /// </para>
    /// <para>
    /// Returned in arrival order: the order they will run in, and the order a reader is looking at.
    /// </para>
    /// </summary>
    IReadOnlyList<InputItem> ActiveItems();

    /// <summary>
    /// Cancels an item. <c>Queued</c>: set to <c>Canceled</c> and persisted (never handed to the consumer),
    /// returns <c>true</c>; <c>Running</c>: triggers <c>RunningToken</c> cancellation and returns <c>true</c>
    /// (the consumer settles the state via <see cref="CompleteAsync"/>); unknown or already settled items
    /// return <c>false</c>.
    /// </summary>
    Task<bool> CancelAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// Settles an item's state. Accepts only <c>Done</c>/<c>Failed</c>/<c>Canceled</c> (otherwise throws
    /// <see cref="ArgumentException"/>). An unknown <paramref name="itemId"/> throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    Task CompleteAsync(string itemId, InputItemStatus status, string? error = null, CancellationToken ct = default);

    /// <summary>Reads the persisted log in write order (one record per state change, no de-duplication).</summary>
    IAsyncEnumerable<InputItem> ReadLogAsync(CancellationToken ct = default);

    /// <summary>
    /// Cancellation token for the item last handed out by <see cref="TakeAsync"/>; <see cref="CancellationToken.None"/>
    /// before anything is taken. The consumer passes it to the execution logic so <see cref="CancelAsync"/> has
    /// somewhere to land for a <c>Running</c> item.
    /// </summary>
    CancellationToken RunningToken { get; }
}
