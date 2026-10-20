using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Partner.Core.Contracts;
using Partner.Core.Storage;

namespace Partner.Core.Queue;

/// <summary>
/// JSONL-persisted implementation of <see cref="IInputQueue"/>.
/// <para>
/// <b>Availability</b>: recovery happens <b>inside the constructor</b>; once it returns the queue is usable,
/// with no separate <c>Open()</c>.
/// </para>
/// <para>
/// <b>Persistence</b>: the log lives at <c>&lt;directory&gt;/queue/items.jsonl</c>, append-only, never
/// truncated. Each state change appends one record and calls <see cref="IAppendOnlySink.FlushToDisk"/> as a
/// durability barrier before returning. On replay the last record for an <c>itemId</c> wins, ordered by
/// <c>Seq</c>.
/// </para>
/// <para>
/// <b>Recovery</b>: construction first drops a crash-torn half line with
/// <see cref="FileAppendOnlySink.RepairTail"/>, then replays the log: <c>Queued</c> items are re-queued by
/// <c>Seq</c>; <c>Running</c> items are treated as interrupted and re-queued with <c>Attempt+1</c> (and a record
/// appended); re-queues are capped at <see cref="MaxAttempts"/> (past that the item settles as <c>Failed</c>);
/// <c>Done</c>/<c>Failed</c>/<c>Canceled</c> are not re-queued, but their idempotency keys still de-duplicate.
/// </para>
/// <para>
/// <b>Concurrency</b>: multiple producers, one consumer. Transfer uses a bounded <see cref="Channel{T}"/> whose
/// capacity is guarded by a semaphore (slots), so a replay backlog larger than the capacity cannot deadlock.
/// All state dictionaries are protected by <c>_gate</c>.
/// </para>
/// </summary>
public sealed class JsonlInputQueue : IInputQueue
{
    private const string QueueDirectoryName = "queue";
    private const string LogFileName = "items.jsonl";
    private const string KeysFileName = "keys.jsonl";

    /// <summary>
    /// How many times one interrupted item may be started in total, the first try included.
    /// <para>
    /// A <c>Running</c> item is re-run from the beginning, not resumed: nothing records which of its tool calls
    /// already happened. Without a cap, an item that reliably wedges the turn would be retried on every start,
    /// re-running whatever the turn had already done. Past the cap the item settles as the failure it is.
    /// </para>
    /// </summary>
    public const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly FileAppendOnlySink _sink;
    private readonly FileAppendOnlySink _keys;
    private readonly Channel<InputItem> _channel;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _enqueueGate = new(1, 1);
    private readonly Dictionary<string, InputItem> _items = new(StringComparer.Ordinal);
    /// <summary>
    /// Idempotency key → the seq that key was accepted at.
    /// <para>
    /// The seq rather than the item id: it is all de-duplication needs to answer, and it stays valid for keys
    /// whose item finished long ago and was dropped from <see cref="_items"/>. Holding the item itself would pin
    /// every completed message in memory for the life of the process.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, long> _byKey = new(StringComparer.Ordinal);

    private long _nextSeq = 1;
    private int _queuedCount;
    private string? _runningItemId;
    private CancellationTokenSource? _runningCts;
    private bool _disposed;

