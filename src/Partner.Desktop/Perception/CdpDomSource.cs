using System.Collections.Concurrent;
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
/// This exists for applications UI Automation cannot see. A Chromium or Electron window exposes almost nothing
/// to UIA, so its controls are unreachable except by a coordinate read off a picture. The DOM is where those
/// controls actually are, named and addressable, and the protocol exposes them directly.
/// </para>
/// </summary>
public sealed class CdpDomSource
{
    /// <summary>
    /// The attribute stamped on each element this reads. In the page's own namespace so it cannot collide with
    /// the application's, and removed from the reported shape: it is a handle, not part of the control.
    /// </summary>
    public const string MarkerAttribute = "data-partner-cdp";

    /// <summary>
    /// The walk, run inside the page. Elements are stamped before being reported, so the report and the later
    /// click address the same node rather than the same position. Coordinates are viewport-relative; offscreen
    /// elements are reported rather than dropped, since something that cannot be named can never be acted on.
    /// </summary>
    internal const string ReadScript = """
        (() => {
          const attr = 'data-partner-cdp';
          const selector = [
            'a[href]', 'button', 'input:not([type=hidden])', 'select', 'textarea', 'summary',
            '[contenteditable="true"]', '[role]', '[tabindex]:not([tabindex="-1"])'
          ].join(',');

          // The places controls can live: the document, every open shadow root in it, and every same-origin
          // frame, each with the offset that turns its own coordinates into the top viewport's. A cross-origin
          // frame refuses to be read from here and is skipped, which is all a script can honestly do about it.
          const contexts = [];
          const visit = (root, win, ox, oy, depth) => {
            if (depth > 5 || contexts.length >= 60) return;
            contexts.push({ root, win, ox, oy });
            let all;
            try { all = root.querySelectorAll('*'); } catch (e) { return; }
            for (const el of all) {
              if (el.shadowRoot) visit(el.shadowRoot, win, ox, oy, depth + 1);
              if (el.tagName === 'IFRAME' || el.tagName === 'FRAME') {
                let doc = null;
                try { doc = el.contentDocument; } catch (e) { doc = null; }
                if (doc && doc.documentElement) {
                  const r = el.getBoundingClientRect();
                  const cs = win.getComputedStyle(el);
                  visit(
                    doc, el.contentWindow,
                    ox + r.left + (parseFloat(cs.borderLeftWidth) || 0),
                    oy + r.top + (parseFloat(cs.borderTopWidth) || 0),
                    depth + 1);
                }
              }
            }
          };
          visit(document, window, 0, 0, 0);

          // Stamps from an earlier read are removed first: markers are numbered from zero each time, and a stale
          // one left on an element that is no longer reported would answer a lookup for the new element's number.
          for (const c of contexts) {
            try { for (const old of c.root.querySelectorAll('[' + attr + ']')) old.removeAttribute(attr); } catch (e) {}
          }

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
          const worth = (el, win) => {
            const role = roleOf(el);
            if (role === 'generic') return false;
            const style = win.getComputedStyle(el);
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
          for (const c of contexts) {
            let list;
            try { list = c.root.querySelectorAll(selector); } catch (e) { continue; }
            for (const el of list) {
              if (seen.has(el)) continue;
              if (!worth(el, c.win)) continue;
              const rect = el.getBoundingClientRect();
              if (rect.width < 2 || rect.height < 2) continue;

              // Stamped before it is reported, so the id in the answer and the node clicked later are one node.
              el.setAttribute(attr, String(found.length));
              seen.add(el);

              const left = c.ox + rect.left;
              const top = c.oy + rect.top;
              found.push({
                marker: String(found.length),
                name: nameOf(el),
                role: roleOf(el),
                x: left + window.scrollX,
                y: top + window.scrollY,
                width: rect.width,
                height: rect.height,
                value: typeof el.value === 'string' ? el.value.slice(0, 200) : null,
                offscreen: top + rect.height < 0 || left + rect.width < 0
                  || top > window.innerHeight || left > window.innerWidth
              });
            }
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
    /// Presses the element a marker names, through the page's own DOM. <c>click()</c> rather than a synthetic
    /// mouse event at coordinates: it reaches the element itself, so occlusion, movement since the observation
    /// and pointer position are irrelevant. A page that re-rendered has dropped the marker, which is reported
    /// rather than guessed at.
    /// </summary>
    internal const string ActScript = """
        (() => {
          const attr = 'data-partner-cdp';
          const marker = '%MARKER%';

          // The marker may sit in a shadow root or a same-origin frame, where document.querySelector cannot see.
          const find = (root, depth) => {
            if (depth > 5) return null;
            let hit = null;
            try { hit = root.querySelector('[' + attr + '="' + marker + '"]'); } catch (e) {}
            if (hit) return hit;
            let all;
            try { all = root.querySelectorAll('*'); } catch (e) { return null; }
            for (const el of all) {
              if (el.shadowRoot) {
                hit = find(el.shadowRoot, depth + 1);
                if (hit) return hit;
              }
              if (el.tagName === 'IFRAME' || el.tagName === 'FRAME') {
                let doc = null;
                try { doc = el.contentDocument; } catch (e) { doc = null; }
                if (doc) {
                  hit = find(doc, depth + 1);
                  if (hit) return hit;
                }
              }
            }
            return null;
          };

          const el = find(document, 0);
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
    /// Viewport coordinates to screen coordinates. The page knows where it is inside the window; only the
    /// caller knows where the window is. The scale factor matters: viewport rectangles are in CSS pixels, and
    /// at 150% DPI three physical pixels cover one CSS pixel, so an unconverted point lands on a neighbouring
    /// control while every step reports success.
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
    /// A page role as the observation's vocabulary. Deliberately smaller than UI Automation's: what matters is
    /// whether pressing it does something, and a familiar name beats a precise one.
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
    /// A page element as an observation element, in the same shape the UI Automation walk produces, so
    /// everything downstream is unchanged by where the control came from.
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
    /// Runs one expression in the page and returns its value as text. Public because inferring the effect of a
    /// click from a screenshot can report a working click as one that changed nothing.
    /// </summary>
    public Task<string?> EvaluateAsync(int port, string expression, string? windowTitle = null, CancellationToken ct = default) =>
        SendAsync(port, windowTitle, $"({expression})", ct);

    /// <summary>Live sockets, one per (port, page) pair. A port serves several tabs, and each target has its own
    /// <c>webSocketDebuggerUrl</c>; keying on the page keeps two windows of one browser on their own tabs.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Port, string Url), CdpConnection> _connections = new();

    /// <summary>How long one expression may take before the page is written off as not answering.</summary>
    internal static readonly TimeSpan EvaluateTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The endpoint a debuggable page is reached through.</summary>
    private readonly HttpClient _http;

    private readonly Func<int, string?, Task<CdpPage?>> _connect;

    /// <summary>
    /// Overridable so the page lookup and the walk can be exercised without a browser.
    /// </summary>
    public CdpDomSource(
        HttpClient? http = null,
        Func<int, string?, Task<CdpPage?>>? connect = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        _connect = connect ?? ConnectCachedAsync;
    }

    /// <summary>How long the page list of a port is trusted. One action asks for it three or four times.</summary>
    internal static readonly TimeSpan PageListTtl = TimeSpan.FromSeconds(1.5);

    private readonly ConcurrentDictionary<(int Port, string Title), (CdpPage Page, long At)> _pageCache = new();

    /// <summary>
    /// <see cref="ConnectAsync"/> behind a short cache: reading a page, acting on it and checking the result
    /// each ask the browser for its tab list again. Only a found page is cached; a missing one is retried at
    /// once, because "not up yet" is the case where waiting costs the most.
    /// </summary>
    internal async Task<CdpPage?> ConnectCachedAsync(int port, string? preferredTitle)
    {
        var key = (port, preferredTitle ?? string.Empty);
        if (_pageCache.TryGetValue(key, out var cached)
            && Environment.TickCount64 - cached.At < (long)PageListTtl.TotalMilliseconds)
        {
            return cached.Page;
        }

        var page = await ConnectAsync(port, preferredTitle).ConfigureAwait(false);
        if (page is not null)
        {
            _pageCache[key] = (page, Environment.TickCount64);
        }
        else
        {
            _pageCache.TryRemove(key, out _);
        }

        return page;
    }

    /// <summary>A page that stopped answering may have been closed or replaced; ask the browser again next time.</summary>
    private void ForgetPages(int port)
    {
        foreach (var key in _pageCache.Keys.Where(key => key.Port == port).ToList())
        {
            _pageCache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Reads one window's page. Null when there is nothing to read: no debug port, no page on it, or a page
    /// that did not answer. Never throws; a browser that is not there is a window the other paths are for.
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
    /// Presses a marked element. Null when the page could not be reached; a refusal when it could not press that
    /// element, reported rather than turned into a click at the coordinates it used to occupy.
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

    /// <summary>What the page says about its own viewport — the numbers a screen point is converted with.</summary>
    public sealed record CdpViewport(double InnerWidth, double InnerHeight, double DevicePixelRatio);

    /// <summary>How long one batch of input commands may take. A drag is the longest and still well inside it.</summary>
    internal static readonly TimeSpan InputTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The page's viewport size and scale, or null when the page cannot be reached. Asked at the moment of the
    /// action, not remembered from the observation: a window resized in between would otherwise convert every
    /// screen point with the old size.
    /// </summary>
    public async Task<CdpViewport?> ViewportAsync(int port, string? windowTitle = null, CancellationToken ct = default)
    {
        var reply = await EvaluateAsync(
            port,
            "{ w: window.innerWidth, h: window.innerHeight, dpr: window.devicePixelRatio || 1 }",
            windowTitle,
            ct).ConfigureAwait(false);

        if (reply is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(reply);
            var root = document.RootElement;
            return new CdpViewport(root.GetProperty("w").GetDouble(), root.GetProperty("h").GetDouble(), root.GetProperty("dpr").GetDouble());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends input commands to the page the window names. Null when the page cannot be reached, so the caller
    /// can tell "this channel is not available" from "the page refused".
    /// </summary>
    internal async Task<UiaInvocation?> SendInputAsync(
        int port,
        string? windowTitle,
        IReadOnlyList<CdpCommand> commands,
        string method,
        string okDetail,
        CancellationToken ct)
    {
        try
        {
            var connection = await ConnectionAsync(port, windowTitle, ct).ConfigureAwait(false);
            if (connection is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InputTimeout);

            var outcome = await connection.RunAsync(commands, timeout.Token).ConfigureAwait(false);

            return outcome switch
            {
                null => null,
                { Length: 0 } => new UiaInvocation(true, method, okDetail),
                _ => new UiaInvocation(false, method, $"页面调试协议拒绝了命令（{outcome}）"),
            };
        }
        catch (Exception error) when (
            error is WebSocketException or OperationCanceledException or JsonException or UriFormatException
                or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    private const string Quiet = "（后台：页面调试协议，不需要前台，也不动用户的指针）";

    /// <summary>
    /// Tells the page to behave as the focused one without touching the window's real focus: key input reaches a
    /// page that believes it has the focus.
    /// </summary>
    public Task<UiaInvocation?> FocusAsync(int port, string? windowTitle, CancellationToken ct = default) =>
        SendInputAsync(port, windowTitle, Array.Empty<CdpCommand>(), "cdp.focus", $"页面已设为聚焦{Quiet}", ct);

    /// <summary>Clicks at a viewport point (CSS pixels): once, or twice for a double click.</summary>
    public Task<UiaInvocation?> ClickAsync(int port, string? windowTitle, double x, double y, bool right, int count, CancellationToken ct = default) =>
        SendInputAsync(port, windowTitle, CdpInput.ClickCommands(x, y, right, count), "cdp.click-at", $"已点击{Quiet}", ct);

    /// <summary>Moves the page's pointer to a viewport point, so hover styles and tooltips happen.</summary>
    public Task<UiaInvocation?> MoveAsync(int port, string? windowTitle, double x, double y, CancellationToken ct = default) =>
        SendInputAsync(port, windowTitle, CdpInput.MoveCommands(x, y), "cdp.move", $"已移动页面指针{Quiet}", ct);

    /// <summary>Turns the wheel at a viewport point; positive notches scroll up.</summary>
    public Task<UiaInvocation?> WheelAsync(int port, string? windowTitle, double x, double y, int notches, CancellationToken ct = default) =>
        SendInputAsync(port, windowTitle, CdpInput.WheelCommands(x, y, notches), "cdp.wheel", $"已滚动{Quiet}", ct);

    /// <summary>Drags between two viewport points.</summary>
    public Task<UiaInvocation?> DragAsync(int port, string? windowTitle, double fromX, double fromY, double toX, double toY, CancellationToken ct = default) =>
        SendInputAsync(port, windowTitle, CdpInput.DragCommands(fromX, fromY, toX, toY), "cdp.drag", $"已拖拽{Quiet}", ct);

    /// <summary>
    /// Presses a key, with modifiers held around it. A key the protocol has no name for is refused here with
    /// the key's code, not sent as something else.
    /// </summary>
    public Task<UiaInvocation?> PressKeyAsync(int port, string? windowTitle, ushort virtualKey, IReadOnlyList<ushort> modifiers, CancellationToken ct = default)
    {
        var commands = CdpInput.KeyCommands(virtualKey, modifiers);
        return commands is null
            ? Task.FromResult<UiaInvocation?>(new UiaInvocation(false, "cdp.key", $"页面调试协议没有对应虚拟键 {virtualKey} 的键名：不发，免得发成别的键"))
            : SendInputAsync(port, windowTitle, commands, "cdp.key", $"已按键{Quiet}", ct);
    }

    /// <summary>
    /// The page of the tab a window shows, rendered by the browser. Works for a window behind others, minimized,
    /// or whose content the window system cannot capture (a GPU-composited surface prints as flat grey). Null
    /// when there is no page, no port, or the browser declined.
    /// </summary>
    public async Task<byte[]?> ScreenshotAsync(int port, string? windowTitle = null, CancellationToken ct = default)
    {
        try
        {
            var connection = await ConnectionAsync(port, windowTitle, ct).ConfigureAwait(false);
            if (connection is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var png = await connection.CaptureScreenshotAsync(timeout.Token).ConfigureAwait(false);
            if (png is null)
            {
                ForgetPages(port);
            }

            return png;
        }
        catch (Exception error) when (
            error is WebSocketException or OperationCanceledException or UriFormatException
                or InvalidOperationException or ObjectDisposedException)
        {
            ForgetPages(port);
            return null;
        }
    }

    /// <summary>Types text into whatever the page has focused.</summary>
    public Task<UiaInvocation?> TypeAsync(int port, string? windowTitle, string text, CancellationToken ct = default) =>
        SendInputAsync(port, windowTitle, CdpInput.TextCommands(text), "cdp.type", $"已输入 {text.Length} 个字符{Quiet}", ct);

    /// <summary>
    /// The first page on a debug port that matches what was asked for, falling back to the first page at all.
    /// Several tabs share one port, and without this rule a second window would read the first tab's DOM and
    /// press the first tab's buttons. The match is bidirectional, case-insensitive containment on the two
    /// titles; Chromium names the window after its page (often with a suffix), Electron after its app or page.
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
    /// One socket per debug port, held open between calls. Not an optimisation but a requirement: a debug target
    /// accepts one client at a time, so reading the page and then acting on it over two sockets fails with
    /// "another client is already attached" while the first is still closing. The connection is re-established
    /// only when the page goes away.
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
            // The pick is checked against the hint each time: a tab that was retitled or replaced must not keep
            // serving the stale socket, whose coordinates are all wrong by a tab.
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

            // A page that never answers must not hold the turn: the caller cannot tell a hang from slowness.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(EvaluateTimeout);

            var answer = await connection.EvaluateAsync(expression, timeout.Token).ConfigureAwait(false);
            if (answer is null)
            {
                ForgetPages(port);
            }

            return answer;
        }
        catch (Exception error) when (
            error is WebSocketException or OperationCanceledException or JsonException or UriFormatException
                or InvalidOperationException or ObjectDisposedException)
        {
            ForgetPages(port);
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
        /// Connects and switches the protocol on. <c>Runtime.enable</c> is required: without it the protocol has
        /// no record of the page's execution contexts, and <c>Runtime.evaluate</c> is answered against a default
        /// empty document, so every lookup finds nothing. The first call can even succeed, which makes it look
        /// intermittent.
        /// </summary>
        public async Task OpenAsync(CancellationToken ct)
        {
            await _socket.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);

            var enable = JsonSerializer.Serialize(new { id = 0, method = "Runtime.enable" });
            await _socket
                .SendAsync(Encoding.UTF8.GetBytes(enable), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);

            // The reply carries the context list, which nothing here asks for, but waiting for it is the point:
            // evaluating before it arrives hits the race described above.
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
                var reply = await RoundTripAsync(
                    "Runtime.evaluate",
                    new { expression, returnByValue = true, awaitPromise = true },
                    ct).ConfigureAwait(false);

                return reply is null ? null : Extract(reply);
            }
            catch (WebSocketException)
            {
                // The page navigated or the browser closed. The next call reconnects; this one reports nothing,
                // which the caller reads as "no controls", the same as any other unreachable page.
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>The page as the browser renders it, as PNG bytes, or null when the browser would not.</summary>
        public async Task<byte[]?> CaptureScreenshotAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                var reply = await RoundTripAsync(
                    "Page.captureScreenshot",
                    new { format = "png", fromSurface = true, captureBeyondViewport = false },
                    ct).ConfigureAwait(false);

                if (reply is null || ErrorOf(reply) is not null)
                {
                    return null;
                }

                using var document = JsonDocument.Parse(reply);
                return document.RootElement.TryGetProperty("result", out var result)
                    && result.TryGetProperty("data", out var data)
                    && data.GetString() is { Length: > 0 } base64
                        ? Convert.FromBase64String(base64)
                        : null;
            }
            catch (Exception error) when (error is WebSocketException or JsonException or FormatException)
            {
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Whether the page has been told to behave as the focused one. Per connection: it dies with the socket.</summary>
        private bool _focusEmulated;

        /// <summary>
        /// Sends a sequence of commands in order, holding the connection for the whole of it so another caller's
        /// request cannot land between a press and its release. The page is first told to behave as the focused
        /// one (<c>Emulation.setFocusEmulationEnabled</c>): a background window is not focused as far as the
        /// browser is concerned, and an unfocused page ignores key input and drops <c>:focus</c> styling. This
        /// changes what the page believes, not what the user sees.
        /// </summary>
        /// <returns>Null when the connection died; otherwise the first protocol error, or an empty string for success.</returns>
        public async Task<string?> RunAsync(IReadOnlyList<CdpCommand> commands, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                if (!_focusEmulated)
                {
                    var enabled = await RoundTripAsync(
                        "Emulation.setFocusEmulationEnabled", new { enabled = true }, ct).ConfigureAwait(false);
                    if (enabled is null)
                    {
                        return null;
                    }

                    _focusEmulated = ErrorOf(enabled) is null;
                }

                foreach (var command in commands)
                {
                    var reply = await RoundTripAsync(command.Method, command.Params, ct).ConfigureAwait(false);
                    if (reply is null)
                    {
                        return null;
                    }

                    if (ErrorOf(reply) is { } error)
                    {
                        return $"{command.Method}: {error}";
                    }

                    if (command.DelayMs > 0)
                    {
                        await Task.Delay(command.DelayMs, ct).ConfigureAwait(false);
                    }
                }

                return string.Empty;
            }
            catch (WebSocketException)
            {
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// One request and the message that answers it. The gate is the caller's: this assumes it is the only
        /// request in flight.
        /// </summary>
        private async Task<string?> RoundTripAsync(string method, object parameters, CancellationToken ct)
        {
            var id = ++_nextId;
            var request = JsonSerializer.Serialize(new { id, method, @params = parameters });

            await _socket
                .SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);

            var buffer = new byte[256 * 1024];
            var message = new StringBuilder();

            // A websocket message is one protocol message, and that framing matters here: two replies read into
            // one string are not parseable as a single document, so every read after the first would fail. A
            // screenshot is megabytes of base64 in one message, hence the generous frame bound.
            for (var received = 0; received < 4096; received++)
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

                // The browser pushes its own events on this socket, so a message answering a different id is
                // stepped over.
                if (IsReplyTo(message.ToString(), id))
                {
                    return message.ToString();
                }

                message.Clear();
            }

            return null;
        }

        /// <summary>The protocol error a reply carries, or null when it succeeded.</summary>
        private static string? ErrorOf(string reply)
        {
            try
            {
                using var document = JsonDocument.Parse(reply);
                if (!document.RootElement.TryGetProperty("error", out var error))
                {
                    return null;
                }

                return error.TryGetProperty("message", out var text) ? text.GetString() ?? "error" : "error";
            }
            catch (JsonException)
            {
                return "unreadable reply";
            }
        }

        /// <summary>
        /// Whether this message answers <paramref name="id"/>. Kept apart from reading the answer because not
        /// every reply carries one: the <c>Runtime.enable</c> acknowledgement has no value, and asking it for one
        /// would fail on a reply that arrived perfectly.
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
    /// The value out of a <c>Runtime.evaluate</c> response, as text. A string comes back as itself; anything
    /// structured comes back as its own JSON. Strings are not re-encoded: a page that answers "已点击" must
    /// arrive as those characters, not as a quoted string that never compares equal.
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

            // An exception in the page, an unserialisable value, a timeout: each arrives as a result with no
            // value, and asking for one anyway would throw out of a read that should report nothing.
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