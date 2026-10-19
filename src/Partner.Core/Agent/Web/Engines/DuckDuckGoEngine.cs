using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Partner.Core.Agent.Web.Engines;

/// <summary>
/// DuckDuckGo's no-JavaScript result page. The primary breadth source, and the one most likely to hand back a
/// challenge instead of results — which is why its parser distinguishes "the page is a captcha" from "the markup
/// changed", all the way out to the circuit breaker.
/// <para>
/// It asks the <c>lite</c> endpoint. That is the same search as the <c>html</c> one, in a page built for slow
/// connections: a plain table, no scripts, nothing to render. It is both lighter to parse and a far less
/// interesting target for the challenge machinery that sits in front of the full page. The parser still
/// understands the older <c>html</c> shape, because both endpoints are the same service and the day one of them
/// is retired is not a day to lose the engine.
/// </para>
/// </summary>
public sealed class DuckDuckGoEngine : IWebSearchEngine
{
    private const string Api = "https://lite.duckduckgo.com/lite/?q=";

    private static readonly HtmlParser Parser = new(new HtmlParserOptions { IsKeepingSourceReferences = false });

    public EngineTraits Traits { get; } = new(
        Id: "duckduckgo",
        Name: "DuckDuckGo",
        Kinds: ContentKinds.General | ContentKinds.News,
        Scripts: QueryScripts.Any,
        Handles: null,
        Nature: ResultNature.Ranked,
        SingleSite: false,
        ExemptFromHostFilter: false,

        // Not a published number: this is the pace the endpoint tolerates without starting to serve challenges.
        // A throttled engine is skipped rather than queued, so exceeding it costs a turn, not a temporary ban.
        Rate: new RateLimit(20, TimeSpan.FromSeconds(1)),
        Role: EnginePoolRole.Base);

    public HttpRequestMessage Build(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var cjk = QueryScript.HasCjk(query.Text);
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{Api}{Uri.EscapeDataString(query.Text)}&kl={(cjk ? "cn-zh" : "us-en")}");

        WebTransport.ApplyHeaders(request, cjk ? "zh-CN,zh;q=0.9,en;q=0.6" : "en;q=0.9,zh;q=0.4");
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
            return Task.FromResult(ParseResult.Blocked($"duckduckgo: HTTP {status}"));
        }

        if (status >= 400)
        {
            return Task.FromResult(ParseResult.Unparseable($"duckduckgo: HTTP {status}"));
        }

        var document = Parser.ParseDocument(body);
        var title = document.Title;

        // The lite page marks a result link with `result-link`; the full page uses `result__a` inside a `.result`.
        // Both are read, and the two shapes differ only in where the snippet lives.
        var anchors = document.QuerySelectorAll("a.result-link");
        var lite = anchors.Length > 0;

        if (!lite)
        {
            anchors = document.QuerySelectorAll("a.result__a");
        }

        if (anchors.Length == 0)
        {
            if (HtmlText.LooksLikeBlockPage(title, body))
            {
                return Task.FromResult(ParseResult.Blocked("duckduckgo: 返回了验证或同意页"));
            }

            // The page answered and simply holds nothing: a real "no results", not a broken parser.
            if (document.QuerySelector("#links, .results, .no-results") is not null
                || body.Contains("No results", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ParseResult.Empty("duckduckgo: 没有结果"));
            }

            return Task.FromResult(ParseResult.Unparseable("duckduckgo: 结果页结构不认识了"));
        }

        var hits = new List<SearchHit>();
        var rank = 0;

        foreach (var anchor in anchors)
        {
            var container = anchor.Closest(".result");
            if (container?.ClassName?.Contains("result--ad", StringComparison.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            var href = anchor.GetAttribute("href") ?? string.Empty;
            var url = RealUrl(href);
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                continue;
            }

            var heading = HtmlText.StripTags(anchor.TextContent);
            if (heading.Length == 0)
            {
                continue;
            }

            var snippet = lite
                ? SnippetAfter(anchor)
                : container?.QuerySelector(".result__snippet")?.TextContent;

            hits.Add(new SearchHit(
                Title: heading,
                Url: url,
                Snippet: HtmlText.StripTags(snippet),
                Published: null,

                // The result page states no date anywhere, and guessing one from the URL or the snippet would be
                // exactly the dishonesty the date-source field exists to prevent.
                DateSource: DateSource.None,
                Engine: Traits.Id,
                Rank: rank++));
        }

        return Task.FromResult(hits.Count == 0 ? ParseResult.Empty("duckduckgo: 结果都是广告") : ParseResult.Ok(hits));
    }

    /// <summary>
    /// The lite page is a table: the title sits in one row and the snippet in the next one or two. Walking the
    /// rows forward from the link finds it, and stops at the next result so one snippet cannot be borrowed by the
    /// result above it.
    /// </summary>
    private static string? SnippetAfter(IElement anchor)
    {
        var row = anchor.Closest("tr");
        for (var next = row?.NextElementSibling; next is not null; next = next.NextElementSibling)
        {
            if (next.QuerySelector("a.result-link") is not null)
            {
                return null;
            }

            if (next.QuerySelector(".result-snippet") is { } snippet)
            {
                return snippet.TextContent;
            }
        }

        return null;
    }

    /// <summary>
    /// The destination behind the click-tracker. DuckDuckGo wraps every result as
    /// <c>/l/?uddg=&lt;encoded&gt;</c>, and storing the wrapper would make every result a duckduckgo.com URL —
    /// which then dedupes against nothing and fetches a redirect.
    /// </summary>
    private static string RealUrl(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            href = "https:" + href;
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        if (!uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/l/", StringComparison.Ordinal))
        {
            return uri.ToString();
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=', StringComparison.Ordinal);
            if (split > 0 && pair[..split] == "uddg")
            {
                return Uri.UnescapeDataString(pair[(split + 1)..]);
            }
        }

        return string.Empty;
    }
}