    /// <param name="directory">Data root; the log is written to <c>&lt;directory&gt;/queue/items.jsonl</c>.</param>
    /// <param name="capacity">Maximum number of items that may wait in the queue; when full,
    /// <see cref="EnqueueAsync"/> waits.</param>
    /// <param name="clock">Time source; tests can inject a fake clock. Defaults to
    /// <see cref="TimeProvider.System"/>.</param>
    public JsonlInputQueue(string directory, int capacity = 1024, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _clock = clock ?? TimeProvider.System;

        var queueDirectory = Path.Combine(directory, QueueDirectoryName);
        Directory.CreateDirectory(queueDirectory);

        _sink = new FileAppendOnlySink(Path.Combine(queueDirectory, LogFileName));
        _sink.RepairTail();

        // Idempotency keys outlive the items they name, so they cannot live in the items log. See `Compact`
        // for why the split exists.
        _keys = new FileAppendOnlySink(Path.Combine(queueDirectory, KeysFileName));
        _keys.RepairTail();

        var replay = Replay();

        // The channel bound is at least the replay backlog so construction cannot overflow it; runtime writes
        // are held to `capacity` by the slot semaphore.
        _channel = Channel.CreateBounded<InputItem>(new BoundedChannelOptions(Math.Max(capacity, replay.Count))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _slots = new SemaphoreSlim(Math.Max(0, capacity - replay.Count), capacity);

        foreach (var item in replay)
        {
            if (!_channel.Writer.TryWrite(item))
            {
                throw new InvalidOperationException("重放写入队列通道失败。");
            }
        }
    }

    /// <summary>
    /// Cancellation token for the item last handed out by <see cref="TakeAsync"/>; <see cref="CancellationToken.None"/>
    /// before anything is taken.
    /// </summary>
    public CancellationToken RunningToken
    {
        get
        {
            lock (_gate)
            {
                return _runningCts?.Token ?? CancellationToken.None;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<long> EnqueueAsync(InputItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.ItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.IdempotencyKey);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Serialise producers: guarantees enqueue order == Seq order == channel order, and gives the capacity
        // wait a deterministic order.
        await _enqueueGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_byKey.TryGetValue(item.IdempotencyKey, out var knownSeq))
                {
                    return knownSeq;
                }
            }

            // Wait for a free slot; the consumer releases one when it takes an item.
            await _slots.WaitAsync(ct).ConfigureAwait(false);

            lock (_gate)
            {
                // 'with' rather than a field-by-field copy: the queue owns the seq, the clock and the state, and
                // anything else the caller sent (attachments included) rides along untouched.
                var stored = item with
                {
                    Seq = _nextSeq++,
                    EnqueuedAt = _clock.GetUtcNow(),
                    Status = InputItemStatus.Queued,
                    StartedAt = null,
                    FinishedAt = null,
                    Attempt = 0,
                    Error = null,
                };

                Persist(stored);
                _queuedCount++;

                if (!_channel.Writer.TryWrite(stored))
                {
                    throw new InvalidOperationException("写入队列通道失败。");
                }

                return stored.Seq;
            }
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<InputItem?> TakeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            InputItem dequeued;
            try
            {
                dequeued = await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ChannelClosedException)
            {
                return null;
            }

            _slots.Release();

            lock (_gate)
            {
                // A miss is not an error: the item was cancelled or settled while still sitting in the channel,
                // and that path deliberately drops it from _items. Either way it must not reach a consumer.
                if (!_items.TryGetValue(dequeued.ItemId, out var latest) || latest.Status != InputItemStatus.Queued)
                {
                    continue;
                }

                var running = Copy(latest, InputItemStatus.Running, _clock.GetUtcNow(), null, latest.Attempt, latest.Error);
                Persist(running);
                _queuedCount--;

                _runningItemId = running.ItemId;
                // The old CTS is not disposed: RunningToken must stay safe to read for the queue's lifetime.
                _runningCts = new CancellationTokenSource();

                return running;
            }
        }
    }

    /// <inheritdoc />
    public QueueState Snapshot()
    {
        lock (_gate)
        {
            return new QueueState(_nextSeq, _runningItemId, _queuedCount, Paused: false);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<InputItem> ActiveItems()
    {
        lock (_gate)
        {
            // Ordered here rather than by the caller: `_items` is a dictionary, and insertion order would make
            // the queue look like it sometimes reordered itself.
            return _items.Values.OrderBy(i => i.Seq).ToList();
        }
    }

    /// <inheritdoc />
    public Task<bool> CancelAsync(string itemId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!_items.TryGetValue(itemId, out var item))
            {
                return Task.FromResult(false);
            }

            switch (item.Status)
            {
                case InputItemStatus.Queued:
                    // Persist the terminal state now; any leftover copy in the channel is skipped on dequeue.
                    Persist(Copy(item, InputItemStatus.Canceled, item.StartedAt, item.FinishedAt, item.Attempt, item.Error));
                    _queuedCount--;

                    // Settled, so out of memory: a cancelled item will not be taken, cancelled or completed
                    // again, and every one of those looks it up in _items.
                    _items.Remove(itemId);
                    return Task.FromResult(true);

                case InputItemStatus.Running:
                    if (string.Equals(_runningItemId, itemId, StringComparison.Ordinal))
                    {
                        _runningCts?.Cancel();
                    }

                    return Task.FromResult(true);

                default:
                    return Task.FromResult(false);
            }
        }
    }

