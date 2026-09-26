using Partner.Core.Contracts;

namespace Partner.Core.Queue;

/// <summary>
/// 单输入队列（设计文档 §2.2）：严格 FIFO、幂等入队、有界容量、单消费者、崩溃可恢复。
/// </summary>
public interface IInputQueue : IAsyncDisposable
{
    /// <summary>
    /// 入队并返回该项的 <c>Seq</c>。队列满时等待（绝不丢弃、绝不抛容量错误）。
    /// 幂等：<see cref="InputItem.IdempotencyKey"/> 已存在（含已消费过的）时不再入队，返回已有项的 <c>Seq</c>。
    /// </summary>
    ValueTask<long> EnqueueAsync(InputItem item, CancellationToken ct = default);

    /// <summary>
    /// 取队首项并转为运行中。无项可取时等待；<paramref name="ct"/> 取消时返回 <c>null</c>。
    /// </summary>
    ValueTask<InputItem?> TakeAsync(CancellationToken ct = default);

    /// <summary>当前快照。</summary>
    QueueState Snapshot();

    /// <summary>
    /// 取消一项。<c>Queued</c>：置为 <c>Canceled</c> 并持久化（该项永不交给消费者），返回 <c>true</c>；
    /// <c>Running</c>：触发 <c>RunningToken</c> 取消并返回 <c>true</c>（状态由消费者用 <see cref="CompleteAsync"/> 落定）；
    /// 未知或已终态的项返回 <c>false</c>。
    /// </summary>
    Task<bool> CancelAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// 落定一项的状态，只接受 <c>Done</c>/<c>Failed</c>/<c>Canceled</c>（否则抛 <see cref="ArgumentException"/>）。
    /// 未知 <paramref name="itemId"/> 抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    Task CompleteAsync(string itemId, InputItemStatus status, string? error = null, CancellationToken ct = default);

    /// <summary>按写入顺序读出持久化日志（每条状态变更一条记录，不做去重）。</summary>
    IAsyncEnumerable<InputItem> ReadLogAsync(CancellationToken ct = default);
}
