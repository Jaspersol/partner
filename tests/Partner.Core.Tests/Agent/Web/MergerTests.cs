using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web;

/// <summary>
/// The last quality gate: one ranked list out of several, scored by rank and by agreement and by nothing else.
/// The tests fall into two groups — what counts as the same page, and what one engine's result on that page is
/// worth — and both are places where being wrong is invisible in the output, which is why they are pinned here.
/// </summary>
public sealed class MergerTests
{
    private static IReadOnlyList<SearchHit> Merge(params SearchHit[] hits) => new Merger().Merge(hits);

    [Fact]
    public void UrlIdentity_FoldsSpellingButNotPublishers()
    {
        var canonical = Merger.UrlIdentity("https://example.com/a");

        Assert.Equal(canonical, Merger.UrlIdentity("http://www.example.com/a/"));
        Assert.Equal(canonical, Merger.UrlIdentity("https://example.com:443/a"));
        Assert.Equal(canonical, Merger.UrlIdentity("https://example.com/a#conclusion"));

        // A country domain is a different publisher, and a query string is how a site serves an article — folding
        // either of them would merge two different pages into one answer.
        Assert.NotEqual(canonical, Merger.UrlIdentity("https://example.cn/a"));
        Assert.NotEqual(canonical, Merger.UrlIdentity("https://example.com/a?id=7"));
        Assert.NotEqual(canonical, Merger.UrlIdentity("https://example.com/a?id=8"));
        Assert.NotEqual(canonical, Merger.UrlIdentity("https://example.com:8443/a"));
    }

    [Fact]
    public void Merge_GroupsTheSamePageFromTwoEnginesAndNamesBoth()
    {
        var merged = Merge(
            FakeEngines.Hit("duckduckgo", "Quantum error correction", rank: 0, url: "https://example.com/x"),
            FakeEngines.Hit("bing", "Quantum error correction", rank: 1, url: "https://www.example.com/x/"));

        var hit = Assert.Single(merged);
        Assert.Equal(new[] { "bing", "duckduckgo" }, hit.Sources);
        Assert.Equal(0, hit.Rank);
    }

    [Fact]
    public void Merge_MergesARepostByItsTitleWhenTheAddressesDiffer()
    {
        var merged = Merge(
            FakeEngines.Hit("duckduckgo", "Quantum error correction milestone reached", rank: 0, url: "https://a.test/x"),
            FakeEngines.Hit("bing", "Quantum error correction milestone reached (updated)", rank: 1, url: "https://b.test/y"));

        Assert.Single(merged);
    }

