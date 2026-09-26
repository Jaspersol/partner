using Partner.Core.Memory;
using Partner.Core.Tests.Sessions;

namespace Partner.Core.Tests.Memory;

/// <summary>Acceptance tests for the memory store (design doc §2.5.6).</summary>
public sealed class MemoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));

    private string DbPath => Path.Combine(_dir, "memory", "memory.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private MemoryStore Open(MemoryStoreOptions? options = null) => new(DbPath, options, _clock);

    private static MemoryRecord Rec(
        string id,
        string body,
        MemoryTier tier = MemoryTier.Semantic,
        MemoryScope scope = MemoryScope.Recall,
        DateTimeOffset? createdAt = null,
        double importance = 0.5) => new()
    {
        RecordId = id,
        Tier = tier,
        Scope = scope,
        Body = body,
        CreatedAt = createdAt ?? default,
        Importance = importance,
    };

    [Fact]
    public async Task Write_and_get_round_trip_preserves_fields()
    {
        await using var store = Open();

        await store.WriteAsync(new MemoryRecord
        {
            RecordId = "m1",
            Tier = MemoryTier.Semantic,
            Scope = MemoryScope.Global,
            Body = "the user prefers dark mode",
            SourceEventSeq = 42,
            Importance = 0.8,
            Confidence = 0.9,
            Veracity = 0.7,
        });

        var loaded = await store.GetAsync("m1");
        Assert.NotNull(loaded);
        Assert.Equal("the user prefers dark mode", loaded!.Body);
        Assert.Equal(MemoryTier.Semantic, loaded.Tier);
        Assert.Equal(MemoryScope.Global, loaded.Scope);
        Assert.Equal(42, loaded.SourceEventSeq);
        Assert.Equal(0.8, loaded.Importance);
        Assert.Equal(0.9, loaded.Confidence);
        Assert.Equal(0.7, loaded.Veracity);
        Assert.Null(loaded.ValidTo);
    }

    [Fact]
    public async Task Recall_finds_by_keyword_and_skips_unrelated_records()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("a", "the deploy pipeline uses blue green releases"));
        await store.WriteAsync(Rec("b", "the cat sleeps on the keyboard"));

        var hits = await store.RecallAsync("deploy pipeline");

        Assert.Contains(hits, h => h.RecordId == "a");
        Assert.DoesNotContain(hits, h => h.RecordId == "b");
    }

    [Fact]
    public async Task Recall_is_deterministic_and_explainable()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("a", "alpha beta gamma", importance: 0.9));
        await store.WriteAsync(Rec("b", "alpha beta", importance: 0.1));

        var first = await store.RecallAsync("alpha beta");
        var second = await store.RecallAsync("alpha beta");

        Assert.Equal(first.Select(h => h.RecordId), second.Select(h => h.RecordId));
        Assert.All(first, h => Assert.NotEmpty(h.Reasons));
        Assert.Contains(first[0].Reasons, r => r.StartsWith("lexical", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recall_ignores_queries_without_usable_tokens()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("a", "alpha beta"));

        Assert.Empty(await store.RecallAsync(""));
        Assert.Empty(await store.RecallAsync("   "));
        Assert.Empty(await store.RecallAsync("a"));
    }

    [Fact]
    public async Task Recall_survives_fts_metacharacters_in_the_query()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("a", "alpha beta"));

        // Would be an FTS5 syntax error if passed through raw.
        var hits = await store.RecallAsync("\"alpha\" AND (beta OR *)");

        Assert.Contains(hits, h => h.RecordId == "a");
    }

    [Fact]
    public async Task Recall_respects_the_limit_and_orders_by_score()
    {
        await using var store = Open();
        for (var i = 0; i < 5; i++)
        {
            await store.WriteAsync(Rec($"m{i}", "alpha beta gamma", importance: 0.1 * i));
        }

        var hits = await store.RecallAsync("alpha beta", limit: 2);

        Assert.Equal(2, hits.Count);
        Assert.True(hits[0].Score.Total >= hits[1].Score.Total);
    }

    [Fact]
    public async Task Injection_stops_at_the_token_budget()
    {
        await using var store = Open(new MemoryStoreOptions { InjectionTokenLimit = 12, RecallLimit = 10 });
        await store.WriteAsync(Rec("a", "alpha beta gamma delta epsilon zeta eta theta"));
        await store.WriteAsync(Rec("b", "alpha beta gamma delta epsilon zeta eta theta"));

        var injection = await store.InjectAsync("alpha");

        Assert.True(injection.Tokens <= 12, $"tokens={injection.Tokens}");
        Assert.True(injection.Truncated);
        Assert.Single(injection.Hits);
    }

    [Fact]
    public async Task Promotion_happens_only_after_the_promotion_age()
    {
        await using var store = Open();
        var t0 = _clock.GetUtcNow();

        await store.WriteAsync(Rec("old", "alpha", MemoryTier.Working, createdAt: t0));

        // Age 23h59m: still inside the window.
        _clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        Assert.Equal(0, await store.PromoteEligibleAsync());

        // A younger record written now must stay working when the older one ages out.
        await store.WriteAsync(Rec("fresh", "alpha", MemoryTier.Working, createdAt: _clock.GetUtcNow()));

        _clock.Advance(TimeSpan.FromMinutes(2)); // old is now 24h01m, fresh is 2m
        Assert.Equal(1, await store.PromoteEligibleAsync());

        var promoted = await store.GetAsync("old");
        Assert.Equal(MemoryTier.Working, promoted!.Tier); // the working row is marked, not re-tiered
        Assert.NotNull(promoted.ConsolidatedAt);

        // The durable copy carries the content forward.
        var copy = await store.GetAsync(MemoryStore.EpisodicCopyId("old"));
        Assert.NotNull(copy);
        Assert.Equal(MemoryTier.Episodic, copy!.Tier);
        Assert.Equal("alpha", copy.Body);
        Assert.NotNull(copy.ConsolidatedAt);

        var untouched = await store.GetAsync("fresh");
        Assert.Equal(MemoryTier.Working, untouched!.Tier);
        Assert.Null(untouched.ConsolidatedAt);

        var log = await store.LogAsync();
        Assert.Contains(log, e => e.RecordId == "old" && e.Action == "promote");

        // Idempotent.
        Assert.Equal(0, await store.PromoteEligibleAsync());
    }

    [Fact]
    public async Task Write_runs_the_promotion_scan_before_committing()
    {
        await using var store = Open();
        var created = _clock.GetUtcNow() - TimeSpan.FromHours(30);
        await store.WriteAsync(Rec("stale", "alpha", MemoryTier.Working, createdAt: created));

        // The very next write must consolidate the stale working record first.
        await store.WriteAsync(Rec("newer", "beta"));

        var stale = await store.GetAsync("stale");
        Assert.NotNull(stale!.ConsolidatedAt);
        Assert.NotNull(await store.GetAsync(MemoryStore.EpisodicCopyId("stale")));
    }

    [Fact]
    public async Task Trim_removes_only_consolidated_expired_working_records()
    {
        await using var store = Open();
        var created = _clock.GetUtcNow() - TimeSpan.FromDays(30);

        await store.WriteAsync(Rec("consolidated", "alpha", MemoryTier.Working, createdAt: created));
        Assert.Equal(1, await store.PromoteEligibleAsync());

        await store.WriteAsync(new MemoryRecord
        {
            RecordId = "unconsolidated",
            Tier = MemoryTier.Working,
            Scope = MemoryScope.Recall,
            Body = "beta",
            CreatedAt = created,
            // A TTL that already expired, but no consolidation: it must survive the trim.
            Ttl = TimeSpan.FromDays(1),
        });

        var trimmed = await store.TrimAsync();

        Assert.Equal(1, trimmed);
        Assert.Null(await store.GetAsync("consolidated"));            // working row removed
        Assert.NotNull(await store.GetAsync(MemoryStore.EpisodicCopyId("consolidated"))); // content preserved
        Assert.NotNull(await store.GetAsync("unconsolidated"));       // never trimmed without consolidation
    }

    [Fact]
    public async Task Revise_appends_a_revision_and_keeps_the_old_body_visible()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("m1", "original body"));

        await store.ReviseAsync("m1", "corrected body", Actor.Human);

        var loaded = await store.GetAsync("m1");
        Assert.Equal("corrected body", loaded!.Body);
        Assert.Equal(Actor.Human, loaded.EditedBy);

        var revisions = await store.RevisionsAsync("m1");
        var revision = Assert.Single(revisions);
        Assert.Equal("original body", revision.Before);
        Assert.Equal("corrected body", revision.After);
        Assert.Equal(Actor.Human, revision.Actor);

        // The FTS index follows the revision.
        Assert.Contains(await store.RecallAsync("corrected"), h => h.RecordId == "m1");
        Assert.DoesNotContain(await store.RecallAsync("original"), h => h.RecordId == "m1");
    }

    [Fact]
    public async Task Soft_delete_performs_all_four_steps_then_compaction_removes_the_row()
    {
        await using var store = Open(new MemoryStoreOptions { Embeddings = new HashingEmbeddingProvider() });
        await store.WriteAsync(Rec("secret", "the api key is hunter2"));

        await store.SoftDeleteAsync("secret", Actor.Human);

        // 1) soft-invalidated  3) body purged
        var purged = await store.GetAsync("secret");
        Assert.NotNull(purged);
        Assert.NotNull(purged!.ValidTo);
        Assert.Equal(string.Empty, purged.Body);

        // 2) not searchable any more
        Assert.Empty(await store.RecallAsync("hunter2"));

        // 4) compaction physically removes it
        Assert.Equal(1, await store.CompactAsync());
        Assert.Null(await store.GetAsync("secret"));
        Assert.Equal(0, await store.CompactAsync());
    }

    [Fact]
    public async Task Delete_removes_the_vector_entry()
    {
        await using var store = Open(new MemoryStoreOptions { Embeddings = new HashingEmbeddingProvider() });
        await store.WriteAsync(Rec("m1", "alpha beta gamma"));

        await store.SoftDeleteAsync("m1", Actor.Human);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memory_embeddings WHERE record_id = 'm1'";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Offline_embedding_failure_degrades_to_fts_without_losing_records()
    {
        var failing = new FailingEmbeddingProvider();
        await using var store = Open(new MemoryStoreOptions { Embeddings = failing });
        await store.WriteAsync(Rec("m1", "alpha beta gamma"));

        // Writes still land, recall still works, nothing is lost.
        var hits = await store.RecallAsync("alpha");
        Assert.Contains(hits, h => h.RecordId == "m1");
        Assert.NotNull(await store.GetAsync("m1"));
        Assert.True(failing.Attempts > 0, "the provider should have been consulted");
    }

    [Fact]
    public async Task Embeddings_improve_recall_for_shared_substrings()
    {
        await using var store = Open(new MemoryStoreOptions { Embeddings = new HashingEmbeddingProvider() });
        await store.WriteAsync(Rec("a", "the deployment pipeline runs nightly"));
        await store.WriteAsync(Rec("b", "the deployment pipeline runs nightly"));

        var hits = await store.RecallAsync("deployment pipeline");

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.True(h.Score.Vector > 0, "vector component should be populated"));
    }

    [Fact]
    public async Task Batch_write_stores_every_record()
    {
        await using var store = Open();
        var records = Enumerable.Range(0, 500)
            .Select(i => Rec($"b{i}", $"alpha beta record number {i}"))
            .ToList();

        await store.WriteBatchAsync(records);

        var listed = await store.ListAsync(new MemoryQuery(Limit: 1000));
        Assert.Equal(500, listed.Count);
        Assert.Contains(await store.RecallAsync("alpha beta", limit: 500), h => h.RecordId == "b42");
    }

    [Fact]
    public async Task List_filters_by_tier_and_scope()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("a", "alpha", MemoryTier.Semantic, MemoryScope.Global));
        await store.WriteAsync(Rec("b", "beta", MemoryTier.Episodic, MemoryScope.Recall));

        var semantic = await store.ListAsync(new MemoryQuery(Tier: MemoryTier.Semantic));
        Assert.Equal(new[] { "a" }, semantic.Select(r => r.RecordId));

        var global = await store.ListAsync(new MemoryQuery(Scope: MemoryScope.Global));
        Assert.Equal(new[] { "a" }, global.Select(r => r.RecordId));
    }

    [Fact]
    public async Task Records_survive_a_reopen()
    {
        await using (var store = Open())
        {
            await store.WriteAsync(Rec("m1", "alpha beta gamma"));
        }

        await using var reopened = Open();
        Assert.NotNull(await reopened.GetAsync("m1"));
        Assert.Contains(await reopened.RecallAsync("alpha"), h => h.RecordId == "m1");
    }

    [Fact]
    public async Task Duplicate_record_id_is_rejected()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("m1", "alpha"));

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.WriteAsync(Rec("m1", "beta")));
    }

    private sealed class FailingEmbeddingProvider : IEmbeddingProvider
    {
        public int Attempts { get; private set; }

        public string ModelId => "failing";

        public int Dimensions => 384;

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
        {
            Attempts++;
            throw new InvalidOperationException("embedding service offline");
        }
    }
}
