namespace Partner.Core.Memory;

public sealed record MemoryStoreOptions
{
    /// <summary>How many hits a recall returns by default.</summary>
    public int RecallLimit { get; init; } = 8;

    /// <summary>Token budget for injection.</summary>
    public int InjectionTokenLimit { get; init; } = 5000;

    /// <summary>
    /// Hits scoring below this are not returned. The score has a floor of roughly 0.2 even for a record that only
    /// shares one word (importance, recency, tier all contribute), so a threshold is what keeps a barely related
    /// record from taking an injection slot just because the store had nothing better. 0 keeps everything.
    /// </summary>
    public double MinScore { get; init; }

    /// <summary>FTS coarse-filter size before re-ranking.</summary>
    public int CandidateLimit { get; init; } = 200;

    /// <summary>An unconsolidated working record older than this must be consolidated before trimming.</summary>
    public TimeSpan PromotionAge { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan DefaultWorkingTtl { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Offline default: no embeddings, FTS-only recall.</summary>
    public IEmbeddingProvider Embeddings { get; init; } = new NullEmbeddingProvider();

    public IMemoryScorer Scorer { get; init; } = new MemoryScorer();

    public ITokenCounter Tokens { get; init; } = new ApproximateTokenCounter();
}

/// <summary>
/// The memory layer. Three tiers plus working memory, explainable retrieval, promotion before trimming, and a
/// four-step privacy delete.
/// </summary>
public interface IMemoryStore : IAsyncDisposable
{
    /// <summary>Stores one record. Runs the promotion scan before committing.</summary>
    Task<string> WriteAsync(MemoryRecord record, CancellationToken ct = default);

    /// <summary>Bulk store in one transaction (used by importers and benchmarks).</summary>
    Task WriteBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default);

    Task<MemoryRecord?> GetAsync(string recordId, CancellationToken ct = default);

    Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryQuery? query = null, CancellationToken ct = default);

    /// <summary>Retrieval pipeline: scope-filtered candidates → explainable re-rank → top <c>limit</c>.</summary>
    Task<IReadOnlyList<MemoryHit>> RecallAsync(string query, int? limit = null, CancellationToken ct = default);

    /// <summary>
    /// Recall that leaves out records distilled from <paramref name="excludeSourceSeqs"/>.
    /// <para>
    /// For a caller that already has those events in front of it, so the model does not read the same statement
    /// twice — once as history and once as a remembered fact.
    /// </para>
    /// <para>
    /// Filtering happens before the top-<c>limit</c> cut, so the answer still holds <c>limit</c> usable hits.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<MemoryHit>> RecallAsync(
        string query,
        int? limit,
        IReadOnlySet<long>? excludeSourceSeqs,
        CancellationToken ct = default);

    /// <summary>Recall bounded by the injection token budget.</summary>
    Task<InjectionResult> InjectAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Recall bounded by the injection token budget, leaving out records distilled from
    /// <paramref name="excludeSourceSeqs"/> (see the overload of <see cref="RecallAsync"/> for why).
    /// </summary>
    Task<InjectionResult> InjectAsync(
        string query,
        IReadOnlySet<long>? excludeSourceSeqs,
        CancellationToken ct = default);

    /// <summary>Appends a revision and applies the new body.</summary>
    Task<string> ReviseAsync(string recordId, string newBody, Actor actor, CancellationToken ct = default);

    /// <summary>
    /// Four-step privacy delete: soft-invalidate → drop embeddings → purge the body → mark for compaction.
    /// Missing any step is a leak.
    /// </summary>
    Task SoftDeleteAsync(string recordId, Actor actor, CancellationToken ct = default);

    /// <summary>Consolidates working records older than the promotion age. Idempotent.</summary>
    Task<int> PromoteEligibleAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops each idle record one tier — the maintenance pass, so a store nobody reads or writes still ages its
    /// records instead of holding a stale fact at full trust forever. Idempotent.
    /// </summary>
    Task<int> DemoteIdleAsync(CancellationToken ct = default);

    /// <summary>Trims expired working records — <b>only</b> those already consolidated.</summary>
    Task<int> TrimAsync(CancellationToken ct = default);

    /// <summary>Physically removes soft-deleted rows (the last step of a privacy delete).</summary>
    Task<int> CompactAsync(CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the full-text index from the content table. The index is a projection, so it must be
    /// reconstructible — and rebuilding must not change what a query returns.
    /// </summary>
    Task<int> RebuildIndexAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ConsolidationEntry>> LogAsync(int limit = 100, CancellationToken ct = default);

    Task<IReadOnlyList<MemoryRevision>> RevisionsAsync(string recordId, CancellationToken ct = default);

    /// <summary>
    /// The record distilled from a given session event, or null when there is none.
    /// <para>
    /// The schema carries no unique constraint on <c>source_event_seq</c>: a memory may be written with no source
    /// at all, so making writes at-least-once is the caller's job, and this is the question that lets it. The
    /// column is indexed because the cascade that deletes a session's memories reads it in ranges.
    /// </para>
    /// </summary>
    Task<MemoryRecord?> FindBySourceAsync(long sourceEventSeq, CancellationToken ct = default);

    /// <summary>
    /// Every live record distilled from the events in <paramref name="fromSeq"/>..<paramref name="toSeq"/>, in one
    /// query.
    /// <para>
    /// The range form exists for deletion: what has to be found is everything a deleted span of the session log
    /// left behind, and per-event queries would make removal cost grow with conversation length.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<MemoryRecord>> FindBySourceRangeAsync(long fromSeq, long toSeq, CancellationToken ct = default);

    /// <summary>
    /// Moves a record to another tier, on a person's judgement. False when the id is unknown.
    /// <para>
    /// The tier decides how long the demotion rule protects a record. "A fact I rely on" versus "something I
    /// once guessed" is not something a score can tell, so it is the person's call, and it is persisted.
    /// </para>
    /// </summary>
    Task<bool> SetTierAsync(string recordId, MemoryTier tier, CancellationToken ct = default);

    /// <summary>
    /// Pins a record against forgetting, or releases it back to the ordinary rules. False when the id is unknown.
    /// <para>
    /// Independent of the tier: retention is about time, tier is about trust. It protects against demotion and
    /// trimming, not against being corrected.
    /// </para>
    /// </summary>
    Task<bool> SetScopeAsync(string recordId, MemoryScope scope, CancellationToken ct = default);
}
