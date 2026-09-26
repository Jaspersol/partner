namespace Partner.Core.Queue;

/// <summary>
/// 队列的一致性快照（设计文档 §2.2）。
/// </summary>
/// <param name="HeadSeq">下一个将被分配的 <c>Seq</c>。</param>
/// <param name="RunningItemId">当前正在运行的项；没有则为 <c>null</c>。</param>
/// <param name="Depth">队列中等待的项数（不含正在运行的项）。</param>
/// <param name="Paused">保留字段；单输入队列恒为 <c>false</c>。</param>
public sealed record QueueState(long HeadSeq, string? RunningItemId, int Depth, bool Paused);