    [Fact]
    public void Merge_KeepsTwoVersionsOfTheSameStoryApart()
    {
        // The single most damaging thing a deduplicator can do is answer the question with the wrong version.
        // These two titles overlap completely and are still two results.
        const string older = "Apple releases iOS 17.4 with security fixes for everyone";
        const string newer = "Apple releases iOS 17.5 with security fixes for everyone";

        Assert.Equal(1.0, Merger.TitleSimilarity(older, newer));

        var merged = Merge(
            FakeEngines.Hit("duckduckgo", older, rank: 0, url: "https://a.test/x"),
            FakeEngines.Hit("bing", newer, rank: 1, url: "https://b.test/y"));

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_TakesTheDateFromTheResultThatKnowsItFirstHand()
    {
        var structured = FakeEngines.Hit(
            "bing",
            "Same story",
            rank: 1,
            url: "https://example.com/x",
            published: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            dateSource: DateSource.Structured);

        var scraped = FakeEngines.Hit(
            "duckduckgo",
            "Same story",
            rank: 0,
            url: "https://example.com/x",
            published: new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero),
            dateSource: DateSource.Text);

        var hit = Assert.Single(Merge(scraped, structured));

        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), hit.Published!.Value);
        Assert.Equal(DateSource.Structured, hit.DateSource);
    }

    [Fact]
    public void Merge_PrefersTheEarlierDateWhenBothKnowItEquallyWell()
    {
        var first = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        var repost = new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.Zero);

        var hit = Assert.Single(Merge(
            FakeEngines.Hit("duckduckgo", "Story", rank: 0, url: "https://example.com/x", published: repost, dateSource: DateSource.Structured),
            FakeEngines.Hit("bing", "Story", rank: 1, url: "https://example.com/x", published: first, dateSource: DateSource.Structured)));

        Assert.Equal(first, hit.Published!.Value);
    }

    [Fact]
    public void Merge_AnUndatedResultRankedFirstDoesNotHideTheDateTheOthersHave()
    {
        var published = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

        var hit = Assert.Single(Merge(
            FakeEngines.Hit("duckduckgo", "Story", rank: 0, url: "https://example.com/x"),
            FakeEngines.Hit("bing", "Story", rank: 1, url: "https://example.com/x", published: published, dateSource: DateSource.Text)));

        Assert.Equal(published, hit.Published!.Value);
        Assert.Equal(DateSource.Text, hit.DateSource);

        // The undated result still supplies the title, because it is the better-ranked one.
        Assert.Equal("duckduckgo", hit.Engine);
    }

    [Fact]
    public void Merge_ReportsNoDateAtAllWhenNobodyHasOne()
    {
        // 日期不详, not "recent": a missing date is a fact the answer has to state rather than smooth over.
        var hit = Assert.Single(Merge(
            FakeEngines.Hit("duckduckgo", "Story", rank: 0, url: "https://example.com/x"),
            FakeEngines.Hit("bing", "Story", rank: 1, url: "https://www.example.com/x/")));

        Assert.Null(hit.Published);
        Assert.Equal(DateSource.None, hit.DateSource);
    }

    [Fact]
    public void Merge_KeepsTheLongestSnippetAndTheSecureAddress()
    {
        var hit = Assert.Single(Merge(
            FakeEngines.Hit("duckduckgo", "Story", "short", rank: 0, url: "http://example.com/x"),
            FakeEngines.Hit("bing", "Story", "a considerably longer summary of the same page", rank: 1, url: "https://example.com/x")));

        Assert.Equal("a considerably longer summary of the same page", hit.Snippet);
        Assert.Equal("https://example.com/x", hit.Url);

        // The title comes from the best-ranked result, not from the one that supplied the snippet.
        Assert.Equal("duckduckgo", hit.Engine);
    }

    [Fact]
    public void Merge_AgreementBeatsBeingFirstOnOneEngine()
    {
        var merged = Merge(
            FakeEngines.Hit("duckduckgo", "Third on both", rank: 3, url: "https://example.com/both"),
            FakeEngines.Hit("bing", "Third on both", rank: 3, url: "https://example.com/both"),
            FakeEngines.Hit("hackernews", "First on one", rank: 0, url: "https://example.com/one"));

        Assert.Equal("https://example.com/both", merged[0].Url);
        Assert.Equal(2, merged[0].Sources.Count);
    }

    [Fact]
    public void Merge_WeighsALookedUpRecordAboveTheWholeDefaultPool()
    {
        var merged = Merge(
            FakeEngines.Hit("open-meteo", "Tokyo 21°C", rank: 2, url: "https://api.open-meteo.test/tokyo"),
            FakeEngines.Hit("duckduckgo", "Tokyo weather", rank: 0, url: "https://a.test/1"),
            FakeEngines.Hit("bing", "Tokyo climate", rank: 0, url: "https://b.test/2"),
            FakeEngines.Hit("hackernews", "Tokyo trip report", rank: 0, url: "https://c.test/3"));

        Assert.Equal("open-meteo", merged[0].Engine);
    }

    [Fact]
    public void Merge_WeighsASpecialistAboveBreadth()
    {
        // One expert hit outranks two base hits ranked below it, and does not outrank three.
        var merged = Merge(
            FakeEngines.Hit("wikipedia", "Encyclopaedia entry", rank: 0, url: "https://wikipedia.test/a"),
            FakeEngines.Hit("duckduckgo", "Some page", rank: 0, url: "https://a.test/1"));

        Assert.Equal("wikipedia", merged[0].Engine);
    }

    [Fact]
    public void Merge_ReindexesTheFinalList()
    {
        var merged = Merge(
            FakeEngines.Hit("duckduckgo", "First thing", rank: 5, url: "https://a.test/1"),
            FakeEngines.Hit("bing", "Second thing", rank: 5, url: "https://b.test/2"),
            FakeEngines.Hit("hackernews", "Third thing", rank: 5, url: "https://c.test/3"));

        Assert.Equal(new[] { 0, 1, 2 }, merged.Select(hit => hit.Rank));
    }

    [Fact]
    public void Merge_OfNothingIsNothing() => Assert.Empty(Merge());
}
