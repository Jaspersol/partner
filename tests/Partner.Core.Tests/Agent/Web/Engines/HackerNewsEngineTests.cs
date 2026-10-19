using Partner.Core.Agent.Web;
using Partner.Core.Agent.Web.Engines;

namespace Partner.Core.Tests.Agent.Web.Engines;

/// <summary>
/// The Algolia index of Hacker News. It answers through the same endpoint for stories, job posts and comments, so
/// the parser's first job is deciding which of those it is holding: only a story is something a reader can open.
/// </summary>
public sealed class HackerNewsEngineTests
{
    private readonly HackerNewsEngine _engine = new();

    private static SearchQuery Query(string text = "local voice assistant") => new(text);

    private Task<ParseResult> ParseAsync(string fixture, int status = 200) =>
        _engine.ParseAsync(Fixtures.Response(fixture, status), Query(), new NoFollowUp());

    private Task<ParseResult> ParseBodyAsync(string body, int status = 200) =>
        _engine.ParseAsync(new WebResponse(status, body, FinalUri: null, ContentType: "application/json", Error: null), Query(), new NoFollowUp());

    [Fact]
    public async Task Parse_ReadsTheStoryIndex()
    {
        var result = await ParseAsync("hackernews-search.json");

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal(2, result.Hits.Count);

        var first = result.Hits[0];
        Assert.Equal("Show HN: I built a local voice assistant that never leaves the LAN", first.Title);
        Assert.Equal("https://example.com/local-voice", first.Url);
        Assert.Equal("342 赞 · 118 条讨论 · 提交于 2026-09-28", first.Snippet);
        Assert.Equal("hackernews", first.Engine);
        Assert.Equal(0, first.Rank);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 14, 33, 12, TimeSpan.Zero), first.Published!.Value);
        Assert.Equal(DateSource.Structured, first.DateSource);
    }

    [Fact]
    public async Task Parse_PointsATextPostAtItsOwnDiscussion()
    {
        // The second hit was submitted with no url, so the discussion page is itself the destination — and the
        // comment in the same batch is not a result at all.
        var result = await ParseAsync("hackernews-search.json");

        Assert.Equal("Ask HN: What do you use for local speech-to-text?", result.Hits[1].Title);
        Assert.Equal("https://news.ycombinator.com/item?id=43112207", result.Hits[1].Url);
        Assert.Equal("88 赞 · 64 条讨论 · 提交于 2026-09-21", result.Hits[1].Snippet);

        Assert.All(result.Hits, hit => Assert.DoesNotContain("whisper.cpp", hit.Snippet, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parse_CountsMissingPointsAndCommentsAsZero()
    {
        // A story the index has not scored yet: the vote count is part of what is being reported, so zero is the
        // honest reading and an absent number is not.
        var result = await ParseBodyAsync(
            """
            {"hits":[{"title":"A story with no score yet","objectID":"1","url":"https://example.com/a","created_at":"2026-10-01T12:00:00Z"}]}
            """);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("0 赞 · 0 条讨论 · 提交于 2026-10-01", hit.Snippet);
    }

    [Fact]
    public async Task Parse_ReportsAnAnsweredSearchWithNothingInItAsEmpty()
    {
        var result = await ParseBodyAsync("""{"hits":[],"nbHits":0,"page":0,"nbPages":0}""");

        Assert.Equal(ParseStatus.Empty, result.Status);
        Assert.Empty(result.Hits);
        Assert.Contains("没有讨论", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsAResponseWithNoHitListAsUnparseable()
    {
        var result = await ParseBodyAsync("""{"message":"Unauthorized","status":401}""");

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("hits", result.Note!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(403, ParseStatus.Blocked)]
    [InlineData(429, ParseStatus.Blocked)]
    [InlineData(500, ParseStatus.Unparseable)]
    public async Task Parse_ReadsTheStatusBeforeTheBody(int status, ParseStatus expected)
    {
        // A refusal is a refusal whatever document came with it: 403 and 429 are the shapes a service uses to say
        // "not you, not now", and only those earn a cooldown. A 500 is the service being unwell, which is a
        // different thing and must not cost the engine one.
        var result = await ParseAsync("hackernews-search.json", status);

        Assert.Equal(expected, result.Status);
        Assert.Contains($"HTTP {status}", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AsksForStoriesAndCapsThePage()
    {
        var request = _engine.Build(Query("local voice assistant") with { Limit = 50 });
        var url = request.RequestUri!.AbsoluteUri;

        Assert.StartsWith("https://hn.algolia.com/api/v1/search?", url, StringComparison.Ordinal);
        Assert.Contains("tags=story", url, StringComparison.Ordinal);
        Assert.Contains($"query={Uri.EscapeDataString("local voice assistant")}", url, StringComparison.Ordinal);
        Assert.Contains("hitsPerPage=20", url, StringComparison.Ordinal);

        Assert.Contains("hitsPerPage=5", _engine.Build(Query() with { Limit = 5 }).RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }
}
