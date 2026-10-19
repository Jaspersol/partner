using Partner.Core.Agent.Web;
using Partner.Core.Agent.Web.Engines;

namespace Partner.Core.Tests.Agent.Web.Engines;

/// <summary>
/// GitHub's repository search, unauthenticated. The interesting part is the snippet: the three facts a person uses
/// to judge a repository without opening it are assembled from fields that may each be missing, and a repository
/// with no description still has to come back with something true to say.
/// </summary>
public sealed class GitHubEngineTests
{
    private readonly GitHubEngine _engine = new();

    private static SearchQuery Query(string text = "local tts") => new(text);

    private Task<ParseResult> ParseAsync(string fixture, int status = 200) =>
        _engine.ParseAsync(Fixtures.Response(fixture, status), Query(), new NoFollowUp());

    private Task<ParseResult> ParseBodyAsync(string body, int status = 200) =>
        _engine.ParseAsync(new WebResponse(status, body, FinalUri: null, ContentType: "application/json", Error: null), Query(), new NoFollowUp());

    [Fact]
    public async Task Parse_ReadsTheRepositoryList()
    {
        var result = await ParseAsync("github-repositories.json");

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal(2, result.Hits.Count);

        var first = result.Hits[0];
        Assert.Equal("rust-lang/rust", first.Title);
        Assert.Equal("https://github.com/rust-lang/rust", first.Url);
        Assert.Equal("Empowering everyone to build reliable and efficient software.（★ 104223 · Rust · 最近推送 2026-10-18）", first.Snippet);
        Assert.Equal("github", first.Engine);
        Assert.Equal(0, first.Rank);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 21, 44, 2, TimeSpan.Zero), first.Published!.Value);
        Assert.Equal(DateSource.Structured, first.DateSource);
    }

    [Fact]
    public async Task Parse_SaysTheFactsAloneWhenThereIsNoDescription()
    {
        // No description, no language. What is left is still three true things rather than an empty snippet.
        var result = await ParseAsync("github-repositories.json");

        Assert.Equal("★ 12 · 最近推送 2026-09-20", result.Hits[1].Snippet);
        Assert.Equal("example-org/pipeline-tools", result.Hits[1].Title);
    }

    [Fact]
    public async Task Parse_SkipsAnItemWithNothingToOpen()
    {
        var result = await ParseBodyAsync(
            """
            {"total_count":2,"incomplete_results":false,"items":[
              {"full_name":"rust-lang/rust","html_url":"https://github.com/rust-lang/rust","stargazers_count":5,"description":"a language"},
              {"full_name":"example-org/no-url","description":"the api left the url out"}
            ]}
            """);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("rust-lang/rust", hit.Title);
        Assert.Equal(0, hit.Rank);
    }

    [Fact]
    public async Task Parse_ReportsAnAnsweredSearchWithNothingInItAsEmpty()
    {
        var result = await ParseBodyAsync("""{"total_count":0,"incomplete_results":false,"items":[]}""");

        Assert.Equal(ParseStatus.Empty, result.Status);
        Assert.Empty(result.Hits);
        Assert.Contains("没有仓库", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsAResponseWithNoItemListAsUnparseable()
    {
        var result = await ParseBodyAsync("""{"message":"Not Found","documentation_url":"https://docs.github.com/rest"}""");

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("items", result.Note!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(403, ParseStatus.Blocked)]
    [InlineData(429, ParseStatus.Blocked)]
    [InlineData(422, ParseStatus.Unparseable)]
    [InlineData(500, ParseStatus.Unparseable)]
    public async Task Parse_CallsBothQuotaStatusesABlock(int status, ParseStatus expected)
    {
        // Ten searches a minute on a shared address: 403 is the quota, not permission. Both are worth backing off
        // over, and both reach the circuit breaker through the same door as a captcha.
        var result = await ParseAsync("github-repositories.json", status);

        Assert.Equal(expected, result.Status);
        Assert.Contains($"HTTP {status}", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AsksTheSearchEndpointAndCapsThePage()
    {
        var request = _engine.Build(Query("local tts") with { Limit = 50 });
        var url = request.RequestUri!.AbsoluteUri;

        Assert.StartsWith("https://api.github.com/search/repositories?", url, StringComparison.Ordinal);
        Assert.Contains($"q={Uri.EscapeDataString("local tts")}", url, StringComparison.Ordinal);
        Assert.Contains("sort=stars", url, StringComparison.Ordinal);
        Assert.Contains("per_page=20", url, StringComparison.Ordinal);

        // The versioned media type is what keeps the shape this parser was written against. It is added alongside
        // the shared browser default rather than in place of it, so the header is read as a list.
        Assert.Contains(
            "application/vnd.github+json",
            string.Join(",", request.Headers.GetValues("Accept")),
            StringComparison.Ordinal);

        Assert.Contains("per_page=3", _engine.Build(Query() with { Limit = 3 }).RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }
}
