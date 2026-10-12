using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Partner.Desktop.Perception;

/// <summary>
/// A page one Chrome DevTools Protocol endpoint is exposing.
/// </summary>
/// <param name="Title">The tab's title, used to pick between several pages on one port.</param>
/// <param name="Url">Where it is — often the only thing that identifies which window it belongs to.</param>
/// <param name="WebSocketDebuggerUrl">The socket to talk to.</param>
public sealed record CdpPage(string Title, string Url, string WebSocketDebuggerUrl);

/// <summary>One interactive element a page reported, in <b>viewport</b> coordinates.</summary>
/// <param name="Marker">
/// The attribute this element was stamped with. It is the only handle that survives between reading the page
/// and acting on it — an index would name whatever happens to be at that position after the page re-renders,
/// which is the same failure the UI Automation index has and this exists to avoid.
/// </param>
/// <param name="Name">What a person reads on it.</param>
/// <param name="Role">Its kind, mapped onto the same vocabulary the observation already uses.</param>
/// <param name="X">Left edge in CSS pixels from the viewport's origin.</param>
public sealed record CdpElementRecord(
    string Marker,
    string Name,
    string Role,
    double X,
    double Y,
    double Width,
    double Height,
    string? Value = null,
    bool Offscreen = false);

/// <summary>
/// The DOM of a page, as a set of controls the same shape as the ones UI Automation returns.
/// <para>
/// This exists for the applications UI Automation cannot see. A Chromium or Electron window exposes a handful of
/// elements to UIA — the webview's own document surface and little else — so "click 播放" is unreachable there
/// and the only route left is a coordinate the model read off a picture. The DOM is where those controls
/// actually are, named and addressable, and the protocol exposes them directly.
/// </para>
/// </summary>
public sealed class CdpDomSource
{
    /// <summary>
    /// The attribute stamped on each element this reads. Kept in the page's own namespace so it cannot collide
    /// with the application's, and removed from the reported shape — it is a handle, not part of the control.
    /// </summary>
    public const string MarkerAttribute = "data-partner-cdp";

    /// <summary>
    /// The walk, run inside the page.
    /// <para>
    /// Interactive elements are the union of the tags that are controls on their own and the ARIA roles that
    /// make anything else one — which is what a page means by "you can press this". Every returned element is
    /// stamped before it is reported, so the report and the later click address the same node rather than the
    /// same position.
    /// </para>
    /// <para>
    /// Viewport coordinates, not screen coordinates: a page has no idea where it is on the screen, and the
    /// caller knows. Offscreen elements are reported rather than dropped, for the same reason the UI Automation
    /// walk reports them — something that cannot be named can never be acted on.
    /// </para>
    /// </summary>
    internal const string ReadScript = """
        (() => {
          const attr = 'data-partner-cdp';
          const selector = [
            'a[href]', 'button', 'input:not([type=hidden])', 'select', 'textarea', 'summary',
            '[contenteditable="true"]', '[role]', '[tabindex]:not([tabindex="-1"])'
          ].join(',');

          const roleOf = (el) => {
            const explicit = (el.getAttribute('role') || '').toLowerCase();
            if (explicit) return explicit;
            const tag = el.tagName.toLowerCase();
            if (tag === 'a') return 'link';
            if (tag === 'button' || tag === 'summary') return 'button';
            if (tag === 'select') return 'combobox';
            if (tag === 'textarea' || el.isContentEditable) return 'textbox';
            if (tag === 'input') {
              const type = (el.getAttribute('type') || 'text').toLowerCase();
              if (type === 'checkbox') return 'checkbox';
              if (type === 'radio') return 'radio';
              if (type === 'button' || type === 'submit' || type === 'reset') return 'button';
              if (type === 'range') return 'slider';
              return 'textbox';
            }
            return 'generic';
          };

          // "generic" is what a div is to a browser and never what a person means by clickable.
          const worth = (el) => {
            const role = roleOf(el);
            if (role === 'generic') return false;
            const style = getComputedStyle(el);
            if (style.visibility === 'hidden' || style.display === 'none') return false;
            if (el.disabled || el.getAttribute('aria-disabled') === 'true') return false;
            return true;
          };

          const nameOf = (el) => (
            el.getAttribute('aria-label')
            || el.getAttribute('title')
            || el.getAttribute('placeholder')
            || (el.labels && el.labels[0] ? el.labels[0].innerText : '')
            || (el.innerText || '').trim()
            || (el.value || '').trim()
            || ''
          ).replace(/\s+/g, ' ').slice(0, 120);

          const found = [];
          const seen = new Set();
          for (const el of document.querySelectorAll(selector)) {
            if (seen.has(el)) continue;
            if (!worth(el)) continue;
            const rect = el.getBoundingClientRect();
            if (rect.width < 2 || rect.height < 2) continue;

            // Stamped before it is reported, so the id in the answer and the node clicked later are one node.
            el.setAttribute(attr, String(found.length));
            seen.add(el);

            found.push({
              marker: String(found.length),
              name: nameOf(el),
              role: roleOf(el),
              x: rect.left + window.scrollX,
              y: rect.top + window.scrollY,
              width: rect.width,
              height: rect.height,
              value: typeof el.value === 'string' ? el.value.slice(0, 200) : null,
              offscreen: rect.bottom < 0 || rect.right < 0
                || rect.top > window.innerHeight || rect.left > window.innerWidth
            });
          }

          return {
            devicePixelRatio: window.devicePixelRatio || 1,
            title: document.title || '',
            url: location.href,
            elements: found
          };
        })()
        """;

