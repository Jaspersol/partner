using Partner.Core.Agent.Web;
using Partner.Core.Agent.Web.Engines;

namespace Partner.Core.Tests.Agent.Web.Engines;

/// <summary>
/// arXiv's Atom feed. Two things here are worth more than the field mapping: the feed indents its summary across
/// lines, so every value has to be collapsed before it becomes a snippet, and a paper with nine authors has to
/// read as a byline rather than as a wall of names.
/// </summary>
public sealed class ArxivEngineTests
{
    private readonly ArxivEngine _engine = new();

    private static SearchQuery Query(string text = "llm agent") => new(text);

    private Task<ParseResult> ParseAsync(string fixture, int status = 200) =>
        _engine.ParseAsync(Fixtures.Response(fixture, status), Query(), new NoFollowUp());

    private Task<ParseResult> ParseBodyAsync(string body, int status = 200) =>
        _engine.ParseAsync(new WebResponse(status, body, FinalUri: null, ContentType: "application/atom+xml", Error: null), Query(), new NoFollowUp());

    [Fact]
    public async Task Parse_ReadsTheFeed()
    {
        var result = await ParseAsync("arxiv-atom.xml");

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal(2, result.Hits.Count);

        var first = result.Hits[0];
        Assert.Equal("Tool-Augmented Language Models: A Survey of Planning and Memory", first.Title);

        // The abstract page, not the PDF: it is the URL a person can read without a download.
        Assert.Equal("http://arxiv.org/abs/2405.12345v2", first.Url);
        Assert.Equal("arxiv", first.Engine);
        Assert.Equal(0, first.Rank);
        Assert.Equal(new DateTimeOffset(2026, 5, 20, 9, 3, 14, TimeSpan.Zero), first.Published!.Value);
        Assert.Equal(DateSource.Structured, first.DateSource);
    }

    [Fact]
    public async Task Parse_CollapsesTheSummaryOntoOneLine()
    {
        // The feed wraps the summary across lines with indentation inside the XML itself. Left as it came, the
        // snippet would carry that indentation into the prompt.
        var result = await ParseAsync("arxiv-atom.xml");

        Assert.Equal(
            "Wei Zhang、Ana Rodrigues、Tomas Lindqvist。We survey how language models plan and remember when they "
            + "are given tools, with attention to the failure modes that appear when the tool set changes "
            + "underneath a running plan.",
            result.Hits[0].Snippet);
    }

    [Fact]
    public async Task Parse_ShortensALongAuthorListRatherThanPrintingIt()
    {
        var result = await ParseAsync("arxiv-atom.xml");

        Assert.StartsWith("Chen Li、Marta Nowak、Priya Raman 等。", result.Hits[1].Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsAnEmptyFeedAsEmpty()
    {
        var result = await ParseAsync("arxiv-empty.xml");

        Assert.Equal(ParseStatus.Empty, result.Status);
        Assert.Empty(result.Hits);
        Assert.Contains("没有论文", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsMalformedXmlAsUnparseable()
    {
        var result = await ParseBodyAsync("<feed><entry><title>x</title>");

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("XML", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsWellFormedXmlThatIsNotAFeedAsUnparseable()
    {
        // A maintenance page served with an XML content type parses perfectly well and is not a result set.
        var result = await ParseBodyAsync("<?xml version=\"1.0\"?><html><body>Down for maintenance</body></html>");

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("Atom", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_FallsBackToTheIdentifierWhenThereIsNoAlternateLink()
    {
        // <link rel="alternate"> is the abstract page and <id> is the same URL in a different coat, so whichever
        // is there is the destination. The second entry also carries its title across lines, which is the other
        // thing the collapsing is for.
        var result = await ParseBodyAsync(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>http://arxiv.org/abs/2401.00001v1</id>
                <title>Only an id, no alternate link</title>
                <summary>An abstract.</summary>
              </entry>
              <entry>
                <id>http://arxiv.org/abs/2401.00002v1</id>
                <title>
                    A title spread over lines
                </title>
                <link href="http://arxiv.org/abs/2401.00002v1" rel="alternate" type="text/html"/>
                <summary>Another abstract.</summary>
              </entry>
            </feed>
            """);

        Assert.Equal(2, result.Hits.Count);
        Assert.Equal("http://arxiv.org/abs/2401.00001v1", result.Hits[0].Url);
        Assert.Equal("Only an id, no alternate link", result.Hits[0].Title);
        Assert.Equal("A title spread over lines", result.Hits[1].Title);
        Assert.Equal("http://arxiv.org/abs/2401.00002v1", result.Hits[1].Url);

        // No author on either: the snippet is then the abstract, with no byline in front of it.
        Assert.Equal("An abstract.", result.Hits[0].Snippet);
        Assert.Null(result.Hits[0].Published);
        Assert.Equal(DateSource.None, result.Hits[0].DateSource);
    }

    [Theory]
    [InlineData(403, ParseStatus.Blocked)]
    [InlineData(503, ParseStatus.Blocked)]
    [InlineData(500, ParseStatus.Unparseable)]
    public async Task Parse_ReadsATemporaryBanAsABlockAndAnythingElseAsAParseFailure(int status, ParseStatus expected)
    {
        // 503 is in the block list on purpose: arXiv says "come back later" that way, and coming back later is
        // exactly what the cooldown does.
        var result = await ParseAsync("arxiv-atom.xml", status);

        Assert.Equal(expected, result.Status);
        Assert.Contains($"HTTP {status}", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AsksTheAtomEndpointAndCapsTheResultCount()
    {
        var request = _engine.Build(Query("llm agent") with { Limit = 50 });
        var url = request.RequestUri!.AbsoluteUri;

        Assert.StartsWith("https://export.arxiv.org/api/query?", url, StringComparison.Ordinal);
        Assert.Contains($"search_query=all:{Uri.EscapeDataString("llm agent")}", url, StringComparison.Ordinal);
        Assert.Contains("sortBy=relevance", url, StringComparison.Ordinal);
        Assert.Contains("max_results=20", url, StringComparison.Ordinal);

        // Asked for by name: the same endpoint serves the same feed to anything that says it accepts anything.
        // The engine's own media type goes on alongside the shared browser default rather than in place of it, so
        // the header is read as a list and the type is looked for among what was sent.
        Assert.Contains(
            "application/atom+xml",
            string.Join(",", request.Headers.GetValues("Accept")),
            StringComparison.Ordinal);
    }
}
