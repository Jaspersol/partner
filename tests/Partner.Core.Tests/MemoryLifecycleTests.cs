using Partner.Core.Memory;

namespace Partner.Core.Tests.Memory;

/// <summary>Retention and independent-source lifecycle behavior.</summary>
public sealed class MemoryLifecycleTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "partner-memory-lifecycle", Guid.NewGuid().ToString("N"));

    private MemoryStore? _store;

    public void Dispose()
    {
        _store?.DisposeAsync().AsTask().GetAwaiter().GetResult();

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private MemoryStore Open(DateTimeOffset? now = null)
    {
        Directory.CreateDirectory(_dir);
        _store = new MemoryStore(
            Path.Combine(_dir, "memory.db"),
            clock: new FixedClock(now ?? DateTimeOffset.UtcNow));
        return _store;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Task Write(MemoryStore store, string id, MemoryTier tier, string body, DateTimeOffset at) =>
        store.WriteAsync(new MemoryRecord
        {
            RecordId = id,
            Tier = tier,
            Scope = MemoryScope.Recall,
            Body = body,
            CreatedAt = at,
        });


    [Fact]
    public async Task A_newer_similar_statement_does_not_retire_the_older_one()
    {
        var store = Open();
        await Write(store, "m1", MemoryTier.Semantic, "用户用 pnpm 作为包管理器", DateTimeOffset.UtcNow);

        await Write(store, "m2", MemoryTier.Semantic, "用户改用 npm 作为包管理器了", DateTimeOffset.UtcNow);

        var old = await store.GetAsync("m1");
        Assert.Null(old!.ValidTo);
        Assert.Null(old.SupersededBy);

        var current = await store.GetAsync("m2");
        Assert.Null(current!.ValidTo);
    }

    [Fact]
    public async Task Similar_records_remain_in_the_management_list()
    {
        var store = Open();
        await Write(store, "m1", MemoryTier.Semantic, "用户用 pnpm 作为包管理器", DateTimeOffset.UtcNow);
        await Write(store, "m2", MemoryTier.Semantic, "用户改用 npm 作为包管理器了", DateTimeOffset.UtcNow);

        var all = await store.ListAsync(new MemoryQuery(IncludeInvalidated: true, Limit: 100));

        Assert.Equal(2, all.Count);
    }


    [Fact]
    public async Task Recall_returns_both_independent_statements()
    {
        var store = Open();
        await Write(store, "m1", MemoryTier.Semantic, "用户用 pnpm 作为包管理器", DateTimeOffset.UtcNow);
        await Write(store, "m2", MemoryTier.Semantic, "用户改用 npm 作为包管理器了", DateTimeOffset.UtcNow);

        var hits = await store.RecallAsync("包管理器");

        Assert.Contains(hits, h => h.RecordId == "m2");
        Assert.Contains(hits, h => h.RecordId == "m1");
    }

    [Fact]
    public async Task A_different_tier_is_not_a_conflict()
    {
        var store = Open();
        await Write(store, "m1", MemoryTier.Semantic, "用户用 pnpm 作为包管理器", DateTimeOffset.UtcNow);
        await Write(store, "m2", MemoryTier.Working, "用户可能在用 pnpm", DateTimeOffset.UtcNow);

        Assert.Null((await store.GetAsync("m1"))!.ValidTo);
    }

    [Fact]
    public async Task A_record_nobody_found_for_three_months_loses_a_tier()
    {
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var store = Open(now);

        await Write(store, "old", MemoryTier.Semantic, "很久以前说过的一件旧事", now.AddDays(-100));
        await Write(store, "fresh", MemoryTier.Semantic, "最近说过的一件新事", now.AddDays(-1));

        await store.RecallAsync("新事");
        await store.DemoteIdleAsync(); // demotion is the maintenance pass's job, not the recall path's

        Assert.Equal(MemoryTier.Episodic, (await store.GetAsync("old"))!.Tier);
        Assert.Equal(MemoryTier.Semantic, (await store.GetAsync("fresh"))!.Tier);
    }

    [Fact]
    public async Task A_record_used_recently_keeps_its_tier()
    {
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var store = Open(now);

        await Write(store, "used", MemoryTier.Semantic, "一件常被想起的事", now.AddDays(-200));
        await store.RecallAsync("常被想起");

        Assert.Equal(MemoryTier.Semantic, (await store.GetAsync("used"))!.Tier);
    }

    [Fact]
    public async Task A_tier_can_be_set_by_hand()
    {
        var store = Open();
        await Write(store, "m1", MemoryTier.Working, "也许该升上去", DateTimeOffset.UtcNow);

        Assert.True(await store.SetTierAsync("m1", MemoryTier.Semantic));
        Assert.Equal(MemoryTier.Semantic, (await store.GetAsync("m1"))!.Tier);

        Assert.False(await store.SetTierAsync("no-such-record", MemoryTier.Semantic));
    }

    [Fact]
    public async Task A_store_created_before_the_column_still_opens()
    {
        // CREATE TABLE IF NOT EXISTS leaves an existing table alone, so the column has to be added separately.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "memory.db");

        await using (var first = new MemoryStore(path))
        {
            await Write(first, "m1", MemoryTier.Semantic, "迁移前写的一条", DateTimeOffset.UtcNow);
        }

        using (var bare = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            bare.Open();
            using var drop = bare.CreateCommand();
            drop.CommandText = "ALTER TABLE memories DROP COLUMN last_recalled_at";
            drop.ExecuteNonQuery();
        }

        await using (var reopened = new MemoryStore(path))
        {
            var record = await reopened.GetAsync("m1");
            Assert.NotNull(record);
            Assert.Null(record!.LastRecalledAt);
        }
    }
}
