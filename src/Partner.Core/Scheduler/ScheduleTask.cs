using System.Text.Json.Serialization;
using Partner.Core.Contracts;

namespace Partner.Core.Scheduler;

public enum ScheduleTaskStatus
{
    /// <summary>Normal: participates in the due set.</summary>
    Active,

    /// <summary>Paused or exhausted: excluded from the due set.</summary>
    Inactive,

    /// <summary>Repeated delivery failures. <b>Still schedulable</b> — a failing task must never be lost (design doc §2.4).</summary>
    Failed,
}

/// <summary>
/// A durable scheduled task (design doc §2.2). It carries no session id: firing it only enqueues an
/// <see cref="Partner.Core.Queue.InputItem"/> with <see cref="InputSource.Schedule"/>.
/// </summary>
public sealed record ScheduleTask
{
    public required string TaskId { get; init; }

    public required ScheduleTrigger Trigger { get; init; }

    /// <summary>Opaque payload handed to the queue item.</summary>
    public required string Payload { get; init; }

    public ScheduleTaskStatus Status { get; init; } = ScheduleTaskStatus.Active;

    /// <summary>Materialized next occurrence. The due set is derived from this field, never from the trigger.</summary>
    public DateTimeOffset NextScheduledAt { get; init; }

    public DateTimeOffset? LastDeliveryAt { get; init; }

    /// <summary>Consecutive delivery failures; reset to 0 on a successful delivery.</summary>
    public int Attempt { get; init; }

    public string? LastError { get; init; }
}

/// <summary>
/// Proof that one occurrence was handed to the input queue (design doc §2.2). Written <b>before</b> the
/// task state advances, so a crash in between can never double-deliver.
/// </summary>
public sealed record DeliveryReceipt
{
    public required string TaskId { get; init; }

    public required DateTimeOffset OccurrenceAt { get; init; }

    public required DateTimeOffset DeliveredAt { get; init; }

    public required string ItemId { get; init; }

    public int Attempt { get; init; }

    /// <summary>Dedup key: <c>(TaskId, OccurrenceAt)</c>.</summary>
[JsonIgnore]
    public string Key => $"{TaskId}@{OccurrenceAt.UtcDateTime:O}";
}
