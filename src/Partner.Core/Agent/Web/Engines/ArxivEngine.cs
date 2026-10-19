using System.Globalization;
using System.Xml.Linq;

namespace Partner.Core.Agent.Web.Engines;

/// <summary>
/// arXiv's API, Atom over HTTPS. Structured, no block page, and every entry carries a submission date — the
/// specialist that makes <c>kind=academic</c> worth asking for.
/// </summary>
public sealed class ArxivEngine : IWebSearchEngine
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    private const string Api = "https://export.arxiv.org/api/query?sortBy=relevance&sortOrder=descending&search_query=all:";

    public EngineTraits Traits { get; } = new(
        Id: "arxiv",
        Name: "arXiv",
        Kinds: ContentKinds.Academic,
        Scripts: QueryScripts.Latin,

        // Latin only, declared rather than discovered: arXiv indexes English abstracts, and a Chinese query
        // against it returns near-misses that look like results. The planner filters it out instead.
        Handles: null,
        Nature: ResultNature.Ranked,
        SingleSite: true,
        ExemptFromHostFilter: true,

        // arXiv publishes a guideline of roughly one request every three seconds; a burst earns a temporary ban.
        // It is a willing queue-er, unlike the general engines, because there is no substitute for it.
        Rate: new RateLimit(20, TimeSpan.FromSeconds(3)),
        Role: EnginePoolRole.Specialist);

    public HttpRequestMessage Build(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{Api}{Uri.EscapeDataString(query.Text)}&start=0&max_results={Math.Clamp(query.Limit, 1, 20)}");

        // Identified, not disguised: arXiv publishes a policy asking automated clients to say who they are, and
        // the media type goes in as the one Accept header rather than alongside a generic default.
        WebTransport.ApplyHeaders(
            request,
            "en;q=0.9,zh;q=0.4",
            WebRequestKind.Data,
            "application/atom+xml,application/xml;q=0.9,*/*;q=0.5");
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

        if (status is 403 or 429 or 503)
        {
            return Task.FromResult(ParseResult.Blocked($"arxiv: HTTP {status}"));
        }

        if (status >= 400)
        {
            return Task.FromResult(ParseResult.Unparseable($"arxiv: HTTP {status}"));
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException error)
        {
            return Task.FromResult(ParseResult.Unparseable($"arxiv: XML 解析失败（{error.Message}）"));
        }

        var feed = document.Root;
        if (feed is null || feed.Name != Atom + "feed")
        {
            return Task.FromResult(ParseResult.Unparseable("arxiv: 响应不是 Atom feed"));
        }

        var hits = new List<SearchHit>();
        var rank = 0;

        foreach (var entry in feed.Elements(Atom + "entry"))
        {
            var title = Collapse(entry.Element(Atom + "title")?.Value);
            if (title.Length == 0)
            {
                continue;
            }

            // <link rel="alternate"> is the abstract page; <id> is the same URL in a different coat. Whichever
            // is present, it is a URL a person can open.
            var url = entry.Elements(Atom + "link")
                .FirstOrDefault(link => (string?)link.Attribute("rel") == "alternate")
                ?.Attribute("href")?.Value
                ?? entry.Element(Atom + "id")?.Value;

            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var published = DateTimeOffset.TryParse(
                entry.Element(Atom + "published")?.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal,
                out var parsed)
                    ? parsed
                    : (DateTimeOffset?)null;

            var authors = entry.Elements(Atom + "author")
                .Select(author => Collapse(author.Element(Atom + "name")?.Value))
                .Where(name => name.Length > 0)
                .ToList();

            var summary = Collapse(entry.Element(Atom + "summary")?.Value);
            var byline = authors.Count == 0
                ? string.Empty
                : authors.Count <= 3
                    ? string.Join("、", authors)
                    : string.Join("、", authors.Take(3)) + " 等";

            hits.Add(new SearchHit(
                Title: title,
                Url: url,
                Snippet: byline.Length == 0 ? summary : $"{byline}。{summary}",
                Published: published,
                DateSource: published is null ? DateSource.None : DateSource.Structured,
                Engine: Traits.Id,
                Rank: rank++));
        }

        return Task.FromResult(hits.Count == 0 ? ParseResult.Empty("arxiv: 没有论文") : ParseResult.Ok(hits));
    }

    private static string Collapse(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
