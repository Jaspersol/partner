using Partner.Core.Agent.Web;
using Partner.Core.Agent.Web.Engines;

namespace Partner.Core.Tests.Agent.Web.Engines;

/// <summary>
/// Bing's result feed. Asking for <c>format=rss</c> is the point of this engine: the same search rendered as a
/// feed needs no script to run, serves no challenge on that path, and names its fields instead of hiding them
/// behind a CSS class that moves. What the tests pin is the feed's mapping — including the two entries Bing
/// lists that a reader cannot open, which have to be dropped rather than stored with an empty destination.
/// </summary>
public sealed class BingEngineTests
{
    private readonly BingEngine _engine = new();

    private static SearchQuery Query(string text = "rust book") => new(text);

    private Task<ParseResult> ParseAsync(string fixture, int status = 200) =>
        _engine.ParseAsync(Fixtures.Response(fixture, status), Query(), new NoFollowUp());

    private Task<ParseResult> ParseBodyAsync(string body, int status = 200) =>
        _engine.ParseAsync(new WebResponse(status, body, FinalUri: null, ContentType: "application/rss+xml", Error: null), Query(), new NoFollowUp());

    [Fact]
    public async Task Parse_ReadsTheFeed()
    {
        var result = await ParseAsync("bing-rss.xml");

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal(3, result.Hits.Count);

        var first = result.Hits[0];
        Assert.Equal("The Rust Programming Language", first.Title);
        Assert.Equal("https://doc.rust-lang.org/book/", first.Url);
        Assert.Equal("The official book about Rust — read it cover to cover.", first.Snippet);
        Assert.Equal("bing", first.Engine);
        Assert.Equal(0, first.Rank);

        // The feed carries a date where Bing has one, which makes the result filterable by recency.
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero), first.Published!.Value);
        Assert.Equal(DateSource.Structured, first.DateSource);

        Assert.Equal(new[] { 0, 1, 2 }, result.Hits.Select(hit => hit.Rank));
    }

    [Fact]
    public async Task Parse_LeavesTheDateUnknownWhenTheFeedCarriesNone()
    {
        // The second item has no pubDate. "Unknown" is the honest reading, and it is what keeps the result out of
        // a recency filter rather than letting it slip through as recent.
        var result = await ParseAsync("bing-rss.xml");

        Assert.Null(result.Hits[1].Published);
        Assert.Equal(DateSource.None, result.Hits[1].DateSource);
        Assert.Equal("Rust is a language empowering everyone to build reliable software.", result.Hits[1].Snippet);
    }

    [Fact]
    public async Task Parse_DropsTheEntriesThatCannotBeOpened()
    {
        // Five items, three hits: one has no link at all and one has no title. A result with nowhere to go or
        // nothing to call it is worse than absent.
        var result = await ParseAsync("bing-rss.xml");

        Assert.DoesNotContain(result.Hits, hit => hit.Title.Contains("without a destination", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Hits, hit => hit.Url.Contains("untitled", StringComparison.Ordinal));
        Assert.All(result.Hits, hit => Assert.StartsWith("https://", hit.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parse_ReportsAnAnsweredSearchWithNothingInItAsEmpty()
    {
        var result = await ParseAsync("bing-rss-empty.xml");

        Assert.Equal(ParseStatus.Empty, result.Status);
        Assert.Empty(result.Hits);
        Assert.Contains("没有结果", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsAChallengeAsBlocked()
    {
        // The challenge arrives instead of the feed, so it fails as XML first. Telling it apart from a broken feed
        // is what keeps a redesign from earning the engine a cooldown it did not deserve.
        var result = await ParseAsync("bing-blocked.html");

        Assert.Equal(ParseStatus.Blocked, result.Status);
        Assert.Contains("验证", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsMarkupThatIsNotAFeedAsUnparseable()
    {
        // Well-formed XML with the wrong root is a different failure from a challenge, and must not cost a
        // cooldown.
        var result = await ParseBodyAsync("<?xml version=\"1.0\"?><html><body>Down for maintenance</body></html>");

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("RSS", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsMalformedXmlAsUnparseable()
    {
        var result = await ParseBodyAsync("<rss><channel><item>");

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("RSS", result.Note!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    public async Task Parse_ReportsARefusedRequestAsBlocked(int status)
    {
        var result = await ParseAsync("bing-rss.xml", status);

        Assert.Equal(ParseStatus.Blocked, result.Status);
        Assert.Contains($"HTTP {status}", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsAServerErrorAsAParseFailureRatherThanABlock()
    {
        var result = await ParseAsync("bing-rss.xml", 500);

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("HTTP 500", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AsksForTheFeedOnTheHostThatCarriesTheQuery()
    {
        // The mainland host answers a Chinese query without a redirect; the other one is what a Latin query gets.
        var chinese = _engine.Build(Query("北京天气") with { Limit = 50 });
        var chineseUrl = chinese.RequestUri!.AbsoluteUri;

        Assert.StartsWith("https://cn.bing.com/search?format=rss&q=", chineseUrl, StringComparison.Ordinal);
        Assert.Contains($"q={Uri.EscapeDataString("北京天气")}", chineseUrl, StringComparison.Ordinal);
        Assert.Contains("setlang=zh-Hans", chineseUrl, StringComparison.Ordinal);

        // One result count is a query parameter a caller cannot inflate its way past.
        Assert.Contains("count=20", chineseUrl, StringComparison.Ordinal);

        var latin = _engine.Build(Query("rust book") with { Limit = 3 });
        var latinUrl = latin.RequestUri!.AbsoluteUri;

        Assert.StartsWith("https://www.bing.com/search?format=rss&q=", latinUrl, StringComparison.Ordinal);
        Assert.Contains("count=3", latinUrl, StringComparison.Ordinal);
        Assert.Contains("setlang=en", latinUrl, StringComparison.Ordinal);

        // One Accept header, asking for the feed and nothing else. Two entries here would be the old bug: the
        // generic page default left in place and the feed appended to it, which also sent text/html.
        Assert.Equal(
            new[] { "application/rss+xml", "application/xml; q=0.9", "*/*; q=0.5" },
            latin.Headers.GetValues("Accept"));
    }
}
