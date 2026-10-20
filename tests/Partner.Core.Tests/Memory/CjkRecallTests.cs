using Partner.Core.Memory;
using Partner.Core.Tests.Sessions;

namespace Partner.Core.Tests.Memory;

/// <summary>
/// CJK recall through the bigram index.
/// <para>
/// The FTS tokenizer cannot serve this path: unicode61 has no word segmentation, so a question worded
/// differently from the record shares no token with it. Nothing errors when the path breaks — recall is simply
/// empty for the language this project is written in.
/// </para>
/// </summary>
public sealed class CjkRecallTests : IDisposable
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

    private MemoryStore Open() => new(DbPath, null, _clock);

    private static MemoryRecord Rec(string id, string body) => new()
    {
        RecordId = id,
        Tier = MemoryTier.Semantic,
        Scope = MemoryScope.Recall,
        Body = body,
    };

    [Fact]
    public async Task A_differently_worded_question_finds_the_record()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("m1", "用户不喜欢被反复确认"));

        // Shares no word with the record — the two sentences only intersect at the bigrams 喜欢 and 欢被.
        var hits = await store.RecallAsync("我不太喜欢被打断");

        Assert.Contains(hits, h => h.RecordId == "m1");
    }

    [Fact]
    public async Task A_question_with_no_bigram_in_common_finds_nothing()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("m1", "用户不喜欢被反复确认"));

        // The control: overlap has to mean something, or the index is matching everything.
        Assert.Empty(await store.RecallAsync("明天去公园散步"));
    }

    [Fact]
    public async Task A_mixed_record_is_reachable_from_its_chinese_half()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("m1", "部署流程 deploy pipeline 走蓝绿"));

        // Only the CJK runs carry bigrams; the Latin half is indexed by the other table. A Chinese question has to
        // reach a record that is mostly English.
        Assert.Contains(await store.RecallAsync("蓝绿的部署"), h => h.RecordId == "m1");
    }

    [Fact]
    public async Task A_mixed_query_reaches_a_record_through_its_english_half()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("m1", "the deployment pipeline runs nightly"));
        await store.WriteAsync(Rec("m2", "蓝绿部署流程"));

        // The query has Chinese in it, but the only thing it shares with m1 is the English word. Choosing the
        // index by "does the query contain CJK" would send this to the bigram table alone, where m1 has no entry.
        var hits = await store.RecallAsync("部署 deployment");

        Assert.Contains(hits, h => h.RecordId == "m1");
        // And the union keeps the half the bigram index can serve.
        Assert.Contains(hits, h => h.RecordId == "m2");
    }

    [Fact]
    public async Task A_mixed_record_supersedes_an_english_one_through_its_english_half()
    {
        await using var store = Open();
        await store.WriteAsync(Rec(
            "old",
            "stage the staging cluster with terraform and then restart the gateway service"));

        // The newcomer is mostly the same claim plus a Chinese word. With the index chosen by script, the
        // candidate lookup would only see bigrams and find nothing to retire.
        await store.WriteAsync(Rec(
            "new",
            "部署 stage the staging cluster with terraform and then restart the gateway daemon"));

        var superseded = await store.GetAsync("old");
        Assert.NotNull(superseded!.ValidTo);
        Assert.Equal("new", superseded.SupersededBy);
    }

    [Fact]
    public async Task The_index_follows_a_revision()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("m1", "用户不喜欢被反复确认"));

        await store.ReviseAsync("m1", "用户希望先看结论", Actor.Human);

        // Both halves move: the new wording finds it, the old wording does not. An index left holding the old
        // body would answer the opposite way on each.
        Assert.Contains(await store.RecallAsync("希望先看结论"), h => h.RecordId == "m1");
        Assert.DoesNotContain(await store.RecallAsync("反复确认"), h => h.RecordId == "m1");
    }

    [Fact]
    public async Task The_index_follows_a_delete()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("m1", "用户不喜欢被反复确认"));

        await store.SoftDeleteAsync("m1", Actor.Human);

        // The purge has to reach both indexes: a record that is gone from the bodies but still in a bigram index
        // would keep answering queries with words the person asked to have removed.
        Assert.Empty(await store.RecallAsync("我不太喜欢被打断"));
    }

    [Fact]
    public async Task Rebuilding_the_index_keeps_the_chinese_answers()
    {
        await using var store = Open();
        await store.WriteAsync(Rec("m1", "用户不喜欢被反复确认"));

        await store.RebuildIndexAsync();

        Assert.Contains(await store.RecallAsync("我不太喜欢被打断"), h => h.RecordId == "m1");
    }

    [Fact]
    public async Task A_span_without_chinese_carries_no_bigrams()
    {
        // A body with nothing to cut leaves body_cjk empty, which is the common case for an English store: the
        // index entry is empty rather than absent, and an empty entry matches nothing.
        await using var store = Open();
        await store.WriteAsync(Rec("m1", "deployment pipeline"));

        Assert.Empty(await store.RecallAsync("明天去公园散步"));
        Assert.Contains(await store.RecallAsync("deployment"), h => h.RecordId == "m1");
    }
}
