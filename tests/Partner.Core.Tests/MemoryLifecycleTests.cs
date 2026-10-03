using Partner.Core.Memory;

namespace Partner.Core.Tests.Memory;

/// <summary>
/// The two rules that keep a growing store honest: the newest statement about a thing retires the older one,
/// and a record nobody has found in three months stops being trusted.
/// </summary>
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
    public void Two_statements_about_the_same_thing_conflict()
    {
        Assert.True(MemoryLifecycle.Conflicts(
            "用户用 pnpm 作为包管理器",
            "用户用 npm 作为包管理器"));
    }

    [Fact]
    public void Two_statements_about_different_things_do_not()
    {
        // Merging these would delete one for no reason, and a false merge is unrecoverable without a person
        // noticing. A false split is two rows saying similar things, which the scorer then ranks between.
        Assert.False(MemoryLifecycle.Conflicts(
            "用户用 pnpm 作为包管理器",
            "部署脚本在 scripts 目录里"));
    }

    [Fact]
    public void A_shared_placeholder_word_is_not_a_shared_topic()
    {
        // "用户" is in nearly every record this system writes. Rating a shared stopword as overlap is what made
        // two unrelated memories look like the same one.
        Assert.False(MemoryLifecycle.Conflicts("用户喜欢深色主题", "用户部署在阿里云"));
    }

    [Fact]
    public async Task A_newer_statement_retires_the_older_one()
    {
        var store = Open();
        await Write(store, "m1", MemoryTier.Semantic, "用户用 pnpm 作为包管理器", DateTimeOffset.UtcNow);

        await Write(store, "m2", MemoryTier.Semantic, "用户改用 npm 作为包管理器了", DateTimeOffset.UtcNow);

        var old = await store.GetAsync("m1");
        Assert.NotNull(old!.ValidTo);
        Assert.Equal("m2", old.SupersededBy);

        var current = await store.GetAsync("m2");
        Assert.Null(current!.ValidTo);
    }

    [Fact]
    public async Task A_retired_record_is_still_there_to_read_and_edit()
    {
        // "Fully visible" is the whole point: the interesting question is usually "what replaced this", and
        // that answer lives on the row that was superseded.
        var store = Open();
        await Write(store, "m1", MemoryTier.Semantic, "用户用 pnpm 作为包管理器", DateTimeOffset.UtcNow);
        await Write(store, "m2", MemoryTier.Semantic, "用户改用 npm 作为包管理器了", DateTimeOffset.UtcNow);

        var all = await store.ListAsync(new MemoryQuery(IncludeInvalidated: true, Limit: 100));

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void An_identical_record_is_not_a_conflict()
    {
        // Retiring it would lose a fact and add nothing: the newcomer says the same thing. And it is a real
        // case — the same turn can be distilled twice, and two turns can land on the same statement.
        Assert.False(MemoryLifecycle.Conflicts(
            "用户用 pnpm 作为包管理器",
            "用户用 pnpm 作为包管理器"));
    }

    [Fact]
    public void Punctuation_does_not_make_two_records_differ()
    {
        Assert.False(MemoryLifecycle.Conflicts(
            "用户用 pnpm 作为包管理器。",
            "用户用 pnpm 作为包管理器"));
    }

    [Fact]
    public async Task A_recall_returns_the_newer_statement_and_not_the_retired_one()
    {
        var store = Open();
        await Write(store, "m1", MemoryTier.Semantic, "用户用 pnpm 作为包管理器", DateTimeOffset.UtcNow);
        await Write(store, "m2", MemoryTier.Semantic, "用户改用 npm 作为包管理器了", DateTimeOffset.UtcNow);

        var hits = await store.RecallAsync("包管理器");

        Assert.Contains(hits, h => h.RecordId == "m2");
        Assert.DoesNotContain(hits, h => h.RecordId == "m1");
    }

    [Fact]
    public async Task A_different_tier_is_not_a_conflict()
    {
        // Demotion moves things between tiers, so comparing across them would retire a record for the wrong
        // reason — and a working note is not a contradiction of a semantic fact.
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

        // Any recall maintains the store, which is where demotion runs.
        await store.RecallAsync("新事");

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
        // "This is a fact I rely on" and "this is something I once guessed" are judgements no score can make.
        var store = Open();
        await Write(store, "m1", MemoryTier.Working, "也许该升上去", DateTimeOffset.UtcNow);

        Assert.True(await store.SetTierAsync("m1", MemoryTier.Semantic));
        Assert.Equal(MemoryTier.Semantic, (await store.GetAsync("m1"))!.Tier);

        Assert.False(await store.SetTierAsync("no-such-record", MemoryTier.Semantic));
    }

    [Fact]
    public async Task A_store_created_before_the_column_still_opens()
    {
        // CREATE TABLE IF NOT EXISTS leaves an existing table alone, so a new column has to be added separately.
        // A workspace that opens fine and then misbehaves on an absent column is worse than one that fails.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "memory.db");

        await using (var first = new MemoryStore(path))
        {
            await Write(first, "m1", MemoryTier.Semantic, "迁移前写的一条", DateTimeOffset.UtcNow);
        }

        // Drop the column the way an older build would not have it.
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