    /// <inheritdoc />
    public Task CompleteAsync(string itemId, InputItemStatus status, string? error = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (status is not (InputItemStatus.Done or InputItemStatus.Failed or InputItemStatus.Canceled))
        {
            throw new ArgumentException("CompleteAsync 只接受 Done/Failed/Canceled。", nameof(status));
        }

        lock (_gate)
        {
            if (!_items.TryGetValue(itemId, out var item))
            {
                // Completing a settled item is a normal path, not a mistake: a redelivered item whose turn
                // already ended is marked Done again without being re-run, and that is exactly when it is no
                // longer in memory. Idempotent on purpose — throwing would turn crash recovery into a crash.
                if (IsSettled(itemId))
                {
                    return Task.CompletedTask;
                }

                throw new InvalidOperationException($"未知的 itemId：{itemId}");
            }

            var finished = Copy(
                item,
                status,
                item.StartedAt,
                _clock.GetUtcNow(),
                item.Attempt,
                status == InputItemStatus.Failed ? error : null);

            Persist(finished);

            if (item.Status == InputItemStatus.Queued)
            {
                // Settled before it was taken: it is no longer waiting (a leftover channel copy is skipped).
                _queuedCount--;
            }

            // A settled item is never taken, cancelled or completed again — every one of those reads _items
            // to ask what state it is in. Keeping it would grow the map by every message the process ever ran;
            // the log still has it, which is what a restart replays.
            _items.Remove(itemId);

            if (string.Equals(_runningItemId, itemId, StringComparison.Ordinal))
            {
                _runningItemId = null;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether the log's last word about this item is a settled one.
    /// <para>
    /// Read from the log rather than memory on purpose: memory is exactly what was dropped, and a restart must
    /// reach the same answer. The scan is acceptable because this only runs for an id that is already gone.
    /// </para>
    /// </summary>
    private bool IsSettled(string itemId)
    {
        var status = InputItemStatus.Queued;

        foreach (var line in _sink.ReadLines())
        {
            var item = TryParse(line);
            if (item is not null && string.Equals(item.ItemId, itemId, StringComparison.Ordinal))
            {
                status = item.Status;
            }
        }

        return status is InputItemStatus.Done or InputItemStatus.Failed or InputItemStatus.Canceled;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<InputItem> ReadLogAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IReadOnlyList<string> lines;
        lock (_gate)
        {
            lines = _sink.ReadLines();
        }

        await Task.Yield();

        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();

            var item = TryParse(line);
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        _sink.Dispose();

        // Both handles, not one. A sink holds its file open with FileShare.Read, so a sink that is not closed
        // keeps the next queue from opening the same directory at all.
        _keys.Dispose();
        _slots.Dispose();
        _enqueueGate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Replays the log and prepares the items to enqueue. The last record per itemId wins; <c>Running</c> items
    /// are re-queued with <c>Attempt+1</c> and a record appended. Returns the items to enqueue in ascending
    /// <c>Seq</c> order.
    /// <para>
    /// <b>Only active items stay in memory.</b> The log only grows, and replaying all of it into <c>_items</c>
    /// would make startup memory proportional to the whole history.
    /// <para>
    /// Settled items do <em>not</em> enter <c>_items</c>: <c>TakeAsync</c>, <c>CancelAsync</c> and
    /// <c>CompleteAsync</c> read it only to ask what state an item is in, and a <c>Done</c> item is never taken,
    /// cancelled or completed again. Crash recovery relies on the log's <c>Done</c> record, not on this map.
    /// </para>
    /// <para>
    /// <c>_byKey</c> still takes every key: idempotency must recognise a key it has seen, and a missing key
    /// would enqueue the same request twice.
    /// </para>
    /// </summary>
    private List<InputItem> Replay()
    {
        var latest = new Dictionary<string, InputItem>(StringComparer.Ordinal);
        long highestSeq = 0;

        foreach (var line in _sink.ReadLines())
        {
            var item = TryParse(line);
            if (item is null)
            {
                continue;
            }

            highestSeq = Math.Max(highestSeq, item.Seq);
            latest[item.ItemId] = item; // the last record wins
        }

        _nextSeq = highestSeq + 1;

        // Keys recorded by earlier compactions: these name items the log no longer holds. Losing them would
        // let a caller retrying an old request enqueue a second copy, which the idempotency key exists to prevent.
        foreach (var line in _keys.ReadLines())
        {
            try
            {
                var record = JsonSerializer.Deserialize<KeyRecord>(line, JsonOptions);
                if (record is not null)
                {
                    _byKey[record.Key] = record.Seq;
                }
            }
            catch (JsonException)
            {
                // A torn line, same as anywhere else in these logs: RepairTail drops the tail, and a damaged
                // middle line costs one key's worth of de-duplication, not the queue.
            }
        }

        var requeue = new List<InputItem>(latest.Count);
        foreach (var item in latest.Values.OrderBy(i => i.Seq))
        {
            _byKey[item.IdempotencyKey] = item.Seq;

            switch (item.Status)
            {
                case InputItemStatus.Queued:
                    _items[item.ItemId] = item;
                    requeue.Add(item);
                    break;

                case InputItemStatus.Running:
                    if (item.Attempt + 1 >= MaxAttempts)
                    {
                        // Attempts exhausted: settle as failed, with the reason left in the log. Failing beats
                        // re-queueing, which would only show the same wedge again.
                        Persist(Copy(
                            item,
                            InputItemStatus.Failed,
                            null,
                            _clock.GetUtcNow(),
                            item.Attempt,
                            $"该项被中断 {item.Attempt + 1} 次仍未跑完，已不再重试。"));
                        _items.Remove(item.ItemId); // settled items do not stay in memory (see above)
                        break;
                    }

                    // Interrupted process: keep the original Seq (order unchanged) and re-queue with Attempt+1.
                    var retried = Copy(item, InputItemStatus.Queued, null, null, item.Attempt + 1, null);
                    Persist(retried);
                    requeue.Add(retried);
                    break;

                default:
                    // Done/Failed/Canceled: not re-queued, not kept in memory. See above.
                    break;
            }
        }

        _queuedCount = requeue.Count;

        // After, not before: compaction re-reads the log, so it must see the re-queue records replay just appended.
        Compact();

        return requeue;
    }

    private static InputItem? TryParse(string line)
    {
        try
        {
            var item = JsonSerializer.Deserialize<InputItem>(line, JsonOptions);
            return string.IsNullOrEmpty(item?.ItemId) ? null : item;
        }
        catch (JsonException)
        {
            // A complete but unparseable line: skip it so one damaged line cannot take the whole queue down.
            return null;
        }
    }

    /// <summary>Produces a new instance after a state change: the same item with only the state fields replaced.</summary>
    private static InputItem Copy(
        InputItem source,
        InputItemStatus status,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        int attempt,
        string? error) => source with
        {
            Status = status,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            Attempt = attempt,
            Error = error,
        };

    /// <summary>One idempotency key, and the sequence number it was accepted at.</summary>
    private sealed record KeyRecord(string Key, long Seq);

    /// <summary>
    /// Drops settled items from the log, leaving only what still has to run, after moving their idempotency
    /// keys into <c>keys.jsonl</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once at construction, after replay. The log's job is crash recovery and ordering, which a settled
    /// item no longer serves; keeping them made every queue read pay for the whole history.
    /// </para>
    /// <para>
    /// It re-reads the log rather than reusing replay's snapshot on purpose: replay re-queues an interrupted
    /// item by appending a fresh <c>Queued</c> record with <c>Attempt+1</c>, and compacting from the pre-requeue
    /// snapshot would write the old <c>Running</c> record back, granting the item another run and undoing the
    /// retry cap.
    /// </para>
    /// <para>
    /// Keys are written first deliberately: in the other order a crash between the two would lose both the
    /// record and its key, and the record is the part that still has to run.
    /// </para>
    /// </remarks>
    private void Compact()
    {
        var latest = new Dictionary<string, InputItem>(StringComparer.Ordinal);
        foreach (var line in _sink.ReadLines())
        {
            var item = TryParse(line);
            if (item is not null)
            {
                latest[item.ItemId] = item;
            }
        }

        var keep = new List<string>();
        var movedKeys = 0;

        foreach (var item in latest.Values.OrderBy(i => i.Seq))
        {
            if (item.Status is InputItemStatus.Queued or InputItemStatus.Running)
            {
                keep.Add(JsonSerializer.Serialize(item, JsonOptions));
                continue;
            }

            _keys.Append(JsonSerializer.Serialize(new KeyRecord(item.IdempotencyKey, item.Seq), JsonOptions));
            movedKeys++;
        }

        if (movedKeys == 0)
        {
            // Nothing settled, so nothing to drop; not rewriting keeps startup from touching an untouched file.
            return;
        }

        _keys.FlushToDisk();
        _sink.Rewrite(keep);
    }

    /// <summary>Appends a record and flushes it to disk (durability barrier). The caller must hold <c>_gate</c>.</summary>
    private void Persist(InputItem item)
    {
        _items[item.ItemId] = item;
        _byKey[item.IdempotencyKey] = item.Seq;
        _sink.Append(JsonSerializer.Serialize(item, JsonOptions));
        _sink.FlushToDisk();
    }
}