    /// <summary>
    /// Presses the element a marker names, through the page's own DOM.
    /// <para>
    /// <c>click()</c> rather than a synthetic mouse event at coordinates, for the same reason a UI Automation
    /// pattern beats a coordinate click: it reaches the element itself, so a window covering it, a control that
    /// moved since the observation, and the pointer being somewhere else entirely are all irrelevant. And a
    /// page that re-rendered has dropped the marker, which is reported rather than guessed at — clicking
    /// whatever now sits at that index is the failure this whole mechanism exists to avoid.
    /// </para>
    /// </summary>
    internal const string ActScript = """
        (() => {
          const el = document.querySelector('[data-partner-cdp="%MARKER%"]');
          if (!el) return { ok: false, detail: '这个元素已经不在页面上了（重新 desktop_observe）' };
          if (el.disabled || el.getAttribute('aria-disabled') === 'true') {
            return { ok: false, detail: '该元素当前不可用（disabled）' };
          }
          el.scrollIntoView({ block: 'center', inline: 'center' });
          if (typeof el.focus === 'function') el.focus();
          el.click();
          return { ok: true, detail: '已通过页面 DOM 直接点击（不需要坐标，也不需要控件在前台）' };
        })()
        """;

    /// <summary>
    /// Viewport coordinates to screen coordinates.
    /// <para>
    /// The page knows where it is inside the window; only the caller knows where the window is on the screen.
    /// The scale factor is not a detail: a viewport rectangle is in CSS pixels, and a display at 150% puts three
    /// physical pixels where the page drew one — so an unconverted point lands up to a third of the screen away,
    /// on a neighbouring control, with every step of the pipeline reporting success.
    /// </para>
    /// </summary>
    internal static PixelRect ToScreen(CdpElementRecord record, int clientX, int clientY, double devicePixelRatio)
    {
        var scale = devicePixelRatio <= 0 ? 1 : devicePixelRatio;

        return new PixelRect(
            clientX + (int)Math.Round(record.X * scale),
            clientY + (int)Math.Round(record.Y * scale),
            (int)Math.Round(record.Width * scale),
            (int)Math.Round(record.Height * scale));
    }

    /// <summary>
    /// A page role as the observation's vocabulary. Deliberately a smaller set than UI Automation's: what
    /// matters here is whether pressing it does something, and a name the model already knows from the other
    /// path is worth more than a precise one it has not seen.
    /// </summary>
    internal static string RoleOf(string pageRole) => pageRole.ToLowerInvariant() switch
    {
        "button" => "Button",
        "link" => "Hyperlink",
        "textbox" => "Edit",
        "combobox" => "ComboBox",
        "checkbox" => "CheckBox",
        "radio" => "RadioButton",
        "slider" => "Slider",
        "tab" => "TabItem",
        "menuitem" => "MenuItem",
        "option" or "listitem" => "ListItem",
        "row" or "gridcell" or "cell" => "DataItem",
        "treeitem" => "TreeItem",
        "switch" or "toggle" => "CheckBox",
        _ => "Button",
    };

