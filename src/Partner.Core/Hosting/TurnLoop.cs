using Partner.Core.Agent;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Hosting;

/// <summary>What the loop has done so far. Reported so a host can show whether work is moving.</summary>
public sealed record TurnLoopStats(int Completed, int Failed, int Canceled, int Skipped)
{
    public static TurnLoopStats Empty { get; } = new(0, 0, 0, 0);

    /// <summary>Serialized into the consumer's <c>stats.handled</c> on <c>/api/consumer</c>.</summary>
    public int Handled => Completed + Failed + Canceled + Skipped;
}

/// <summary>
/// The single consumer of the input queue: takes one item at a time, in arrival order, runs
/// a turn for it, and settles the item as <c>Done</c>/<c>Failed</c>/<c>Canceled</c>.
/// <para>
/// Invariants enforced here:
/// <list type="bullet">
///   <item>Exactly one item is in flight; the loop never overlaps turns.</item>
///   <item>Every claimed item is settled — an exception cannot leave an item stuck in <c>Running</c>.</item>
///   <item>Cancellation of a <c>Running</c> item (the queue's <c>RunningToken</c>) settles it as <c>Canceled</c>.</item>
///   <item>Stopping the loop stops the <i>claiming</i>, not the turn in flight: a half-run turn is worse than a
///     late shutdown. An interrupted item is re-queued by the queue on the next start.</item>
///   <item>A turn is bounded by <see cref="AgentTurnOptions.TurnTimeout"/>, and a turn that outlives it settles
///     its item as <c>Failed</c> and is let go of — waiting on a wedged turn is what parks the queue.</item>
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

    /// <summary>
    /// Work to run after a turn has ended, on this loop (auto-compaction is the one user today).
    /// </summary>
    private readonly Func<CancellationToken, Task>? _afterTurn;
    private readonly Func<InputItem, InputItemStatus, string?, Task>? _onSettled;

    public TurnLoop(
        IInputQueue queue,
        AgentTurnRunner runner,
        ISessionStore session,
        Func<CancellationToken, Task>? afterTurn = null,
        Func<InputItem, InputItemStatus, string?, Task>? onSettled = null)
    {
        _afterTurn = afterTurn;
        _onSettled = onSettled;
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

        var turnWatch = System.Diagnostics.Stopwatch.StartNew();
        var settled = await ProcessCoreAsync(item).ConfigureAwait(false);
        if (settled is not null)
        {
            Partner.Core.Diagnostics.LatencyTracker.Record("turn", turnWatch.Elapsed);
        }

        // Told how the item ended, so a scheduled task can show what its last run did. Best effort: the item is
        // already settled and this must not change that.
        if (settled is not null && _onSettled is not null)
        {
            try
            {
                await _onSettled(item, settled.Status, settled.Error).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Nothing to do: the outcome is recorded in the queue and the session either way.
            }
        }

        // After the turn, on this same loop: the queue has a single reader, so whatever the hook writes cannot
        // interleave with another turn. A hook that fails must not fail the item it followed — that item is
        // already settled.
        if (_afterTurn is not null)
        {
            try
            {
                await _afterTurn(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort by design; the next turn's pass tries again.
            }
        }
    }

    /// <summary>How an item ended, for the callback that wants to know. Null from <see cref="ProcessCoreAsync"/> means "nothing ran".</summary>
    private sealed record Settled(InputItemStatus Status, string? Error);

    private async Task<Settled?> ProcessCoreAsync(InputItem item)
    {
        if (await AlreadyTurnedAsync(item).ConfigureAwait(false))
        {
            // At-least-once delivery: the item was re-queued after an interruption, but its turn already ran
            // to an end. Re-running it would duplicate the conversation.
            Interlocked.Increment(ref _skipped);
            await _queue.CompleteAsync(item.ItemId, InputItemStatus.Done).ConfigureAwait(false);
            return null;
        }

        // The ceiling is the loop's, not the runner's. Without it a turn that never returns holds the only reader
        // this queue has, so the item stays Running, every message behind it waits, and the UI's "running" is a
        // lie with no way out but killing the process.

        // The turn observes the item's own cancel token, so a UI cancel interrupts it, while stopping the
        // loop does not.
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(_queue.RunningToken);

        try
        {
            var running = _runner.RunAsync(item, turn.Token);

            // Waited on with a bound rather than awaited outright. Cancelling the token does <i>not</i> bring
            // back a turn parked on an await that ignores it, so `await running` would keep waiting for exactly
            // the case this exists to catch. Waiting on the pair means the loop gives up on schedule whether or
            // not the turn is listening.
            var finished = await Task
                .WhenAny(running, Task.Delay(_runner.TurnTimeout, CancellationToken.None))
                .ConfigureAwait(false);

            if (finished != running)
            {
                // Asked to stop in case it is listening, then left alone: it is not waited for, and its result
                // belongs to a turn that no longer has an item.
                turn.Cancel();
                Forget(running);

                Interlocked.Increment(ref _failed);
                var overran = $"回合超过 {_runner.TurnTimeout.TotalSeconds:0} 秒未结束，已中止"
                    + "（多半是某个工具或模型调用没有返回；请看会话里最后一个 tool/call 定位）";
                await _queue.CompleteAsync(item.ItemId, InputItemStatus.Failed, overran).ConfigureAwait(false);
                return new Settled(InputItemStatus.Failed, overran);
            }

            await running.ConfigureAwait(false);
            Interlocked.Increment(ref _completed);
            await _queue.CompleteAsync(item.ItemId, InputItemStatus.Done).ConfigureAwait(false);
            return new Settled(InputItemStatus.Done, null);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _canceled);
            await _queue.CompleteAsync(item.ItemId, InputItemStatus.Canceled).ConfigureAwait(false);
            return new Settled(InputItemStatus.Canceled, null);
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref _failed);
            await _queue.CompleteAsync(item.ItemId, InputItemStatus.Failed, error.Message).ConfigureAwait(false);
            return new Settled(InputItemStatus.Failed, error.Message);
        }
    }

    /// <summary>
    /// Keeps an abandoned turn from resurfacing as an unobserved task exception. It is deliberately not
    /// awaited: the whole point is that the loop has stopped waiting for it.
    /// </summary>
    private static void Forget(Task abandoned) =>
        abandoned.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

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
