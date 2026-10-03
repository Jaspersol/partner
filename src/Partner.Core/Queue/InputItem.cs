using Partner.Core.Contracts;

namespace Partner.Core.Queue;

/// <summary>
/// 队列中的一条输入（设计文档 §2.2）。不可变值对象（record）：每次状态变更都产生一个新实例，
/// 并作为一条新记录追加到日志（同一 <see cref="ItemId"/> 以最后一条记录为准）。
/// <para>
/// 是 record 而非逐字段手抄的类：状态推进用 <c>with</c>，新增字段不会被漏掉——附图曾经就是这样在
/// 项推进状态时被静默丢掉的。
/// </para>
/// </summary>
public sealed record InputItem
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

    /// <summary>
    /// 附图（媒体库里的 id，按用户附加顺序）。纯文本输入为空。**字节不进日志**：日志里只记 id，
    /// 图片本体在 media 目录，否则会话文件会被 base64 撑成兆级。
    /// </summary>
    public IReadOnlyList<string> MediaIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 这一条消息选的思考挡位（<c>reasoning_effort</c>）。空 = 用设置里的默认挡位。
    /// <para>
    /// 随项走而不是随全局设置走：挡位是**逐条消息**的选择，一句话重一点还是轻一点由发消息的人决定，
    /// 改了设置也不该把已经排队的那些条目的挡位改掉——它们入队时的选择是那一条消息的一部分。
    /// </para>
    /// </summary>
    public string ReasoningEffort { get; init; } = string.Empty;

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
