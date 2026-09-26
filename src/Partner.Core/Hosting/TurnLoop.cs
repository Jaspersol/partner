using Partner.Core.Agent;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Hosting;

/// <summary>What the loop has done so far. Reported so a host can show whether work is moving.</summary>
public sealed record TurnLoopStats(int Completed, int Failed, int Canceled, int Skipped)
{
    public static TurnLoopStats Empty { get; } = new(0, 0, 0, 0);

    public int Handled => Completed + Failed + Canceled + Skipped;
}

/// <summary>
/// The single consumer of the input queue (design doc §2.2): takes one item at a time, in arrival order, runs
/// a turn for it, and settles the item as <c>Done</c>/<c>Failed</c>/<c>Canceled</c>.
/// <para>
/// Invariants enforced here:
/// <list type="bullet">
///   <item>Exactly one item is in flight; the loop never overlaps turns.</item>
///   <item>Every claimed item is settled — an exception cannot leave an item stuck in <c>Running</c>.</item>
///   <item>Cancellation of a <c>Running</c> item (the queue's <c>RunningToken</c>) settles it as <c>Canceled</c>.</item>
///   <item>Stopping the loop stops the <i>claiming</i>, not the turn in flight: a half-run turn is worse than a
///     late shutdown. An interrupted item is re-queued by the queue on the next start.</item>
///   <item>A redelivered item (re-queued after a crash) whose turn already ended is not run twice.</item>
/// </list>
/// </para>
/// </summary>
public sealed class TurnLoop
{
    private readonly IInputQueue _queue;
    private readonly AgentTurnRunner _runner;
    private readonly ISessionStore _session;

    private int _completed;
    private int _failed;
    private int _canceled;
    private int _skipped;

    public TurnLoop(IInputQueue queue, AgentTurnRunner runner, ISessionStore session)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public TurnLoopStats Stats => new(
        Volatile.Read(ref _completed),
        Volatile.Read(ref _failed),
        Volatile.Read(ref _canceled),
        Volatile.Read(ref _skipped));

    /// <summary>Runs until <paramref name="ct"/> is cancelled. Returns when the queue yields nothing more.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            InputItem? item;

            try
            {
                item = await _queue.TakeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled while waiting: nothing was claimed, so there is nothing to settle.
                return;
            }

            if (item is null)
            {
                return;
            }

            await ProcessAsync(item).ConfigureAwait(false);
        }
    }

    /// <summary>Runs exactly one claimed item. Exposed so a host can drain deterministically in a test.</summary>
    public async Task ProcessAsync(InputItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (await AlreadyTurnedAsync(item).ConfigureAwait(false))
        {
            // At-least-once delivery: the item was re-queued after an interruption, but its turn already ran
            // to an end. Re-running it would duplicate the conversation.
            Interlocked.Increment(ref _skipped);
            await _queue.CompleteAsync(item.ItemId, InputItemStatus.Done).ConfigureAwait(false);
            return;
        }

        try
        {
            // The turn observes the item's own cancel token, so a UI cancel interrupts it, while stopping the
            // loop does not.
            await _runner.RunAsync(item, _queue.RunningToken).ConfigureAwait(false);
            Interlocked.Increment(ref _completed);
            await _queue.CompleteAsync(item.ItemId, InputItemStatus.Done).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _canceled);
            await _queue.CompleteAsync(item.ItemId, InputItemStatus.Canceled).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref _failed);
            await _queue.CompleteAsync(item.ItemId, InputItemStatus.Failed, error.Message).ConfigureAwait(false);
        }
    }

    /// <summary>True when the log already carries a finished turn for this item.</summary>
    private async Task<bool> AlreadyTurnedAsync(InputItem item)
    {
        await foreach (var e in _session.ReadAllAsync().ConfigureAwait(false))
        {
            if (e.Kind == SessionEventKind.TurnEnd
                && e.Meta is not null
                && e.Meta.TryGetValue("itemId", out var itemId)
                && string.Equals(itemId, item.ItemId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
