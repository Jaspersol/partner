namespace Partner.Core.Memory;

/// <summary>Who made a change (design doc §2.5.5): the distinction must stay visible.</summary>
public enum Actor
{
    Agent,
    Human,
}

/// <summary>
/// One memory record (design doc §2.2). Records are append-only in spirit: corrections go through
/// <c>ReviseAsync</c> (which appends a revision) and removals through <c>SoftDeleteAsync</c> (soft
/// invalidation + body purge), never a silent overwrite.
/// </summary>
public sealed record MemoryRecord
{
    public required string RecordId { get; init; }

    public required MemoryTier Tier { get; init; }

    public required MemoryScope Scope { get; init; }

    public required string Body { get; init; }

    /// <summary>Session event this memory was derived from, when applicable.</summary>
    public long? SourceEventSeq { get; init; }

    /// <summary>
    /// When this record was last handed to a turn. Null means never recalled, which the demotion rule treats
    /// as "unused" — a record nobody ever found is not one to keep trusting.
    /// </summary>
    public DateTimeOffset? LastRecalledAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Defaults to <see cref="CreatedAt"/>.</summary>
    public DateTimeOffset? ValidFrom { get; init; }

    public DateTimeOffset? ValidTo { get; init; }

    public double Confidence { get; init; } = 1.0;

    public double Importance { get; init; } = 0.5;

    public double Veracity { get; init; } = 1.0;

    /// <summary>Working-tier time to live. Ignored for other tiers.</summary>
    public TimeSpan? Ttl { get; init; }

    public DateTimeOffset? ConsolidatedAt { get; init; }

    public string? SupersededBy { get; init; }

    public Actor? EditedBy { get; init; }
}

/// <summary>Audit entry for an edit (design doc §2.2 / §2.5.5).</summary>
public sealed record MemoryRevision(
    string RecordId,
    DateTimeOffset At,
    string Before,
    string After,
    Actor Actor);

/// <summary>Audit entry for promotion / trim / delete / write (design doc §2.5.6 ③).</summary>
public sealed record ConsolidationEntry(
    long Id,
    string RecordId,
    DateTimeOffset At,
    string Action,
    string? Detail);

/// <summary>Result of a budget-bounded injection (design doc §2.5.6 ④ step 5).</summary>
public sealed record InjectionResult(
    IReadOnlyList<MemoryHit> Hits,
    int Tokens,
    bool Truncated);

/// <summary>Optional filter for listing.</summary>
public sealed record MemoryQuery(
    MemoryTier? Tier = null,
    MemoryScope? Scope = null,
    bool IncludeInvalidated = false,
    int Limit = 1000);
