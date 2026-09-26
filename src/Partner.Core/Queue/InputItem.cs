using Partner.Core.Contracts;

namespace Partner.Core.Queue;

/// <summary>
/// 队列中的一条输入（设计文档 §2.2）。不可变值对象：每次状态变更都产生一个新实例，
/// 并作为一条新记录追加到日志（同一 <see cref="ItemId"/> 以最后一条记录为准）。
/// </summary>
public sealed class InputItem
{
    /// <summary>调用方提供的唯一标识（非空）。</summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>由队列分配：从 1 开始、单调递增、永不复用。</summary>
    public long Seq { get; init; }

    /// <summary>入队时间（由队列时钟给出）。</summary>
    public DateTimeOffset EnqueuedAt { get; init; }

    /// <summary>输入来源。</summary>
    public InputSource Source { get; init; }

    /// <summary>对队列完全不透明的载荷。</summary>
    public string Payload { get; init; } = string.Empty;

    /// <summary>幂等键（非空）：去重依据，跨重启生效。</summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>当前状态。</summary>
    public InputItemStatus Status { get; init; }

    /// <summary>被消费者取走的时刻。</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>落定为终态的时刻。</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>已被消费者取走的次数（进程中断后重新入队时 +1）。</summary>
    public int Attempt { get; init; }

    /// <summary>失败原因（仅 <see cref="InputItemStatus.Failed"/> 时写入）。</summary>
    public string? Error { get; init; }
}
