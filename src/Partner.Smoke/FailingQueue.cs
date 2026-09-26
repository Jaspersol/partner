using Partner.Core.Contracts;
using Partner.Core.Queue;

namespace Partner.Smoke;

/// <summary>Delegating queue that fails its first enqueue, used to prove failure retention end to end.</summary>
internal sealed class FailOnceQueue(IInputQueue inner) : IInputQueue
{
    private int _remaining = 1;

    public ValueTask<long> EnqueueAsync(InputItem item, CancellationToken ct = default)
    {
        if (_remaining > 0)
        {
            _remaining--;
            throw new IOException("smoke: simulated enqueue failure");
        }

        return inner.EnqueueAsync(item, ct);
    }

    public ValueTask<InputItem?> TakeAsync(CancellationToken ct = default) => inner.TakeAsync(ct);

    public QueueState Snapshot() => inner.Snapshot();

    public Task<bool> CancelAsync(string itemId, CancellationToken ct = default) => inner.CancelAsync(itemId, ct);

    public Task CompleteAsync(string itemId, InputItemStatus status, string? error = null, CancellationToken ct = default) =>
        inner.CompleteAsync(itemId, status, error, ct);

    public IAsyncEnumerable<InputItem> ReadLogAsync(CancellationToken ct = default) => inner.ReadLogAsync(ct);

    public CancellationToken RunningToken => inner.RunningToken;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
