using System.Globalization;
using System.Text.Json;

namespace Partner.Core.Agent.Web.Engines;

/// <summary>
/// GitHub's repository search, unauthenticated. The one source that answers "which library does this" with
/// stars, a language and a last-push date — three facts that let a person judge the answer without opening it.
/// </summary>
public sealed class GitHubEngine : IWebSearchEngine
{
    private const string Api = "https://api.github.com/search/repositories?sort=stars&order=desc&q=";

    public EngineTraits Traits { get; } = new(
        Id: "github",
        Name: "GitHub",
        Kinds: ContentKinds.Code,
        Scripts: QueryScripts.Any,
        Handles: null,
        Nature: ResultNature.Ranked,
        SingleSite: true,
        ExemptFromHostFilter: true,

        // GitHub allows ten unauthenticated searches a minute. A token is not an option: this capability is
        // credential-free by design, so it queues briefly and then gives up its slot to someone else.
        Rate: new RateLimit(10, TimeSpan.FromSeconds(2)),
        Role: EnginePoolRole.Specialist);

    public HttpRequestMessage Build(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{Api}{Uri.EscapeDataString(query.Text)}&per_page={Math.Clamp(query.Limit, 1, 20)}");

        // The versioned media type is what keeps the shape this parser was written against, and it is the only
        // Accept header the request carries.
        WebTransport.ApplyHeaders(request, "en;q=0.9,zh;q=0.4", WebRequestKind.Data, "application/vnd.github+json");
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

        // Both 403 and 429 from this endpoint mean "the shared address is out of quota", which is the same shape
        // as a block as far as the circuit breaker is concerned, and is worth backing off over.
        if (status is 403 or 429)
        {
            return Task.FromResult(ParseResult.Blocked($"github: 限额（HTTP {status}）"));
        }

        if (status >= 400)
        {
            return Task.FromResult(ParseResult.Unparseable($"github: HTTP {status}"));
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return Task.FromResult(ParseResult.Unparseable("github: 响应里没有 items"));
            }

            var hits = new List<SearchHit>();
            var rank = 0;

            foreach (var item in items.EnumerateArray())
            {
                var fullName = Text(item, "full_name");
                var url = Text(item, "html_url");
                if (fullName.Length == 0 || url.Length == 0)
                {
                    continue;
                }

                var pushed = Date(item, "pushed_at");
                var stars = item.TryGetProperty("stargazers_count", out var starValue) && starValue.TryGetInt64(out var count)
                    ? count
                    : 0;

                var facts = new List<string> { $"★ {stars.ToString(CultureInfo.InvariantCulture)}" };
                if (Text(item, "language") is { Length: > 0 } language)
                {
                    facts.Add(language);
                }

                if (pushed is { } stamp)
                {
                    facts.Add($"最近推送 {stamp:yyyy-MM-dd}");
                }

                var description = Text(item, "description");
                var snippet = description.Length == 0
                    ? string.Join(" · ", facts)
                    : $"{description}（{string.Join(" · ", facts)}）";

                hits.Add(new SearchHit(
                    Title: fullName,
                    Url: url,
                    Snippet: snippet,
                    Published: pushed,
                    DateSource: pushed is null ? DateSource.None : DateSource.Structured,
                    Engine: Traits.Id,
                    Rank: rank++));
            }

            return Task.FromResult(hits.Count == 0 ? ParseResult.Empty("github: 没有仓库") : ParseResult.Ok(hits));
        }
        catch (JsonException error)
        {
            return Task.FromResult(ParseResult.Unparseable($"github: JSON 解析失败（{error.Message}）"));
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
