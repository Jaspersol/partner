using Partner.Core.Agent.Web;
using Partner.Core.Agent.Web.Engines;

namespace Partner.Core.Tests.Agent.Web.Engines;

/// <summary>
/// The DuckDuckGo result page. This is the engine whose failure modes are the reason a parser reports a category
/// rather than a hit list: one endpoint serves results, a captcha and a page that is neither, and the three have
/// to reach the circuit breaker as three different things.
/// <para>
/// It asks the <c>lite</c> endpoint, so the lite shape is what most of these tests pin; the <c>html</c> shape
/// is tested too because the parser still reads it.
/// </para>
/// </summary>
public sealed class DuckDuckGoEngineTests
{
    private readonly DuckDuckGoEngine _engine = new();

    private static SearchQuery Query(string text = "rust book") => new(text);

    private Task<ParseResult> ParseAsync(string fixture, int status = 200) =>
        _engine.ParseAsync(Fixtures.Response(fixture, status), Query(), new NoFollowUp());

    [Fact]
    public async Task Parse_ReadsTheLiteResultPage()
    {
        var result = await ParseAsync("duckduckgo-lite.html");

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal(2, result.Hits.Count);

        var first = result.Hits[0];
        Assert.Equal("The Rust Programming Language", first.Title);
        Assert.Equal("https://doc.rust-lang.org/book/", first.Url);
        Assert.Equal("The official book — read it cover to cover.", first.Snippet);
        Assert.Equal("duckduckgo", first.Engine);
        Assert.Equal(0, first.Rank);

        // The page states no date anywhere, and reading one into the URL or the snippet is exactly what the
        // date-source field exists to prevent.
        Assert.Null(first.Published);
        Assert.Equal(DateSource.None, first.DateSource);

        Assert.Equal(1, result.Hits[1].Rank);
    }

    [Fact]
    public async Task Parse_TakesEachSnippetFromItsOwnRow()
    {
        // On the lite page the snippet is not inside the result: it is the row after it. Walking forward has to
        // stop at the next result, or the last result would borrow the snippet above it.
        var result = await ParseAsync("duckduckgo-lite.html");

        Assert.Equal("Rust is a general-purpose programming language.", result.Hits[1].Snippet);
    }

    [Fact]
    public async Task Parse_SkipsTheAnchorWithoutADestination()
    {
        // Three anchors on the page, two hits: the third points back inside the site rather than at a result.
        var result = await ParseAsync("duckduckgo-lite.html");

        Assert.DoesNotContain(result.Hits, hit => hit.Title == "Settings");
        Assert.Equal(new[] { 0, 1 }, result.Hits.Select(hit => hit.Rank));
    }

    [Fact]
    public async Task Parse_StoresTheDestinationBehindTheClickTracker()
    {
        // Every organic result is wrapped as /l/?uddg=<encoded>. Keeping the wrapper would make every hit a
        // duckduckgo.com URL, which dedupes against nothing and costs a redirect for anyone who opens it.
        var result = await ParseAsync("duckduckgo-lite.html");

        Assert.All(result.Hits, hit => Assert.DoesNotContain("duckduckgo.com", hit.Url, StringComparison.Ordinal));
        Assert.Equal("https://en.wikipedia.org/wiki/Rust_(programming_language)", result.Hits[1].Url);
    }

    [Fact]
    public async Task Parse_StillReadsTheFullHtmlPage()
    {
        // Both endpoints are the same service, so losing one must not lose the engine.
        var result = await ParseAsync("duckduckgo-results.html");

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal(2, result.Hits.Count);
        Assert.Equal("https://doc.rust-lang.org/book/", result.Hits[0].Url);
        Assert.Equal("The official book — read it cover to cover.", result.Hits[0].Snippet);

        // The sponsored container is marked as such, and the fourth anchor points back inside the site.
        Assert.All(result.Hits, hit => Assert.DoesNotContain("Learn Rust Fast", hit.Title, StringComparison.Ordinal));
        Assert.All(result.Hits, hit => Assert.DoesNotContain("ads.example.com", hit.Url, StringComparison.Ordinal));
        Assert.Equal(new[] { 0, 1 }, result.Hits.Select(hit => hit.Rank));
    }

    [Fact]
    public async Task Parse_ReportsAnAnsweredPageWithNothingInItAsEmpty()
    {
        var lite = await ParseAsync("duckduckgo-lite-no-results.html");
        var full = await ParseAsync("duckduckgo-no-results.html");

        Assert.Equal(ParseStatus.Empty, lite.Status);
        Assert.Empty(lite.Hits);
        Assert.Contains("没有结果", lite.Note!, StringComparison.Ordinal);

        Assert.Equal(ParseStatus.Empty, full.Status);
        Assert.Empty(full.Hits);
    }

    [Fact]
    public async Task Parse_ReportsAChallengeAsBlocked()
    {
        // Told apart from a redesign deliberately: a challenge earns the engine a cooldown, and a page this
        // parser simply does not understand must not.
        var result = await ParseAsync("duckduckgo-blocked.html");

        Assert.Equal(ParseStatus.Blocked, result.Status);
        Assert.Contains("验证", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsAnUnknownLayoutAsUnparseable()
    {
        var result = await ParseAsync("duckduckgo-redesigned.html");

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("结构", result.Note!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    public async Task Parse_ReportsARefusedRequestAsBlocked(int status)
    {
        // The status is read before the body: a refusal is a refusal whatever page came with it.
        var result = await ParseAsync("duckduckgo-lite.html", status);

        Assert.Equal(ParseStatus.Blocked, result.Status);
        Assert.Contains($"HTTP {status}", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AsksTheLiteEndpointForTheLanguageOfTheQuery()
    {
        // The lite page is the plain table: less to fetch and parse, and less interesting to the challenge
        // machinery in front of the full page.
        var chinese = _engine.Build(Query("北京天气"));
        var chineseUrl = chinese.RequestUri!.AbsoluteUri;

        Assert.StartsWith("https://lite.duckduckgo.com/lite/?q=", chineseUrl, StringComparison.Ordinal);
        Assert.Contains($"q={Uri.EscapeDataString("北京天气")}", chineseUrl, StringComparison.Ordinal);
        Assert.Contains("kl=cn-zh", chineseUrl, StringComparison.Ordinal);
        // The list header is stored parsed and printed back in its canonical form, space after each semicolon.
        Assert.Equal("zh-CN,zh; q=0.9,en; q=0.6", string.Join(",", chinese.Headers.GetValues("Accept-Language")));

        var latin = _engine.Build(Query("rust book"));

        Assert.Contains("kl=us-en", latin.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("en; q=0.9,zh; q=0.4", string.Join(",", latin.Headers.GetValues("Accept-Language")));
    }

    [Fact]
    public void Build_AsksForAPageOnce()
    {
        // One Accept value only: a second would be an engine appending its own media type to the default.
        var request = _engine.Build(Query("rust book"));

        Assert.Equal(
            new[] { "text/html", "application/xhtml+xml", "application/xml; q=0.9", "image/avif", "image/webp", "*/*; q=0.8" },
            request.Headers.GetValues("Accept"));
    }
}
