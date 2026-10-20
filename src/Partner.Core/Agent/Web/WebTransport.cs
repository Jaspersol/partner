using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Partner.Core.Agent.Web;

/// <summary>
/// What kind of request this is, which decides the headers it wears. The distinction exists because the honest
/// answer differs: a search result page is a page, and an API that publishes a policy for automated clients is
/// not. Wearing one set for both is what makes a client conspicuous.
/// </summary>
public enum WebRequestKind
{
    /// <summary>A page a person is looking at: the browser headers, script markers included.</summary>
    Document,

    /// <summary>A data endpoint: identified by name, asking only for the media type it can read.</summary>
    Data,
}

/// <summary>One response, already decoded and already capped, or the reason there is none.</summary>
/// <param name="Status">The HTTP status, or 0 when the request never completed.</param>
/// <param name="Body">The decoded text, or null on failure.</param>
/// <param name="FinalUri">Where the redirects ended up. The answer says this, because it is not what was asked for.</param>
/// <param name="ContentType">Media type without parameters, lower-cased.</param>
/// <param name="Error">A prefixed failure line (<c>denied:</c>, <c>unreachable:</c>, …), or null.</param>
public sealed record WebResponse(
    int Status,
    string? Body,
    Uri? FinalUri,
    string? ContentType,
    string? Error)
{
    public bool Ok => Error is null;

    public string? Charset { get; init; }
}

/// <summary>
/// The one HTTP client the whole capability uses: searches and page fetches share it, its headers, its cookie
/// container and its proxy.
/// <para>
/// Shared on purpose and not only for the connection pool. Two clients with two identities would be one more
/// thing for a source to notice, and a source that notices serves both of them a captcha.
/// </para>
/// <para>
/// Redirects are followed by hand rather than by the handler, because every hop has to pass
/// <see cref="WebAddressGuard"/> — a public URL that redirects to 127.0.0.1 is the ordinary shape of a
/// server-side request forgery, and a handler that follows redirects internally is a handler this code cannot
/// check.
/// </para>
/// </summary>
public sealed class WebTransport : IDisposable
{
    /// <summary>How much of one response is read. Past it the response is refused, not truncated.</summary>
    public const int MaxBodyBytes = 2 * 1024 * 1024;

    /// <summary>Redirect hops allowed before the request is called a loop.</summary>
    public const int MaxRedirects = 5;

    /// <summary>Per-request ceiling. The fanout has its own, larger, overall budget.</summary>
    public static readonly TimeSpan EngineTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Where <see cref="ApplyHeaders"/> records what kind of request it dressed, so a redirect can keep it.</summary>
    internal static readonly HttpRequestOptionsKey<WebRequestKind> KindKey = new("partner.web.kind");

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>
    /// The user agent for endpoints that are interfaces rather than pages. It says what this is and where it comes
    /// from, which is what Wikipedia and arXiv ask of automated clients; pretending to be Chrome at an API that
    /// publishes a policy against exactly that would be the one case where the disguise costs access.
    /// </summary>
    public const string IdentifiedUserAgent = "Partner/1.0 (+local desktop assistant; web search)";

    /// <summary>The brand list Chrome 126 sends, quoted the way the header wants it.</summary>
    private const string BrowserBrands =
        "\"Not/A)Brand\";v=\"8\", \"Chromium\";v=\"126\", \"Google Chrome\";v=\"126\"";

    private readonly Func<string?> _proxy;
    private readonly object _gate = new();
    private readonly CookieContainer _cookies = new();

    /// <summary>One request at a time per host, so two searches in a row do not arrive as a burst.</summary>
    private readonly HostGate _hosts = new();

    private HttpClient? _client;
    private string? _builtFor;

