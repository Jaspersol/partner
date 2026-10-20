namespace Partner.Core.Sessions;

/// <summary>
/// The single, project-wide session log.
/// <para>
/// Contract:
/// <list type="bullet">
///   <item><b>Single writer.</b> <see cref="AppendAsync"/> is the only mutator and assigns the seq.</item>
///   <item><b>Append-only by default.</b> Corrections are <c>Edit</c> events and removals are retractions;
///   nothing is rewritten except by an explicit <see cref="PurgeAsync"/>.</item>
///   <item><b>Durability barrier.</b> When the store is configured to flush on append, a returned seq
///   means the record is on disk. Otherwise <see cref="FlushAsync"/> is the barrier and must throw on
///   failure — callers treat a thrown flush as "the turn is not durable".</item>
///   <item><b>Rebuildable projection.</b> <see cref="ProjectAsync"/> can always be recomputed from the log.</item>
/// </list>
/// </para>
/// </summary>
/// <summary>What a physical removal took out of the log.</summary>
/// <param name="RemovedEvents">Events whose content was dropped.</param>
/// <param name="RemovedRevisions">Revisions that held a copy of that content.</param>
/// <param name="TombstoneSeq">Seq of the content-free retraction left for the range.</param>
public readonly record struct PurgeResult(int RemovedEvents, int RemovedRevisions, long TombstoneSeq);

public interface ISessionStore : IAsyncDisposable
{
    /// <summary>Highest seq durably committed. 0 for an empty log.</summary>
    long LastSeq { get; }

    /// <summary>True after a failed durability barrier: the log is in an unknown state and the store refuses further writes.</summary>
    bool Faulted { get; }

    /// <summary>Appends one event and returns its seq. Throws <see cref="InvalidOperationException"/> when <see cref="Faulted"/>.</summary>
    Task<long> AppendAsync(SessionEvent sessionEvent, CancellationToken ct = default);

    /// <summary>
    /// Physically removes <paramref name="fromSeq"/>..<paramref name="toSeq"/> (inclusive) from the log,
    /// together with any revisions of those events. Used by an explicit user removal: a retraction hides
    /// content, this deletes it. A content-free retraction is left for the range so the seq space stays
    /// monotonic, and nothing else in the log is touched.
    /// </summary>
    Task<PurgeResult> PurgeAsync(long fromSeq, long toSeq, CancellationToken ct = default);

    /// <summary>Durability barrier for buffered appends. Throws on failure and faults the store.</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>All durably committed events, in seq order.</summary>
    IAsyncEnumerable<SessionEvent> ReadAllAsync(CancellationToken ct = default);

    /// <summary>Durably committed events with <c>Seq &gt;= fromSeq</c>, at most <paramref name="take"/>, in seq order.</summary>
    IAsyncEnumerable<SessionEvent> ReadAsync(long fromSeq, int take, CancellationToken ct = default);

    /// <summary>Rebuilds the view from the log.</summary>
    Task<SessionView> ProjectAsync(CancellationToken ct = default);
}
