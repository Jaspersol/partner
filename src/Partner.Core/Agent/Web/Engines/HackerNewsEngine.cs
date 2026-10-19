using System.Globalization;
using System.Text.Json;

namespace Partner.Core.Agent.Web.Engines;

/// <summary>
/// Hacker News search, served by Algolia's index of it. Included for the one thing it does that a general engine
/// does not: it dates every submission and ranks by community attention, so "what is being said about this
/// lately" has an answer with timestamps attached.
/// </summary>
public sealed class HackerNewsEngine : IWebSearchEngine
{
    private const string Api = "https://hn.algolia.com/api/v1/search?tags=story&query=";

    public EngineTraits Traits { get; } = new(
        Id: "hackernews",
        Name: "Hacker News",
        Kinds: ContentKinds.News | ContentKinds.Qa | ContentKinds.General,
        Scripts: QueryScripts.Latin,
        Handles: null,
        Nature: ResultNature.Ranked,

        // Results link out to whatever was submitted, so this is not a single-site catalogue and the decoy check
        // applies to it like any other general index.
        SingleSite: false,
        ExemptFromHostFilter: false,
        Rate: RateLimit.Unlimited,
        Role: EnginePoolRole.Base);

    public HttpRequestMessage Build(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{Api}{Uri.EscapeDataString(query.Text)}&hitsPerPage={Math.Clamp(query.Limit, 1, 20)}");

        WebTransport.ApplyHeaders(request, "en;q=0.9,zh;q=0.4", WebRequestKind.Data, "application/json");
        return request;
    }

    public Task<ParseResult> ParseAsync(
        WebResponse response,
        SearchQuery query,
        IEngineFollowUp follow,
        CancellationToken ct = default)
    {
        var body = response.Body ?? string.Empty;
        var status = response.Status;

        // Algolia rate-limits by IP, and 429 is that, not a parser failure: backing off is the right response and
        // a cooldown is how this pipeline backs off.
        if (status is 403 or 429)
        {
            return Task.FromResult(ParseResult.Blocked($"hackernews: HTTP {status}"));
        }

        if (status >= 400)
        {
            return Task.FromResult(ParseResult.Unparseable($"hackernews: HTTP {status}"));
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            {
                return Task.FromResult(ParseResult.Unparseable("hackernews: 响应里没有 hits"));
            }

            var results = new List<SearchHit>();
            var rank = 0;

            foreach (var hit in hits.EnumerateArray())
            {
                var title = Text(hit, "title");
                var objectId = Text(hit, "objectID");
                if (title.Length == 0 || objectId.Length == 0)
                {
                    // Algolia also serves comments and job posts through the same index; neither has a title a
                    // reader can use, so they are skipped rather than dressed up.
                    continue;
                }

                // A submission with no url is a text post, and the discussion page is then the item itself.
                var url = Text(hit, "url");
                if (url.Length == 0)
                {
                    url = $"https://news.ycombinator.com/item?id={objectId}";
                }

                var created = Date(hit, "created_at");
                var points = hit.TryGetProperty("points", out var pointValue) && pointValue.TryGetInt32(out var score)
                    ? score
                    : 0;
                var comments = hit.TryGetProperty("num_comments", out var commentValue) && commentValue.TryGetInt32(out var count)
                    ? count
                    : 0;

                results.Add(new SearchHit(
                    Title: title,
                    Url: url,
                    Snippet: $"{points} 赞 · {comments} 条讨论 · 提交于 {created:yyyy-MM-dd}",
                    Published: created,
                    DateSource: created is null ? DateSource.None : DateSource.Structured,
                    Engine: Traits.Id,
                    Rank: rank++));
            }

            return Task.FromResult(results.Count == 0 ? ParseResult.Empty("hackernews: 没有讨论") : ParseResult.Ok(results));
        }
        catch (JsonException error)
        {
            return Task.FromResult(ParseResult.Unparseable($"hackernews: JSON 解析失败（{error.Message}）"));
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