    static WebTransport()
    {
        // GB18030 lives in the code-pages provider; the runtime does not register it by itself.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <param name="proxy">
    /// Read on every request rather than fixed at construction: the setting is live, and a proxy edit must not
    /// restart the consumer. A change rebuilds the client lazily, here.
    /// </param>
    public WebTransport(Func<string?> proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        _proxy = proxy;
    }

    /// <summary>
    /// Sends one request, following up to <see cref="MaxRedirects"/> redirects and checking the address at every
    /// hop. Never throws for a network fault: the failure comes back as <see cref="WebResponse.Error"/> with the
    /// prefix the job engine's classification already understands.
    /// </summary>
    public async Task<WebResponse> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var current = request;
        var owns = false;
        string? previousHost = null;

        try
        {
            for (var hop = 0; ; hop++)
            {
                if (current.RequestUri is not { } uri)
                {
                    return Failure("invalid_args: 请求没有地址");
                }

                if (WebAddressGuard.Reject(uri) is { } structural)
                {
                    return Failure($"denied: {structural}");
                }

                // The proxy is the user's own service, often on loopback; only the target is judged.
                var proxy = Normalize(_proxy());
                if (proxy is null && await WebAddressGuard.ResolveAsync(uri, ct).ConfigureAwait(false) is var (allowed, error))
                {
                    if (error is not null)
                    {
                        return Failure(error.StartsWith("unreachable:", StringComparison.Ordinal) ? error : $"denied: {error}");
                    }

                    _ = allowed;
                }

                // Waited for before the request goes out, not before it is built: the gate is about what the
                // other end sees. A cancelled wait leaves nothing open, which is why it is outside the using.
                // A redirect that stays on the host just answered is the same navigation, not a second visitor: the gap
                // is for separate requests. Only the first hop, or a hop to a different host, is made to wait.
                if (!string.Equals(previousHost, uri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    await _hosts.WaitAsync(uri.Host, ct).ConfigureAwait(false);
                }

                previousHost = uri.Host;

                using var attempt = owns ? current : null;
                owns = true;

                HttpResponseMessage response;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(EngineTimeout);
                    response = await Client
                        .SendAsync(current, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Failure($"timeout after {EngineTimeout.TotalSeconds:0.#}s: 服务器没有在限时内响应");
                }
                catch (HttpRequestException failure)
                {
                    return Failure($"unreachable: {failure.Message}");
                }
                catch (OperationCanceledException)
                {
                    return Failure("cancelled: 请求被取消");
                }

                using (response)
                {
                    var status = (int)response.StatusCode;
                    if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                    {
                        if (hop >= MaxRedirects)
                        {
                            return Failure("unreachable: 重定向次数过多");
                        }

                        var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                        current = new HttpRequestMessage(HttpMethod.Get, next);

                        // The hop keeps the kind of the request that started the chain. A JSON endpoint that
                        // redirects must not start sending navigation headers halfway through.
                        var kind = request.Options.TryGetValue(KindKey, out var recorded)
                            ? recorded
                            : WebRequestKind.Document;
                        ApplyHeaders(current, AcceptLanguageFor(next), kind);
                        continue;
                    }

                    var read = await ReadAsync(response, ct).ConfigureAwait(false);
                    return read with { Status = status };
                }
            }
        }
        finally
        {
            if (owns)
            {
                request.Dispose();
            }
        }
    }

    private static WebResponse Failure(string error) => new(0, null, null, null, error);

    /// <summary>
    /// Reads the body up to the cap. A response longer than the cap is refused rather than cut: half a page
    /// reads as a complete page, and the model has no way to tell.
    /// </summary>
    private static async Task<WebResponse> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        var declared = response.Content.Headers.ContentLength;

        byte[] bytes;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            bytes = await ReadCappedAsync(stream, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            return Failure($"unreachable: 读响应失败（{error.Message}）");
        }
        catch (OperationCanceledException)
        {
            return Failure("cancelled: 读响应被取消");
        }

        if (bytes.Length > MaxBodyBytes)
        {
            return Failure($"too_large: 响应超过 {MaxBodyBytes / (1024 * 1024)} MB 上限（声明长度 {declared?.ToString() ?? "未知"}），不读");
        }

        var charset = CharsetOf(response.Content.Headers.ContentType, bytes);
        var body = Decode(bytes, charset);

        return new WebResponse(0, body, response.RequestMessage?.RequestUri, contentType, null) { Charset = charset };
    }

    /// <summary>Reads until the cap plus one byte, which is how "too large" is told apart from "exactly the cap".</summary>
    private static async Task<byte[]> ReadCappedAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var collected = new MemoryStream();

