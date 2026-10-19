using System.Text.Json;

namespace Partner.Core.Agent.Web.Engines;

/// <summary>
/// Wikipedia through its own action API: no scraping, no consent wall, a stable JSON shape, and a real timestamp
/// on every hit — which is why it is in the base set even though it is a catalogue of one site.
/// </summary>
public sealed class WikipediaEngine : IWebSearchEngine
{
    private const string Api = "https://{0}.wikipedia.org/w/api.php?action=query&format=json&utf8=1&list=search"
        + "&srprop=snippet%7Ctimestamp&srsearch=";

    public EngineTraits Traits { get; } = new(
        Id: "wikipedia",
        Name: "Wikipedia",
        Kinds: ContentKinds.Encyclopedia | ContentKinds.General,
        Scripts: QueryScripts.Any,
        Handles: null,
        Nature: ResultNature.Ranked,
        SingleSite: true,
        ExemptFromHostFilter: true,
        Rate: RateLimit.Unlimited,
        Role: EnginePoolRole.Specialist);

    public HttpRequestMessage Build(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var language = LanguageOf(query);
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{string.Format(Api, language)}{Uri.EscapeDataString(query.Text)}&srlimit={Math.Clamp(query.Limit, 1, 20)}");

        // The action API is an interface with a published policy for automated clients, so it is called by name
        // rather than by a browser whose script markers are missing.
        WebTransport.ApplyHeaders(
            request,
            language == "zh" ? "zh-CN,zh;q=0.9,en;q=0.5" : "en;q=0.9,zh;q=0.4",
            WebRequestKind.Data,
            "application/json");
        return request;
    }

    public Task<ParseResult> ParseAsync(
        WebResponse response,
        SearchQuery query,
        IEngineFollowUp follow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var body = response.Body ?? string.Empty;
        var status = response.Status;

        // 429 is the API's own rate limit and 403 is what a shared address earns; both mean "come back later",
        // which is what a cooldown does.
        if (status is 403 or 429)
        {
            return Task.FromResult(ParseResult.Blocked($"wikipedia: HTTP {status}"));
        }

        if (status >= 400)
        {
            return Task.FromResult(ParseResult.Unparseable($"wikipedia: HTTP {status}"));
        }

        var language = LanguageOf(query);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("query", out var payload)
                || !payload.TryGetProperty("search", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return Task.FromResult(ParseResult.Unparseable("wikipedia: 响应里没有 query.search"));
            }

            var hits = new List<SearchHit>();
            var rank = 0;

            foreach (var item in results.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var published = item.TryGetProperty("timestamp", out var stamp)
                    && stamp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(stamp.GetString(), out var parsed)
                        ? parsed
                        : (DateTimeOffset?)null;

                hits.Add(new SearchHit(
                    Title: title,
                    Url: $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}",
                    Snippet: HtmlText.StripTags(item.TryGetProperty("snippet", out var snippet) ? snippet.GetString() : null),
                    Published: published,

                    // The API returned a real timestamp field, so this is first-hand and may be filtered on.
                    DateSource: published is null ? DateSource.None : DateSource.Structured,
                    Engine: Traits.Id,
                    Rank: rank++));
            }

            return Task.FromResult(hits.Count == 0 ? ParseResult.Empty("wikipedia: 没有条目") : ParseResult.Ok(hits));
        }
        catch (JsonException error)
        {
            // A JSON error here means a portal page or an error document, not a result set.
            var blocked = HtmlText.LooksLikeBlockPage(null, body);
            return Task.FromResult(blocked
                ? ParseResult.Blocked("wikipedia: 返回了拦截页")
                : ParseResult.Unparseable($"wikipedia: JSON 解析失败（{error.Message}）"));
        }
    }

    /// <summary>Chinese query, Chinese Wikipedia. The language is a property of the query, not of this machine.</summary>
    private static string LanguageOf(SearchQuery query) => QueryScript.HasCjk(query.Text) ? "zh" : "en";
}