    /// <summary>
    /// A page element as an observation element, in the same shape the UI Automation walk produces — so
    /// everything downstream (numbering, marks, the click point, revalidation) is unchanged by where the
    /// control came from.
    /// </summary>
    internal static UiElement ToElement(CdpElementRecord record, string windowId, int clientX, int clientY, double ratio)
    {
        var rect = ToScreen(record, clientX, clientY, ratio);
        var role = RoleOf(record.Role);

        return new UiElement(
            ElementId: $"{windowId}#cdp:{record.Marker}",
            WindowId: windowId,
            Name: record.Name,
            Role: role,
            Rect: rect,
            IdentityDigest: DesktopObservation.ElementIdentity(windowId, record.Name, role),
            Value: record.Value,
            IsOffscreen: record.Offscreen);
    }

    /// <summary>What one page reported: its geometry context and its elements.</summary>
    public sealed record CdpDomSnapshot(double DevicePixelRatio, string Title, string Url, IReadOnlyList<CdpElementRecord> Elements);

    /// <summary>
    /// Runs one expression in the page and returns its value as text.
    /// <para>
    /// Public because "what does the page say now" is a question with exactly one honest answer, and the
    /// alternative — inferring the effect of a click from a screenshot — is how a working click gets reported
    /// as one that changed nothing.
    /// </para>
    /// </summary>
    public Task<string?> EvaluateAsync(int port, string expression, string? windowTitle = null, CancellationToken ct = default) =>
        SendAsync(port, windowTitle, $"({expression})", ct);

    /// <summary>Live sockets, one per (port, page) pair. A port serves several tabs, and each target has
    /// its own <c>webSocketDebuggerUrl</c> — the socket keys on the page so two windows of one browser can
    /// each talk to the tab they are looking at instead of both talking to the first one.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Port, string Url), CdpConnection> _connections = new();

    /// <summary>How long one expression may take before the page is written off as not answering.</summary>
    internal static readonly TimeSpan EvaluateTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The endpoint a debuggable page is reached through.</summary>
    private readonly HttpClient _http;

    private readonly Func<int, string?, Task<CdpPage?>> _connect;

    /// <summary>
    /// Overridable so the page lookup and the walk can be exercised without a browser: the failure modes being
    /// pinned here are "no port", "no page on it" and "the page answered nothing", and all three need a real
    /// process to reproduce otherwise.
    /// </summary>
    public CdpDomSource(
        HttpClient? http = null,
        Func<int, string?, Task<CdpPage?>>? connect = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        _connect = connect ?? ConnectAsync;
    }

