using System.Globalization;
using System.Xml.Linq;

namespace Partner.Core.Agent.Web.Engines;

/// <summary>
/// Bing's result feed. The second breadth source, and the one that carries Chinese queries well: the mainland
/// host serves them without a redirect, so a query written in Chinese is asked of <c>cn.bing.com</c>.
/// <para>
/// It asks for <c>format=rss</c> rather than for the HTML page. The RSS view is the same search, rendered as a
/// feed, and it is a far better thing to depend on: no script has to run, no challenge is served on that path, and
/// the fields are named rather than found by CSS class. The previous parser read <c>li.b_algo</c> and the
/// <c>b_lineclamp*</c> family, whose shape could not be checked from this machine; the feed removes that whole
/// class of risk rather than guarding against it.
/// </para>
/// </summary>
public sealed class BingEngine : IWebSearchEngine
{
    private const string ApiTemplate = "https://{0}/search?format=rss&q=";

    public EngineTraits Traits { get; } = new(
        Id: "bing",
        Name: "Bing",
        Kinds: ContentKinds.General | ContentKinds.News,
        Scripts: QueryScripts.Any,
        Handles: null,
        Nature: ResultNature.Ranked,
        SingleSite: false,
        ExemptFromHostFilter: false,
        Rate: new RateLimit(20, TimeSpan.FromSeconds(1)),
        Role: EnginePoolRole.Base);

    public HttpRequestMessage Build(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var cjk = QueryScript.HasCjk(query.Text);
        var host = cjk ? "cn.bing.com" : "www.bing.com";
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{string.Format(ApiTemplate, host)}{Uri.EscapeDataString(query.Text)}"
                + $"&count={Math.Clamp(query.Limit, 1, 20)}&setlang={(cjk ? "zh-Hans" : "en")}");

        WebTransport.ApplyHeaders(
            request,
            cjk ? "zh-CN,zh;q=0.9,en;q=0.6" : "en;q=0.9,zh;q=0.4",
            accept: "application/rss+xml,application/xml;q=0.9,*/*;q=0.5");
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
        if (status is 403 or 429)
        {
            return Task.FromResult(ParseResult.Blocked($"bing: HTTP {status}"));
        }

        if (status >= 400)
        {
            return Task.FromResult(ParseResult.Unparseable($"bing: HTTP {status}"));
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException error)
        {
            return Task.FromResult(NoFeed($"bing: 响应不是 RSS（{error.Message}）", body));
        }

        var channel = document.Root?.Element("channel");
        if (document.Root?.Name != "rss" || channel is null)
        {
            // A challenge page is usually not well-formed XML, but it can be, and then it arrives here instead of
            // at the catch above. Both ways of arriving at "no feed" ask the same question.
            return Task.FromResult(NoFeed("bing: 响应不是 RSS 频道", body));
        }

        var hits = new List<SearchHit>();
        var rank = 0;

        foreach (var item in channel.Elements("item"))
        {
            var title = HtmlText.StripTags(item.Element("title")?.Value);
            if (title.Length == 0)
            {
                continue;
            }

            var link = (item.Element("link")?.Value ?? string.Empty).Trim();
            if (!Uri.TryCreate(link, UriKind.Absolute, out _))
            {
                // Bing occasionally lists a result it will not hand a destination for. A hit with nowhere to go
                // is worse than absent.
                continue;
            }

            var published = DateTimeOffset.TryParse(
                item.Element("pubDate")?.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal,
                out var parsed)
                    ? parsed
                    : (DateTimeOffset?)null;

            hits.Add(new SearchHit(
                Title: title,
                Url: link,
                Snippet: HtmlText.StripTags(item.Element("description")?.Value),
                Published: published,

                // Only when the feed carried one: a result with no pubDate is of unknown age, and saying so is
                // the point of the field.
                DateSource: published is null ? DateSource.None : DateSource.Structured,
                Engine: Traits.Id,
                Rank: rank++));
        }

        return Task.FromResult(hits.Count == 0 ? ParseResult.Empty("bing: 没有结果") : ParseResult.Ok(hits));
    }

    /// <summary>
    /// What a body that yielded no feed is. A challenge if it says so and a parse failure otherwise: the two reach
    /// the circuit breaker as different things, and only one of them earns a cooldown. Asked only once the body has
    /// produced nothing, so a search whose own results mention a challenge cannot be mistaken for one.
    /// </summary>
    private static ParseResult NoFeed(string parseError, string body) =>
        HtmlText.LooksLikeBlockPage(null, body)
            ? ParseResult.Blocked("bing: 返回了验证或同意页")
            : ParseResult.Unparseable(parseError);
}
