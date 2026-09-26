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
    private readonly Channel<InputItem> _channel;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _enqueueGate = new(1, 1);
    private readonly Dictionary<string, InputItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);

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
                if (_byKey.TryGetValue(item.IdempotencyKey, out var knownId))
                {
                    return _items[knownId].Seq;
                }
            }

            // 有界容量：满时在此等待，直到消费者取走一项释放槽位。
            await _slots.WaitAsync(ct).ConfigureAwait(false);

            lock (_gate)
            {
                var stored = new InputItem
                {
                    ItemId = item.ItemId,
                    Seq = _nextSeq++,
                    EnqueuedAt = _clock.GetUtcNow(),
                    Source = item.Source,
                    Payload = item.Payload,
                    IdempotencyKey = item.IdempotencyKey,
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
                var latest = _items[dequeued.ItemId];

                // 排队期间被取消：永不交给消费者，跳过（其槽位已随出队释放）。
                if (latest.Status != InputItemStatus.Queued)
                {
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

            if (string.Equals(_runningItemId, itemId, StringComparison.Ordinal))
            {
                _runningItemId = null;
            }
        }

        return Task.CompletedTask;
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
        _slots.Dispose();
        _enqueueGate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 重放日志并准备待入队项。同一 itemId 以最后一条记录为准；<c>Running</c> 项以 <c>Attempt+1</c> 重新入队并追加记录。
    /// 返回按 <c>Seq</c> 升序的待入队项。
    /// </summary>
    private List<InputItem> Replay()
    {
        var latest = new Dictionary<string, InputItem>(StringComparer.Ordinal);
        foreach (var line in _sink.ReadLines())
        {
            var item = TryParse(line);
            if (item is null)
            {
                continue;
            }

            latest[item.ItemId] = item; // 最后一条记录生效
        }

        var ordered = latest.Values.OrderBy(i => i.Seq).ToList();
        _nextSeq = ordered.Count == 0 ? 1 : ordered[^1].Seq + 1;

        var requeue = new List<InputItem>(ordered.Count);
        foreach (var item in ordered)
        {
            _items[item.ItemId] = item;
            _byKey[item.IdempotencyKey] = item.ItemId;

            switch (item.Status)
            {
                case InputItemStatus.Queued:
                    requeue.Add(item);
                    break;

                case InputItemStatus.Running:
                    // 进程中断：保持原 Seq（顺序不变），Attempt+1 后重新入队。
                    var retried = Copy(item, InputItemStatus.Queued, null, null, item.Attempt + 1, null);
                    Persist(retried);
                    requeue.Add(retried);
                    break;

                default:
                    break; // Done/Failed/Canceled 不重入队
            }
        }

        _queuedCount = requeue.Count;
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

    /// <summary>产生一个状态变更后的新实例（<see cref="InputItem"/> 是不可变类，不是 record）。</summary>
    private static InputItem Copy(
        InputItem source,
        InputItemStatus status,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        int attempt,
        string? error) => new()
        {
            ItemId = source.ItemId,
            Seq = source.Seq,
            EnqueuedAt = source.EnqueuedAt,
            Source = source.Source,
            Payload = source.Payload,
            IdempotencyKey = source.IdempotencyKey,
            Status = status,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            Attempt = attempt,
            Error = error,
        };

    /// <summary>追加一条记录并落盘（持久化屏障）。调用方必须持有 <c>_gate</c>。</summary>
    private void Persist(InputItem item)
    {
        _items[item.ItemId] = item;
        _byKey[item.IdempotencyKey] = item.ItemId;
        _sink.Append(JsonSerializer.Serialize(item, JsonOptions));
        _sink.FlushToDisk();
    }
}
