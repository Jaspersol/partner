using Partner.Core.Agent.Web;
using Partner.Core.Tests.Agent.Web.Engines;

namespace Partner.Core.Tests.Agent.Web;

public sealed class ConfiguredWebEngineTests
{
    private static readonly WebEngineDefinition Definition = new(
        "custom", "Custom", UrlTemplate: "https://example.test/search?q={query}",
        ResultsPath: "data.results", TitleField: "item.title", UrlField: "item.url", SnippetField: "item.summary");

    private static IWebSearchEngine Engine => Assert.Single(ConfiguredWebEngines.Resolve([Definition]));

    private static Task<ParseResult> Parse(string body) => Engine.ParseAsync(
        new WebResponse(200, body, FinalUri: null, ContentType: "application/json", Error: null),
        new SearchQuery("test"), new NoFollowUp());

    [Fact]
    public void Build_EncodesQueryAsOneParameter()
    {
        using var request = Engine.Build(new SearchQuery("中文 & a+b"));
        Assert.Equal("?q=%E4%B8%AD%E6%96%87%20%26%20a%2Bb", request.RequestUri!.Query);
    }

    [Fact]
    public async Task Parse_ReadsNestedFieldsAndSkipsInvalidItems()
    {
        var result = await Parse("""{"data":{"results":[{"item":{"title":"Title","url":"https://example.test/a","summary":"Summary"}},{"item":{"title":"Missing URL"}}]}}""");
        Assert.Equal(ParseStatus.Ok, result.Status);
        var hit = Assert.Single(result.Hits);
        Assert.Equal("Title", hit.Title);
        Assert.Equal("Summary", hit.Snippet);
        Assert.Equal("custom", hit.Engine);
    }

    [Fact]
    public async Task Parse_LimitsValidHitsRatherThanInputItems()
    {
        const string body = """{"data":{"results":[{}, {"item":{"title":"First","url":"https://example.test/1"}}, {"item":{"title":"Second","url":"https://example.test/2"}}, {"item":{"title":"Third","url":"https://example.test/3"}}]}}""";
        var result = await Engine.ParseAsync(new WebResponse(200, body, null, "application/json", null),
            new SearchQuery("test", Limit: 2), new NoFollowUp());
        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal(new[] { "First", "Second" }, result.Hits.Select(hit => hit.Title));
        Assert.Equal(new[] { 0, 1 }, result.Hits.Select(hit => hit.Rank));
    }

    [Theory]
    [InlineData("{\"data\":{\"results\":[]}}", ParseStatus.Empty)]
    [InlineData("{\"data\":{\"results\":[{}]}}", ParseStatus.Unparseable)]
    [InlineData("{\"data\":{\"results\":{}}}", ParseStatus.Unparseable)]
    [InlineData("not json", ParseStatus.Unparseable)]
    public async Task Parse_DistinguishesEmptyFromInvalid(string body, ParseStatus expected)
    {
        Assert.Equal(expected, (await Parse(body)).Status);
    }

    [Fact]
    public async Task Search_ExplainsWhenNoConfiguredEngineIsEnabled()
    {
        using var service = new WebSearchService(
            () => WebOptions.Default with { Engines = [Definition with { Enabled = false }] },
            Path.GetTempPath());
        var result = await service.SearchAsync("test", null, null, null);
        Assert.False(result.Ok);
        Assert.Contains("没有启用的搜索引擎", result.Content);
    }

    [Fact]
    public void HealthIdentity_ChangesWithEndpointAndMappingButNotName()
    {
        var original = ConfiguredWebEngines.HealthId(Definition);
        Assert.Equal(original, ConfiguredWebEngines.HealthId(Definition with { Name = "Renamed" }));
        Assert.NotEqual(original, ConfiguredWebEngines.HealthId(Definition with { UrlTemplate = "https://other.test/?q={query}" }));
        Assert.NotEqual(original, ConfiguredWebEngines.HealthId(Definition with { UrlField = "link" }));
    }

    [Fact]
    public async Task Search_RevisionSeparatesCachedAndInFlightResults()
    {
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var options = WebOptions.Default with { Engines = [Definition], Revision = 1 };
        async Task<WebResponse> Send(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            var isOld = request.RequestUri!.Host == "example.test";
            if (isOld) { oldStarted.SetResult(); await releaseOld.Task.WaitAsync(ct); }
            var title = isOld ? "Old result" : "New result";
            var body = System.Text.Json.JsonSerializer.Serialize(new { data = new { results = new[] { new { item = new { title, url = "https://result.test/page" } } } } });
            return new WebResponse(200, body, request.RequestUri, "application/json", null);
        }

        using var service = new WebSearchService(() => options, Path.GetTempPath(), send: Send);
        var old = service.SearchAsync("same query", null, null, null);
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        options = options with { Revision = 2, Engines = [Definition with { UrlTemplate = "https://new.test/search?q={query}" }] };
        var current = await service.SearchAsync("same query", null, null, null);
        Assert.Contains("New result", current.Content);
        releaseOld.SetResult();
        Assert.Contains("Old result", (await old).Content);
        var cached = await service.SearchAsync("same query", null, null, null);
        Assert.Contains("New result", cached.Content);
        Assert.DoesNotContain("Old result", cached.Content);
        Assert.Equal(2, calls);
        options = options with { Revision = 3 };
        await service.SearchAsync("same query", null, null, null);
        Assert.Equal(3, calls);
    }
}
