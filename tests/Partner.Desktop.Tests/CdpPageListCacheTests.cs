using System.Net;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tests;

/// <summary>One action used to ask the browser for its tab list three or four times; now it asks once.</summary>
public sealed class CdpPageListCacheTests
{
    private const string Tabs =
        """[{"type":"page","title":"音乐","url":"https://example.org","webSocketDebuggerUrl":"ws://127.0.0.1:1/devtools/page/A"}]""";

    private sealed class CountingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Repeated_lookups_of_the_same_page_ask_the_browser_once()
    {
        var handler = new CountingHandler(Tabs);
        var source = new CdpDomSource(new HttpClient(handler));

        for (var i = 0; i < 4; i++)
        {
            Assert.NotNull(await source.ConnectCachedAsync(9222, "音乐"));
        }

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_different_window_title_is_a_different_lookup()
    {
        var handler = new CountingHandler(Tabs);
        var source = new CdpDomSource(new HttpClient(handler));

        await source.ConnectCachedAsync(9222, "音乐");
        await source.ConnectCachedAsync(9222, "别的窗口");

        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task A_browser_that_has_no_page_is_asked_again_at_once()
    {
        var handler = new CountingHandler("[]");
        var source = new CdpDomSource(new HttpClient(handler));

        Assert.Null(await source.ConnectCachedAsync(9222, "音乐"));
        Assert.Null(await source.ConnectCachedAsync(9222, "音乐"));

        Assert.Equal(2, handler.Requests);
    }
}
