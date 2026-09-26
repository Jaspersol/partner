namespace Partner.Core.Memory;

public sealed record MemoryStoreOptions
{
    /// <summary>How many hits a recall returns by default (design doc §2.5.6 ⑥: recallLimit).</summary>
    public int RecallLimit { get; init; } = 8;

    /// <summary>Token budget for injection (design doc §2.5.6 ⑥: injectionTokenLimit).</summary>
    public int InjectionTokenLimit { get; init; } = 5000;

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
/// The memory layer (design doc §2.5 / §2.5.6). Three tiers plus working memory, explainable retrieval,
/// promotion before trimming, and a four-step privacy delete.
/// </summary>
public interface IMemoryStore : IAsyncDisposable
{
    /// <summary>Stores one record. Runs the promotion scan before committing (design doc §2.5.6 ③).</summary>
    Task<string> WriteAsync(MemoryRecord record, CancellationToken ct = default);

    /// <summary>Bulk store in one transaction (used by importers and benchmarks).</summary>
    Task WriteBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default);

    Task<MemoryRecord?> GetAsync(string recordId, CancellationToken ct = default);

    Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryQuery? query = null, CancellationToken ct = default);

    /// <summary>Retrieval pipeline: scope-filtered candidates → explainable re-rank → top <c>limit</c>.</summary>
    Task<IReadOnlyList<MemoryHit>> RecallAsync(string query, int? limit = null, CancellationToken ct = default);

    /// <summary>Recall bounded by the injection token budget.</summary>
    Task<InjectionResult> InjectAsync(string query, CancellationToken ct = default);

    /// <summary>Appends a revision and applies the new body (design doc §2.5.5).</summary>
    Task<string> ReviseAsync(string recordId, string newBody, Actor actor, CancellationToken ct = default);

    /// <summary>
    /// Four-step privacy delete: soft-invalidate → drop embeddings → purge the body → mark for compaction.
    /// Missing any step is a leak (design doc §2.5 第 4 条).
    /// </summary>
    Task SoftDeleteAsync(string recordId, Actor actor, CancellationToken ct = default);

    /// <summary>Consolidates working records older than the promotion age. Idempotent.</summary>
    Task<int> PromoteEligibleAsync(CancellationToken ct = default);

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
}
