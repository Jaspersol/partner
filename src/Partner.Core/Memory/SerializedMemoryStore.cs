namespace Partner.Core.Memory;

/// <summary>
/// Lets one <see cref="IMemoryStore"/> be used from several threads. <see cref="MemoryStore"/> holds a single
/// SQLite connection, which is not safe for concurrent use.
/// <para>
/// A decorator rather than a lock inside the store: the store's own methods call each other (a write promotes,
/// a recall maintains), so a lock taken per public method would deadlock on itself. Outside callers go through
/// here one at a time and the inner store keeps calling itself directly.
/// </para>
/// </summary>
public sealed class SerializedMemoryStore(IMemoryStore inner) : IMemoryStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync(Func<Task> call, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await call().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<string> WriteAsync(MemoryRecord record, CancellationToken ct = default) =>
        RunAsync(() => inner.WriteAsync(record, ct), ct);

    public Task WriteBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default) =>
        RunAsync(() => inner.WriteBatchAsync(records, ct), ct);

    public Task<MemoryRecord?> GetAsync(string recordId, CancellationToken ct = default) =>
        RunAsync(() => inner.GetAsync(recordId, ct), ct);

    public Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryQuery? query = null, CancellationToken ct = default) =>
        RunAsync(() => inner.ListAsync(query, ct), ct);

    public Task<IReadOnlyList<MemoryHit>> RecallAsync(string query, int? limit = null, CancellationToken ct = default) =>
        RunAsync(() => inner.RecallAsync(query, limit, ct), ct);

    public Task<IReadOnlyList<MemoryHit>> RecallAsync(
        string query,
        int? limit,
        IReadOnlySet<long>? excludeSourceSeqs,
        CancellationToken ct = default) =>
        RunAsync(() => inner.RecallAsync(query, limit, excludeSourceSeqs, ct), ct);

    public Task<InjectionResult> InjectAsync(string query, CancellationToken ct = default) =>
        RunAsync(() => inner.InjectAsync(query, ct), ct);

    public Task<InjectionResult> InjectAsync(
        string query,
        IReadOnlySet<long>? excludeSourceSeqs,
        CancellationToken ct = default) =>
        RunAsync(() => inner.InjectAsync(query, excludeSourceSeqs, ct), ct);

    public Task<string> ReviseAsync(string recordId, string newBody, Actor actor, CancellationToken ct = default) =>
        RunAsync(() => inner.ReviseAsync(recordId, newBody, actor, ct), ct);

    public Task SoftDeleteAsync(string recordId, Actor actor, CancellationToken ct = default) =>
        RunAsync(() => inner.SoftDeleteAsync(recordId, actor, ct), ct);

    public Task<int> PromoteEligibleAsync(CancellationToken ct = default) =>
        RunAsync(() => inner.PromoteEligibleAsync(ct), ct);

    public Task<int> DemoteIdleAsync(CancellationToken ct = default) =>
        RunAsync(() => inner.DemoteIdleAsync(ct), ct);

    public Task<int> TrimAsync(CancellationToken ct = default) =>
        RunAsync(() => inner.TrimAsync(ct), ct);

    public Task<int> CompactAsync(CancellationToken ct = default) =>
        RunAsync(() => inner.CompactAsync(ct), ct);

    public Task<string> BackupAsync(int keep = 7, CancellationToken ct = default) =>
        RunAsync(() => inner.BackupAsync(keep, ct), ct);

    public Task<string?> EnsureBackupAsync(TimeSpan maxAge, int keep = 7, CancellationToken ct = default) =>
        RunAsync(() => inner.EnsureBackupAsync(maxAge, keep, ct), ct);

    public Task<int> RebuildIndexAsync(CancellationToken ct = default) =>
        RunAsync(() => inner.RebuildIndexAsync(ct), ct);

    public Task<IReadOnlyList<ConsolidationEntry>> LogAsync(int limit = 100, CancellationToken ct = default) =>
        RunAsync(() => inner.LogAsync(limit, ct), ct);

    public Task<IReadOnlyList<MemoryRevision>> RevisionsAsync(string recordId, CancellationToken ct = default) =>
        RunAsync(() => inner.RevisionsAsync(recordId, ct), ct);

    public Task<MemoryRecord?> FindBySourceAsync(long sourceEventSeq, CancellationToken ct = default) =>
        RunAsync(() => inner.FindBySourceAsync(sourceEventSeq, ct), ct);

    public Task<IReadOnlyList<MemoryRecord>> FindBySourceRangeAsync(long fromSeq, long toSeq, CancellationToken ct = default) =>
        RunAsync(() => inner.FindBySourceRangeAsync(fromSeq, toSeq, ct), ct);

    public Task<bool> SetTierAsync(string recordId, MemoryTier tier, CancellationToken ct = default) =>
        RunAsync(() => inner.SetTierAsync(recordId, tier, ct), ct);

    public Task<bool> SetScopeAsync(string recordId, MemoryScope scope, CancellationToken ct = default) =>
        RunAsync(() => inner.SetScopeAsync(recordId, scope, ct), ct);

    public async ValueTask DisposeAsync()
    {
        // Waits for whatever is in flight: closing the connection under a running query is the failure this exists
        // to prevent.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
