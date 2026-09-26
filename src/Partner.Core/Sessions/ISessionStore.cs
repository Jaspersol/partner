namespace Partner.Core.Sessions;

/// <summary>
/// The single, project-wide session log (design doc §2.3).
/// <para>
/// Contract:
/// <list type="bullet">
///   <item><b>Single writer.</b> <see cref="AppendAsync"/> is the only mutator and assigns the seq.</item>
///   <item><b>Append-only.</b> Nothing is rewritten; corrections are <c>Edit</c> events.</item>
///   <item><b>Durability barrier.</b> When the store is configured to flush on append, a returned seq
///   means the record is on disk. Otherwise <see cref="FlushAsync"/> is the barrier and must throw on
///   failure — callers treat a thrown flush as "the turn is not durable".</item>
///   <item><b>Rebuildable projection.</b> <see cref="ProjectAsync"/> can always be recomputed from the log.</item>
/// </list>
/// </para>
/// </summary>
public interface ISessionStore : IAsyncDisposable
{
    /// <summary>Highest seq durably committed. 0 for an empty log.</summary>
    long LastSeq { get; }

    /// <summary>True after a failed durability barrier: the log is in an unknown state and the store refuses further writes.</summary>
    bool Faulted { get; }

    /// <summary>Appends one event and returns its seq. Throws <see cref="InvalidOperationException"/> when <see cref="Faulted"/>.</summary>
    Task<long> AppendAsync(SessionEvent sessionEvent, CancellationToken ct = default);

    /// <summary>Durability barrier for buffered appends. Throws on failure and faults the store.</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>All durably committed events, in seq order.</summary>
    IAsyncEnumerable<SessionEvent> ReadAllAsync(CancellationToken ct = default);

    /// <summary>Durably committed events with <c>Seq &gt;= fromSeq</c>, at most <paramref name="take"/>, in seq order.</summary>
    IAsyncEnumerable<SessionEvent> ReadAsync(long fromSeq, int take, CancellationToken ct = default);

    /// <summary>Rebuilds the view from the log.</summary>
    Task<SessionView> ProjectAsync(CancellationToken ct = default);
}