    /// <summary>
    /// Reads one window's page. Null when there is nothing to read: no debug port, no page on it, or a page
    /// that did not answer. Never throws — a browser that is not there is not an error, it is a window the
    /// other paths are for.
    /// </summary>
    public async Task<CdpDomSnapshot?> ReadAsync(int port, string? windowTitle = null, CancellationToken ct = default)
    {
        var page = await _connect(port, windowTitle).ConfigureAwait(false);
        if (page is null)
        {
            return null;
        }

        var result = await SendAsync(port, windowTitle, ReadScript, ct).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;

            var elements = new List<CdpElementRecord>();
            foreach (var item in root.GetProperty("elements").EnumerateArray())
            {
                elements.Add(new CdpElementRecord(
                    item.GetProperty("marker").GetString() ?? string.Empty,
                    item.GetProperty("name").GetString() ?? string.Empty,
                    item.GetProperty("role").GetString() ?? string.Empty,
                    item.GetProperty("x").GetDouble(),
                    item.GetProperty("y").GetDouble(),
                    item.GetProperty("width").GetDouble(),
                    item.GetProperty("height").GetDouble(),
                    item.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null,
                    item.TryGetProperty("offscreen", out var offscreen) && offscreen.GetBoolean()));
            }

            return new CdpDomSnapshot(
                root.TryGetProperty("devicePixelRatio", out var ratio) ? ratio.GetDouble() : 1,
                root.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                elements);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Presses a marked element. Null when the page could not be reached; a refusal when it could not press
    /// that element — which is reported rather than turned into a click at the coordinates it used to occupy.
    /// </summary>
    public async Task<UiaInvocation?> ActAsync(int port, string marker, string? windowTitle = null, CancellationToken ct = default)
    {
        var page = await _connect(port, windowTitle).ConfigureAwait(false);
        if (page is null)
        {
            return null;
        }

        var result = await SendAsync(port, windowTitle, ActScript.Replace("%MARKER%", marker, StringComparison.Ordinal), ct)
            .ConfigureAwait(false);

        if (result is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result);
            var ok = document.RootElement.GetProperty("ok").GetBoolean();
            var detail = document.RootElement.TryGetProperty("detail", out var value)
                ? value.GetString() ?? string.Empty
                : string.Empty;

            return new UiaInvocation(ok, "cdp.click", detail);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The first page on a debug port that matches what was asked for, falling back to the first page at all.
    /// Several tabs share one port, and without this rule they do not share it safely — a second window reads
    /// the first tab's DOM and presses the first tab's buttons, which is a wrong-tab click with no error
    /// anywhere. The window title is the only hint the window has: Chromium names the window after its page
    /// (often with a suffix like " - Google Chrome"), Electron after its app or page. The match is
    /// bidirectional containment, case-insensitive, on the two titles; when nothing matches (the suffix
    /// conventions differ too much) the first page answers, which is the behaviour this used to always have.
    /// </summary>
    private async Task<CdpPage?> ConnectAsync(int port, string? preferredTitle)
    {
        try
        {
            using var response = await _http
                .GetAsync($"http://127.0.0.1:{port}/json/list")
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            CdpPage? first = null;
            foreach (var target in document.RootElement.EnumerateArray())
            {
                if (target.TryGetProperty("type", out var type) && type.GetString() != "page")
                {
                    continue;
                }

                if (!target.TryGetProperty("webSocketDebuggerUrl", out var socket) || socket.GetString() is not { Length: > 0 } url)
                {
                    continue;
                }

                var page = new CdpPage(
                    target.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                    target.TryGetProperty("url", out var pageUrl) ? pageUrl.GetString() ?? string.Empty : string.Empty,
                    url);

                if (Matches(page.Title, preferredTitle))
                {
                    return page;
                }

                first ??= page;
            }

            return first;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether a page's title and the window's title name the same tab: either containing the
    /// other, so "音乐 - 网易云音乐" matches the page titled "音乐" and vice versa.</summary>
    internal static bool Matches(string pageTitle, string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle) || string.IsNullOrWhiteSpace(pageTitle))
        {
            return false;
        }

        var page = pageTitle.Trim();
        var window = windowTitle.Trim();
        if (window.Length == 0 || page.Length == 0)
        {
            return false;
        }

        return page.Contains(window, StringComparison.OrdinalIgnoreCase)
            || window.Contains(page, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One socket per debug port, held open between calls.
    /// <para>
    /// Not an optimisation — a requirement. A debug target accepts <b>one</b> client at a time, so reading the
    /// page and then acting on it over two sockets fails with "another client is already attached" the moment
    /// the first has not finished closing. That is precisely the read-then-press sequence this whole mechanism
    /// exists for, so the connection is kept and re-established only when the page goes away.
    /// </para>
    /// </summary>
    private async Task<CdpConnection?> ConnectionAsync(int port, string? preferredTitle, CancellationToken ct)
    {
        var page = await _connect(port, preferredTitle).ConfigureAwait(false);
        if (page is null)
        {
            return null;
        }

        var key = (port, page.WebSocketDebuggerUrl);
        if (_connections.TryGetValue(key, out var existing2) && existing2.IsOpen)
        {
            // The pick is checked against the hint each time: a tab that was retitled (or closed and
            // replaced) must not keep serving the stale socket — the hint is what the window answered
            // for, and a stale one names a different page with every coordinate wrong by a tab.
            if (Matches(page.Title, preferredTitle))
            {
                return existing2;
            }

            if (_connections.TryRemove(key, out var wrong))
            {
                wrong.Dispose();
            }
        }
        if (page is null)
        {
            return null;
        }

        try
        {
            var connection = new CdpConnection(page.WebSocketDebuggerUrl);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await connection.OpenAsync(timeout.Token).ConfigureAwait(false);
            _connections[key] = connection;
            return connection;
        }
        catch (Exception error) when (
            error is WebSocketException or OperationCanceledException or UriFormatException
                or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>One expression, one answer, over the connection held for the page the window names.</summary>
    private async Task<string?> SendAsync(int port, string? preferredTitle, string expression, CancellationToken ct)
    {
        try
        {
            var connection = await ConnectionAsync(port, preferredTitle, ct).ConfigureAwait(false);
            if (connection is null)
            {
                return null;
            }

            // A page that never answers must not hold the turn: an observation with no budget is a turn that
            // never comes back, and the caller cannot tell that apart from a slow one.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(EvaluateTimeout);

            return await connection.EvaluateAsync(expression, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is WebSocketException or OperationCanceledException or JsonException or UriFormatException
                or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>A debugger socket and its request counter.</summary>
    private sealed class CdpConnection(string url) : IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _nextId;

        public bool IsOpen => _socket.State == WebSocketState.Open;

        /// <summary>
        /// Connects and switches the protocol on.
        /// <para>
        /// <c>Runtime.enable</c> is not optional, and leaving it out is not a smaller request: without it the
        /// protocol has no record of the page's execution contexts, and <c>Runtime.evaluate</c> without an
        /// explicit one is answered against whichever context is the default — a document with nothing in it, so
        /// every lookup finds no elements and every click finds no node. The first call can even succeed, which
        /// makes it look intermittent.
        /// </para>
        /// </summary>
        public async Task OpenAsync(CancellationToken ct)
        {
            await _socket.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);

            var enable = JsonSerializer.Serialize(new { id = 0, method = "Runtime.enable" });
            await _socket
                .SendAsync(Encoding.UTF8.GetBytes(enable), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);

            // The reply carries the context list, which nothing here asks for, but waiting for it is the point:
            // evaluating before it arrives is the race described above.
            var buffer = new byte[64 * 1024];
            var message = new StringBuilder();

            for (var received = 0; received < 64; received++)
            {
                var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException("the debug target closed the connection on enable");
                }

                message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage && IsReplyTo(message.ToString(), 0))
                {
                    return;
                }

                message.Clear();
            }
        }

        public async Task<string?> EvaluateAsync(string expression, CancellationToken ct)
        {
            // One request at a time: replies are matched by id, and two in flight would leave this connection
            // holding an answer that belongs to the other request.
            await _gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                var id = ++_nextId;
                var request = JsonSerializer.Serialize(new
                {
                    id,
                    method = "Runtime.evaluate",
                    @params = new { expression, returnByValue = true, awaitPromise = true },
                });

                await _socket
                    .SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, ct)
                    .ConfigureAwait(false);

                var buffer = new byte[64 * 1024];
                var message = new StringBuilder();

                // A websocket *message* is one protocol message, and that is the framing that matters here: two
                // replies read into one string are not parseable as a single document, so a buffer that kept
                // accumulating them would fail every read after the first — which is exactly a walk followed by
                // a press, the sequence this whole mechanism exists for.
                for (var received = 0; received < 64; received++)
                {
                    var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }

                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    // The browser pushes its own events on this socket, so a message answering a different id
                    // is stepped over rather than returned as this one's answer.
                    if (TryTake(message.ToString(), id) is { } ready)
                    {
                        return ready;
                    }

                    message.Clear();
                }

                return null;
            }
            catch (WebSocketException)
            {
                // The page navigated or the browser closed. The next call reconnects; this one reports nothing,
                // which the caller reads as "no controls" — the same as any other unreachable page.
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>The reply to <paramref name="id"/>, if this message is one.</summary>
        private static string? TryTake(string message, int id) =>
            IsReplyTo(message, id) ? Extract(message) : null;

        /// <summary>
        /// Whether this message answers <paramref name="id"/>.
        /// <para>
        /// Kept apart from reading the answer because not every reply carries one: the handshake's
        /// <c>Runtime.enable</c> acknowledgement is a message with nothing in it, and asking it for a value
        /// would fail on a reply that arrived perfectly.
        /// </para>
        /// </summary>
        private static bool IsReplyTo(string message, int id)
        {
            try
            {
                using var document = JsonDocument.Parse(message);
                return document.RootElement.TryGetProperty("id", out var reply)
                    && reply.ValueKind == JsonValueKind.Number
                    && reply.GetInt32() == id;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        public void Dispose()

        {
            _socket.Dispose();
            _gate.Dispose();
        }
    }
    /// <summary>
    /// The value out of a <c>Runtime.evaluate</c> response, as text.
    /// <para>
    /// A string comes back as itself; anything structured comes back as its own JSON, which the caller that
    /// asked for a shape then parses. Strings are deliberately not re-encoded: a page that answers "已点击"
    /// has to arrive as those characters, not as a quoted string that never compares equal to them.
    /// </para>
    /// </summary>
    private static string? Extract(string received)
    {
        try
        {
            using var document = JsonDocument.Parse(received);
            var result = document.RootElement.GetProperty("result");

            if (result.TryGetProperty("exceptionDetails", out _))
            {
                return null;
            }

            var payload = result.GetProperty("result");

            // An exception in the page, an unserialisable value, a timeout: each of these arrives as a result
            // with no value in it, and asking for one anyway throws out of a read that should simply have had
            // nothing to report. The caller reads all of them the same way — nothing came back.
            if (!payload.TryGetProperty("value", out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}