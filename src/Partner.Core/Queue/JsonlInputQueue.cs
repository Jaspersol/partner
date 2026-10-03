using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Partner.Core.Contracts;
using Partner.Core.Storage;

namespace Partner.Core.Queue;

/// <summary>
/// <see cref="IInputQueue"/> 的 JSONL 持久化实现（设计文档 §2.2 / §2.3）。
/// <para>
/// <b>可用性</b>：恢复在<b>构造函数内</b>完成——构造返回后队列即可用，无需额外的 <c>Open()</c>。
/// </para>
/// <para>
/// <b>持久化</b>：日志位于 <c>&lt;directory&gt;/queue/items.jsonl</c>，只追加、从不截断；
/// 每次状态变更追加一条记录，并在返回前调用 <see cref="IAppendOnlySink.FlushToDisk"/> 作为持久化屏障。
/// 重放时同一 <c>itemId</c> 以最后一条记录为准，顺序由 <c>Seq</c> 决定。
/// </para>
/// <para>
/// <b>恢复</b>：构造时先用 <see cref="FileAppendOnlySink.RepairTail"/> 丢弃崩溃产生的半行，再重放日志：
/// <c>Queued</c> 项按 <c>Seq</c> 重新入队；<c>Running</c> 项视为进程中断，以 <c>Attempt+1</c> 重新入队（并追加一条记录）；
/// 重新入队的次数以 <see cref="MaxAttempts"/> 为限（超过则直接落为 <c>Failed</c>）；
/// <c>Done</c>/<c>Failed</c>/<c>Canceled</c> 不重入队，但其幂等键仍参与去重。
/// </para>
/// <para>
/// <b>并发</b>：多生产者 + 单消费者。传输用有界 <see cref="Channel{T}"/>；容量由信号量（槽位）把守，
/// 因此即使重放积压超过容量也不会死锁。所有状态字典由 <c>_gate</c> 锁保护。
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
    /// A <c>Running</c> item is re-run from the beginning, not resumed: there is no record of which of its
    /// tool calls already happened, so a restart re-issues them. Without a cap, one item that reliably wedges
    /// the turn is retried on every single start, each retry re-running whatever the turn had already done —
    /// and a queue that reports "running" for something that never finishes gives the user nothing to do but
    /// kill the process, which is the retry. So the attempts are counted, and the last one is recorded as the
    /// failure it is instead of being started again.
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
    /// The seq rather than the item id, because that is all the de-duplication needs to answer and it is
    /// true for keys whose item finished long ago and was dropped from <see cref="_items"/>. Holding the item
    /// itself would pin every completed message in memory for the life of the process.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, long> _byKey = new(StringComparer.Ordinal);

    private long _nextSeq = 1;
    private int _queuedCount;
    private string? _runningItemId;
    private CancellationTokenSource? _runningCts;
    private bool _disposed;

    /// <param name="directory">数据根目录；日志写在 <c>&lt;directory&gt;/queue/items.jsonl</c>。</param>
    /// <param name="capacity">队列中最多可等待的项数；满时 <see cref="EnqueueAsync"/> 等待。</param>
    /// <param name="clock">时间源，测试可注入假时钟；默认 <see cref="TimeProvider.System"/>。</param>
    public JsonlInputQueue(string directory, int capacity = 1024, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _clock = clock ?? TimeProvider.System;

        var queueDirectory = Path.Combine(directory, QueueDirectoryName);
        Directory.CreateDirectory(queueDirectory);

        _sink = new FileAppendOnlySink(Path.Combine(queueDirectory, LogFileName));
        _sink.RepairTail();

        // Idempotency keys outlive the items they name, so they cannot live in the items log. See
        // `Compact` for why the split exists at all.
        _keys = new FileAppendOnlySink(Path.Combine(queueDirectory, KeysFileName));
        _keys.RepairTail();

        var replay = Replay();

        // 通道上界不小于重放积压，保证构造期写入不会溢出；运行期写入由槽位信号量限制在 capacity 以内。
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
    /// 最近一次 <see cref="TakeAsync"/> 交出的项所对应的取消令牌；尚未取出任何项时为 <see cref="CancellationToken.None"/>。
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

        // 串行化生产者：既保证「入队顺序 == Seq 顺序 == 通道顺序」，也让容量等待有确定的先后。
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

            // 有界容量：满时在此等待，直到消费者取走一项释放槽位。
            await _slots.WaitAsync(ct).ConfigureAwait(false);

            lock (_gate)
            {
                // 'with' rather than a field-by-field copy: the queue owns the seq, the clock and the state,
                // and anything else the caller sent (attachments included) rides along untouched.
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
                // A miss is not an error: the item was cancelled or settled while still sitting in the
                // channel, and the cancel path deliberately drops it from _items. Either way it must not be
                // handed to a consumer.
                if (!_items.TryGetValue(dequeued.ItemId, out var latest) || latest.Status != InputItemStatus.Queued)
                {
                    // 排队期间被取消或落定：永不交给消费者，跳过（其槽位已随出队释放）。
                    continue;
                }

                var running = Copy(latest, InputItemStatus.Running, _clock.GetUtcNow(), null, latest.Attempt, latest.Error);
                Persist(running);
                _queuedCount--;

                _runningItemId = running.ItemId;
                // 不释放旧 CTS：RunningToken 在队列存活期内始终可安全读取。
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
            // Ordered here rather than by the caller: `_items` is a dictionary, and handing back insertion
            // order by accident would make a queue that sometimes looks like it reordered itself.
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
                    // 终态立即落盘；通道里的残留条目会在出队时被跳过。
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
                // longer in memory. Idempotent here on purpose — throwing would turn crash recovery into a
                // crash.
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
                // 尚未被取走就落定：它不再是等待项（通道里的残留条目会在出队时被跳过）。
                _queuedCount--;
            }

            // A settled item is never taken, cancelled or completed again — every one of those reads _items
            // to ask what state it is in, and this one has just answered. Keeping it would grow the map by
            // every message the process ever ran; the log still has it, which is what a restart replays.
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
    /// Read from the log rather than kept in memory on purpose: memory is exactly what was dropped, and a
    /// restart must reach the same answer. It costs a scan, which is fine because this only runs for an id
    /// that is already gone — the rare case, not the hot one.
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

        // Both handles, not one. A sink holds its file open with FileShare.Read — "readers may tail it" — so a
        // sink that is not closed keeps the *next* queue from opening the same directory at all. That is how a
        // "reopen the queue over the same workspace" test fails with a sharing violation on a fresh-looking path.
        _keys.Dispose();
        _slots.Dispose();
        _enqueueGate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 重放日志并准备待入队项。同一 itemId 以最后一条记录为准；<c>Running</c> 项以 <c>Attempt+1</c> 重新入队并追加记录。
    /// 返回按 <c>Seq</c> 升序的待入队项。
    /// <para>
    /// <b>只把活跃项留在内存里。</b>日志只增不减，跑久了是几十万行，而全量重放进 <c>_items</c> 意味着启动
    /// 时的内存与整个历史的项数成正比——为一个用不到的数据结构付费。
    /// <para>
    /// 终态项<em>不</em>进 <c>_items</c>：<c>TakeAsync</c>、<c>CancelAsync</c>、<c>CompleteAsync</c> 读它，都是为了判断
    /// 「这一条现在是什么状态」——而一个 <c>Done</c> 的项永远不会再被取走、取消或完成。崩溃恢复靠的是日志里
    /// 那条 <c>Done</c> 记录本身（不重入队），不是这份内存。
    /// </para>
    /// <para>
    /// 但 <c>_byKey</c> 仍收全量：幂等去重要认出「这个键见过」，键本身很小（一条 GUID 字符串），而漏掉
    /// 一条会让同一请求重复入队、对话被污染两次。
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
            latest[item.ItemId] = item; // 最后一条记录生效
        }

        _nextSeq = highestSeq + 1;

        // Keys recorded by earlier compactions: these name items the log no longer holds. Losing them would
        // make a caller retrying an old request enqueue a second copy of it, which is the exact thing the
        // idempotency key exists to prevent.
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
                        // 已用尽尝试次数：落为失败，并把这个理由留在日志里。
                        // 直接失败而不是重新入队，是因为重新入队只会让人再看见一次同样的卡死。
                        Persist(Copy(
                            item,
                            InputItemStatus.Failed,
                            null,
                            _clock.GetUtcNow(),
                            item.Attempt,
                            $"该项被中断 {item.Attempt + 1} 次仍未跑完，已不再重试。"));
                        _items.Remove(item.ItemId); // 终态项不留内存（见上方说明）
                        break;
                    }

                    // 进程中断：保持原 Seq（顺序不变），Attempt+1 后重新入队。
                    var retried = Copy(item, InputItemStatus.Queued, null, null, item.Attempt + 1, null);
                    Persist(retried);
                    requeue.Add(retried);
                    break;

                default:
                    // Done/Failed/Canceled：不重入队，也不留在内存里。见上方说明。
                    break;
            }
        }

        _queuedCount = requeue.Count;

        // After, not before: compaction re-reads the log so it sees the re-queue records replay just appended.
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
            // 完整但无法解析的行：跳过，避免一行损坏让整个队列不可用。
            return null;
        }
    }

    /// <summary>产生一个状态变更后的新实例：同一个项，只换状态相关的字段。</summary>
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
    /// Drops the settled items from the log and leaves only what still has to run, moving their idempotency
    /// keys into <c>keys.jsonl</c> first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once, at construction, after replay has worked out what is still in play. The log's job is crash
    /// recovery and ordering; a finished item is neither. Leaving them in meant every read of the queue paid
    /// for the whole history — the API did exactly that on a five-second timer, and while holding the queue
    /// lock.
    /// </para>
    /// <para>
    /// It reads the log again rather than reusing replay's snapshot, and that is not tidiness. Replay
    /// re-queues an interrupted item by appending a fresh <c>Queued</c> record with <c>Attempt+1</c>; compacting
    /// from the pre-requeue snapshot would write the old <c>Running</c> record back and hand that item another
    /// run for free — quietly undoing the retry cap, which is the one thing standing between a wedged turn and
    /// a boot loop.
    /// </para>
    /// <para>
    /// Keys go first, deliberately. Written the other way round, a crash between the two loses both the record
    /// and its key — and the record is the part that still has to run.
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
            // Nothing settled, so there is nothing to drop — and not rewriting keeps start-up from touching a
            // file that does not need touching.
            return;
        }

        _keys.FlushToDisk();
        _sink.Rewrite(keep);
    }

    /// <summary>追加一条记录并落盘（持久化屏障）。调用方必须持有 <c>_gate</c>。</summary>
    private void Persist(InputItem item)
    {
        _items[item.ItemId] = item;
        _byKey[item.IdempotencyKey] = item.Seq;
        _sink.Append(JsonSerializer.Serialize(item, JsonOptions));
        _sink.FlushToDisk();
    }
}
