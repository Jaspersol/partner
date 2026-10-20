using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web;

/// <summary>
/// The headers one request wears. A browser identity is only worth anything if it is consistent: the set Chrome
/// sends for a page it is navigating to differs from the set it sends for a fetch, and a Chrome user agent carrying
/// neither is more conspicuous than an honest client. The other half of the same rule is that endpoints which
/// publish a policy for automated clients — Wikipedia, arXiv — are called by a named client, not by a browser with
/// its script markers missing.
/// </summary>
public sealed class WebTransportHeaderTests
{
    /// <summary>
    /// The user agent as one line. .NET parses a User-Agent into its products, so the pieces come back separately
    /// and joining them with the space they were written with is what puts the header back together.
    /// </summary>
    private static string UserAgentOf(HttpRequestMessage request) =>
        string.Join(" ", request.Headers.GetValues("User-Agent"));

    [Fact]
    public void ApplyHeaders_DressesAPageAsANavigation()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");

        WebTransport.ApplyHeaders(request, "en;q=0.9");

        Assert.StartsWith("Mozilla/5.0", UserAgentOf(request), StringComparison.Ordinal);

        // The four Fetch Metadata headers a top-level navigation carries, plus the hint that says the address was
        // typed or followed rather than fetched by a script.
        Assert.Equal("none", request.Headers.GetValues("Sec-Fetch-Site").Single());
        Assert.Equal("navigate", request.Headers.GetValues("Sec-Fetch-Mode").Single());
        Assert.Equal("document", request.Headers.GetValues("Sec-Fetch-Dest").Single());
        Assert.Equal("?1", request.Headers.GetValues("Sec-Fetch-User").Single());
        Assert.Equal("1", request.Headers.GetValues("Upgrade-Insecure-Requests").Single());

        // The client hints Chrome sends with them. A browser without them is a browser that is not a browser.
        Assert.Contains("Chromium", request.Headers.GetValues("sec-ch-ua").Single(), StringComparison.Ordinal);
        Assert.Equal("?0", request.Headers.GetValues("sec-ch-ua-mobile").Single());
        Assert.Equal("\"Windows\"", request.Headers.GetValues("sec-ch-ua-platform").Single());
    }

    [Fact]
    public void ApplyHeaders_CallsADataEndpointByName()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://en.wikipedia.org/w/api.php");

        WebTransport.ApplyHeaders(request, "en;q=0.9", WebRequestKind.Data, "application/json");

        // Identified, not disguised. Pretending to be Chrome at an interface whose operator publishes a policy
        // against exactly that is the one case where the disguise costs access.
        Assert.Equal(WebTransport.IdentifiedUserAgent, UserAgentOf(request));
        Assert.Equal("application/json", request.Headers.GetValues("Accept").Single());
        // The list header is stored parsed and printed back in its canonical form, space after the semicolon.
        Assert.Equal("en; q=0.9", request.Headers.GetValues("Accept-Language").Single());

        // None of the navigation markers: this is not a page and nothing about the request claims it is.
        Assert.False(request.Headers.Contains("Sec-Fetch-Mode"));
        Assert.False(request.Headers.Contains("Sec-Fetch-Dest"));
        Assert.False(request.Headers.Contains("Upgrade-Insecure-Requests"));
        Assert.False(request.Headers.Contains("sec-ch-ua"));
    }

    [Fact]
    public void ApplyHeaders_AsksForThePageOnceAndSendsNoCacheDirective()
    {
        // Engines used to append their own media type on top of the generic default, which sent two Accept headers
        // and a Cache-Control a browser does not send. Both are the kind of detail a challenge page reads before it
        // decides whether to answer. A list header comes back from .NET one value per media type, so the count of
        // values is what says whether anything was appended.
        var page = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        var data = new HttpRequestMessage(HttpMethod.Get, "https://example.com/api");

        WebTransport.ApplyHeaders(page, "en;q=0.9");
        WebTransport.ApplyHeaders(data, "en;q=0.9", WebRequestKind.Data);

        Assert.Equal(
            new[] { "text/html", "application/xhtml+xml", "application/xml; q=0.9", "image/avif", "image/webp", "*/*; q=0.8" },
            page.Headers.GetValues("Accept"));

        // A data endpoint asks for everything, because the caller that knows what it can read says so itself.
        Assert.Equal("*/*", data.Headers.GetValues("Accept").Single());

        Assert.False(page.Headers.Contains("Cache-Control"));
        Assert.False(data.Headers.Contains("Cache-Control"));
    }

    [Fact]
    public void ApplyHeaders_LetsTheCallerChooseTheMediaTypeForAPage()
    {
        // A page request is not always HTML: a feed is a page too, and asking for it by name keeps the request
        // honest without adding a second Accept header.
        var request = new HttpRequestMessage(HttpMethod.Get, "https://www.bing.com/search?format=rss");

        WebTransport.ApplyHeaders(
            request,
            "en;q=0.9",
            WebRequestKind.Document,
            "application/rss+xml,application/xml;q=0.9,*/*;q=0.5");

        Assert.Equal(
            new[] { "application/rss+xml", "application/xml; q=0.9", "*/*; q=0.5" },
            request.Headers.GetValues("Accept"));

        // Still dressed as a navigation: the feed is a page, and the endpoint serving it expects a page request.
        Assert.Equal("document", request.Headers.GetValues("Sec-Fetch-Dest").Single());
    }

    [Fact]
    public void ApplyHeaders_RejectsNothing()
    {
        Assert.Throws<ArgumentNullException>(() => WebTransport.ApplyHeaders(null!, "en"));
    }

    [Fact]
    public void AcceptLanguageFor_ClaimsWhatTheHostServes()
    {
        // The redirect hop is re-dressed from the address it is going to, so this is what decides the language a
        // second hop claims.
        Assert.Equal("zh-CN,zh;q=0.9,en;q=0.6", WebTransport.AcceptLanguageFor(new Uri("https://zh.wikipedia.org/wiki/X")));
        Assert.Equal("zh-CN,zh;q=0.9,en;q=0.8", WebTransport.AcceptLanguageFor(new Uri("https://en.wikipedia.org/wiki/X")));
        Assert.Equal("zh-CN,zh;q=0.9,en;q=0.8", WebTransport.AcceptLanguageFor(new Uri("https://example.com/")));
    }

    [Fact]
    public void A_data_request_remembers_its_kind_so_a_redirect_hop_stays_a_data_request()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1");

        WebTransport.ApplyHeaders(request, "en;q=0.9", WebRequestKind.Data);

        Assert.True(request.Options.TryGetValue(WebTransport.KindKey, out var kind));
        Assert.Equal(WebRequestKind.Data, kind);
    }
}
