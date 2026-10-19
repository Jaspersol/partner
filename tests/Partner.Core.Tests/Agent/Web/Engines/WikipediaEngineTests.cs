using Partner.Core.Agent.Web;
using Partner.Core.Agent.Web.Engines;

namespace Partner.Core.Tests.Agent.Web.Engines;

/// <summary>
/// The one engine that is not scraping: the action API returns JSON, so its parser has no markup to guess at. What
/// it does have is two jobs the fixtures pin — turning a title into a URL somebody can open, and telling a real
/// answer from a wall that arrived instead of one.
/// </summary>
public sealed class WikipediaEngineTests
{
    private readonly WikipediaEngine _engine = new();

    private static SearchQuery Query(string text = "人工智能") => new(text);

    private Task<ParseResult> ParseAsync(string fixture, string query = "人工智能", int status = 200) =>
        _engine.ParseAsync(Fixtures.Response(fixture, status), Query(query), new NoFollowUp());

    private Task<ParseResult> ParseBodyAsync(string body, int status = 200) =>
        _engine.ParseAsync(new WebResponse(status, body, FinalUri: null, ContentType: "application/json", Error: null), Query(), new NoFollowUp());

    [Fact]
    public async Task Parse_ReadsTheChineseResultPage()
    {
        var result = await ParseAsync("wikipedia-search-zh.json");

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal(2, result.Hits.Count);

        var first = result.Hits[0];
        Assert.Equal("人工智能", first.Title);

        // The title is a phrase, not a URL, so it goes through percent-escaping on the way into the link. The
        // escaped form is what a browser and a terminal both open, and it is the form the wiki itself serves.
        Assert.Equal("https://zh.wikipedia.org/wiki/%E4%BA%BA%E5%B7%A5%E6%99%BA%E8%83%BD", first.Url);

        // The snippet arrives as HTML with the matched terms wrapped in spans, and with entities. Both have to be
        // gone before it reaches a prompt.
        Assert.Equal(
            "人工智能（英語：Artificial Intelligence，縮寫為 AI）是指由人製造出來的機器所表現出來的智能，"
            + "通常指通過普通電腦程式來呈現人類智能的技術。\"智能\"一詞源自希臘語。",
            first.Snippet);

        Assert.Equal("wikipedia", first.Engine);
        Assert.Equal(0, first.Rank);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 9, 12, 33, TimeSpan.Zero), first.Published!.Value);

        // The API carries a real timestamp field, which is what makes a Wikipedia hit filterable by recency.
        Assert.Equal(DateSource.Structured, first.DateSource);
    }

    [Fact]
    public async Task Parse_TurnsTheTitleIntoALinkSomebodyCanOpen()
    {
        // The title is a phrase, not a URL: the spaces in it are underscores in the wiki's own links, and the
        // parentheses of a disambiguated title are escaped rather than left to be read as punctuation.
        var result = await ParseAsync("wikipedia-search-en.json", "Rust programming language");

        Assert.Equal("https://en.wikipedia.org/wiki/Rust_%28programming_language%29", result.Hits[0].Url);
        Assert.Equal("https://en.wikipedia.org/wiki/Cargo_%28software%29", result.Hits[1].Url);
        Assert.Equal("Cargo is Rust's package manager and build system.", result.Hits[1].Snippet);

        // The language is a property of the query, not of the machine asking it.
        Assert.DoesNotContain("zh.wikipedia.org", result.Hits[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsAnAnsweredSearchWithNothingInItAsEmpty()
    {
        var result = await ParseBodyAsync("""{"batchcomplete":"","query":{"searchinfo":{"totalhits":0},"search":[]}}""");

        Assert.Equal(ParseStatus.Empty, result.Status);
        Assert.Empty(result.Hits);
        Assert.Contains("没有条目", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsAResponseWithNoSearchBlockAsUnparseable()
    {
        // An error document from the API itself. This must not read as "nothing matched".
        var result = await ParseBodyAsync("""{"error":{"code":"badvalue","info":"Unrecognized value for parameter"}}""");

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("query.search", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsAWallAsBlocked()
    {
        // Reached through the JSON failure branch, and told apart there: an HTML wall is a block, a malformed
        // error document is not.
        var result = await ParseAsync("wikipedia-blocked.html");

        Assert.Equal(ParseStatus.Blocked, result.Status);
        Assert.Contains("拦截页", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsHtmlThatIsNotAWallAsUnparseable()
    {
        // The JSON failure branch again, with nothing on the page that marks it as a challenge: this failure is
        // the parser's, and must not cost the engine a cooldown.
        var result = await ParseBodyAsync(
            "<html><head><title>Not found</title></head><body><h1>Not found</h1></body></html>");

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("JSON", result.Note!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(403, ParseStatus.Blocked, "wikipedia: HTTP 403")]
    [InlineData(429, ParseStatus.Blocked, "wikipedia: HTTP 429")]
    [InlineData(404, ParseStatus.Unparseable, "wikipedia: HTTP 404")]
    [InlineData(500, ParseStatus.Unparseable, "wikipedia: HTTP 500")]
    public async Task Parse_ReadsTheStatusBeforeTheBody(int status, ParseStatus expected, string note)
    {
        var result = await ParseAsync("wikipedia-search-zh.json", status: status);

        Assert.Equal(expected, result.Status);
        Assert.Contains(note, result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AsksTheWikiForTheLanguageOfTheQueryAndCapsTheLimit()
    {
        var chinese = _engine.Build(Query("人工智能") with { Limit = 50 });
        var chineseUrl = chinese.RequestUri!.AbsoluteUri;

        Assert.StartsWith("https://zh.wikipedia.org/w/api.php?", chineseUrl, StringComparison.Ordinal);
        Assert.Contains("action=query", chineseUrl, StringComparison.Ordinal);
        Assert.Contains("list=search", chineseUrl, StringComparison.Ordinal);
        Assert.Contains("srprop=snippet%7Ctimestamp", chineseUrl, StringComparison.Ordinal);
        Assert.Contains($"srsearch={Uri.EscapeDataString("人工智能")}", chineseUrl, StringComparison.Ordinal);
        Assert.Contains("srlimit=20", chineseUrl, StringComparison.Ordinal);
        // The list header is stored parsed and printed back in its canonical form, space after each semicolon.
        Assert.Equal("zh-CN,zh; q=0.9,en; q=0.5", string.Join(",", chinese.Headers.GetValues("Accept-Language")));

        var latin = _engine.Build(Query("rust book") with { Limit = 4 });

        Assert.StartsWith("https://en.wikipedia.org/w/api.php?", latin.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("srlimit=4", latin.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("en; q=0.9,zh; q=0.4", string.Join(",", latin.Headers.GetValues("Accept-Language")));
    }
}