        while (collected.Length <= MaxBodyBytes)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            collected.Write(buffer, 0, read);
        }

        return collected.ToArray();
    }

    /// <summary>
    /// The charset the page is really in: the header says so if it says, otherwise a <c>&lt;meta charset&gt;</c>
    /// in the first two kilobytes, otherwise UTF-8 tried strictly and GB18030 as the fallback — the same order
    /// and the same reasoning as <c>FileReadTool</c>, because Chinese sites still serve GB18030 without saying so.
    /// </summary>
    private static string CharsetOf(MediaTypeHeaderValue? header, byte[] bytes)
    {
        if (!string.IsNullOrWhiteSpace(header?.CharSet))
        {
            return header.CharSet.Trim('"', ' ');
        }

        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2048)).ToLowerInvariant();
        var at = head.IndexOf("charset=", StringComparison.Ordinal);
        if (at >= 0)
        {
            var value = head[(at + "charset=".Length)..].TrimStart('"', '\'', ' ');
            var end = value.IndexOfAny(['"', '\'', ' ', ';', '>', '/']);
            var name = (end < 0 ? value : value[..end]).Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return "utf-8";
    }

    private static string Decode(byte[] bytes, string charset)
    {
        try
        {
            var encoding = Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var offset = HasBom(bytes, encoding) ? encoding.GetPreamble().Length : 0;
            return encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (Exception error) when (error is ArgumentException or DecoderFallbackException or NotSupportedException)
        {
            // An unknown name, or bytes that are not in the encoding the header claimed — which is common, and
            // which on a Chinese page almost always means GB18030.
            return Encoding.GetEncoding("gb18030").GetString(bytes);
        }
    }

    private static bool HasBom(byte[] bytes, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        return preamble.Length > 0 && bytes.Length >= preamble.Length && bytes.AsSpan(0, preamble.Length).SequenceEqual(preamble);
    }

    /// <summary>
    /// Headers for one request, chosen by what the request is rather than applied uniformly.
    /// <para>
    /// A browser identity is only worth wearing if it is consistent. The set a browser sends for a page it is
    /// navigating to — <c>Sec-Fetch-Mode: navigate</c>, <c>Dest: document</c>, <c>Upgrade-Insecure-Requests</c>,
    /// the client hints — differs from the set it sends for a fetch, and a Chrome user agent that carries neither
    /// is a more conspicuous thing than an honest client. The same is true the other way round: Wikipedia and
    /// arXiv both publish policies asking automated clients to identify themselves, so their APIs are asked by a
    /// named client instead of by a browser with its script markers missing.
    /// </para>
    /// <para>
    /// The <c>Accept</c> value is one header and never two: it is set once, here, from what the caller says it
    /// wants. An engine must not add its own media type on top of it.
    /// </para>
    /// </summary>
    /// <param name="request">The request to dress.</param>
    /// <param name="acceptLanguage">The languages to claim, from the query's script.</param>
    /// <param name="kind">A page being navigated to, or a data endpoint being called.</param>
    /// <param name="accept">The media type to ask for, or null for the default of the kind.</param>
    public static void ApplyHeaders(
        HttpRequestMessage request,
        string acceptLanguage,
        WebRequestKind kind = WebRequestKind.Document,
        string? accept = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (kind == WebRequestKind.Data)
        {
            // Identified rather than disguised. Every endpoint this is used for is an interface whose operator
            // wants to know who is calling; none of them is challenged by a well-behaved named client.
            request.Headers.TryAddWithoutValidation("User-Agent", IdentifiedUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", accept ?? "*/*");
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);

            // Recorded here as well: a redirect hop reads the kind back, and a missing one means "page", which would
            // send a browser's headers to an interface that asked to be called by name.
            request.Options.Set(KindKey, kind);
            return;
        }

        // A page a person is looking at. The same six headers Chrome sends for a top-level navigation, because a
        // subset of them is itself a fingerprint.
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation(
            "Accept",
            accept ?? "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        request.Headers.TryAddWithoutValidation("sec-ch-ua", BrowserBrands);
        request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");

        // Kept on the request so a redirect chain stays one kind of request from the first hop to the last.
        request.Options.Set(KindKey, kind);
    }

    /// <summary>Which languages to claim, by the script the query is written in.</summary>
    public static string AcceptLanguageFor(Uri uri) =>
        uri.Host.Contains("wikipedia.org", StringComparison.OrdinalIgnoreCase) && uri.Host.StartsWith("zh.", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN,zh;q=0.9,en;q=0.6"
            : "zh-CN,zh;q=0.9,en;q=0.8";

    private static string? Normalize(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy))
        {
            return null;
        }

        // Only the target's addresses are judged; a proxy on loopback is the normal case (Clash, v2ray) and must
        // not be refused by the guard written for the target.
        return Uri.TryCreate(proxy, UriKind.Absolute, out var uri) ? uri.ToString() : null;
    }

    private HttpClient Client
    {
        get
        {
            lock (_gate)
            {
                var proxy = Normalize(_proxy());
                if (_client is not null && _builtFor == proxy)
                {
                    return _client;
                }

                _client?.Dispose();
                _builtFor = proxy;
                _client = Build(proxy);
                return _client;
            }
        }
    }

    private HttpClient Build(string? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = _cookies,

            // Cookies live in this process only. A cookie jar on disk is a new piece of sensitive data, and the
            // capability is not worth one.
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        if (proxy is null)
        {
            // Pinning the connection to an address the guard already approved. Without this the handler would
            // resolve the name a second time, and a name that answers 93.184.216.34 to the check and 127.0.0.1
            // to the connect defeats the check entirely.
            handler.ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
                var allowed = addresses.Where(address => !WebAddressGuard.IsBlocked(address)).ToArray();
                if (allowed.Length == 0)
                {
                    throw new HttpRequestException($"'{context.DnsEndPoint.Host}' 解析到本机或内网地址，拒绝连接");
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(new IPEndPoint(allowed[0], context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            };
        }

        // Only the proxy the user typed, never one inherited from the environment: an HTTP_PROXY this code did
        // not choose would silently move the traffic somewhere the settings page does not show.
        handler.UseProxy = proxy is not null;
        if (proxy is not null)
        {
            handler.Proxy = new WebProxy(proxy);
        }

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,

            // Named rather than left to negotiation. Every host this talks to serves HTTP/2, a current browser
            // uses it for all of them, and an HTTP/1.1 request with a Chrome user agent is a pairing no browser
            // produces. The policy falls back to 1.1 on its own for a host that does not offer 2.
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
