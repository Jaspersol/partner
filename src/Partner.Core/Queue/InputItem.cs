using Partner.Core.Contracts;

namespace Partner.Core.Queue;

/// <summary>
/// One input in the queue. Immutable value object (record): every state change produces a new instance,
/// appended to the log as a new record (the last record for an <see cref="ItemId"/> wins).
/// <para>
/// A record rather than a hand-written class: state advances with <c>with</c>, so new fields cannot be
/// dropped by accident when an item changes state.
/// </para>
/// </summary>
public sealed record InputItem
{
    /// <summary>Caller-supplied unique identifier (non-empty).</summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>Assigned by the queue: starts at 1, monotonic, never reused.</summary>
    public long Seq { get; init; }

    /// <summary>Enqueue time, from the queue's clock.</summary>
    public DateTimeOffset EnqueuedAt { get; init; }

    /// <summary>Where the input came from.</summary>
    public InputSource Source { get; init; }

    /// <summary>Payload, fully opaque to the queue.</summary>
    public string Payload { get; init; } = string.Empty;

    /// <summary>
    /// Attached media (ids in the media store, in the order the user attached them). Empty for plain text.
    /// <b>Bytes never enter the log</b>: the log records ids only, and the media directory holds the bytes.
    /// </summary>
    public IReadOnlyList<string> MediaIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Reasoning effort chosen for this message (<c>reasoning_effort</c>). Empty = the default from settings.
    /// <para>
    /// Travels with the item rather than with global settings: effort is a <b>per-message</b> choice, and
    /// changing settings must not alter items already queued, whose choice was part of that message.
    /// </para>
    /// </summary>
    public string ReasoningEffort { get; init; } = string.Empty;

    /// <summary>Idempotency key (non-empty): the de-duplication basis, honoured across restarts.</summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>Current state.</summary>
    public InputItemStatus Status { get; init; }

    /// <summary>When the consumer took it.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When it reached a terminal state.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>How many times a consumer has taken it (incremented when re-queued after an interruption).</summary>
    public int Attempt { get; init; }

    /// <summary>Failure reason (set only for <see cref="InputItemStatus.Failed"/>).</summary>
    public string? Error { get; init; }
}
