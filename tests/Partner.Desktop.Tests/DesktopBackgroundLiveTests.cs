using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Partner.Desktop;
using Partner.Desktop.Capture;
using Partner.Desktop.Input;
using Partner.Desktop.Interop;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;
using Partner.Desktop.Windows;
using Xunit;

namespace Partner.Desktop.Tests;

/// <summary>
/// The whole background path against a real browser: launched without taking the foreground, then clicked,
/// typed into, keyed, scrolled and restored — while the user's foreground window and pointer are checked to be
/// exactly where they were.
/// <para>
/// The arithmetic and the wire format are pinned elsewhere. What only a live window can answer is whether the
/// claims hold: that a window started with "show, don't activate" really leaves the foreground alone, that a
/// screen coordinate converted by the page-geometry rule lands on the pixel it names (the target's position is
/// measured from a picture of the window, not from the rule being tested), and that none of it moves the pointer.
/// </para>
/// <para>
/// Opt-in on <c>PARTNER_DESKTOP_LIVE=1</c> — it starts a browser, which will sit behind the user's windows for a
/// few seconds. It never sends input to the screen, so unlike the other live tests it does not move the cursor.
/// </para>
/// </summary>
public sealed class DesktopBackgroundLiveTests
{
    private sealed class LiveDesktopFactAttribute : FactAttribute
    {
        public LiveDesktopFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARTNER_DESKTOP_LIVE") != "1")
            {
                Skip = "live desktop test: set PARTNER_DESKTOP_LIVE=1 to run (it starts a browser behind your windows)";
            }
        }
    }

    private const string PageTitle = "partner bg live";

    private static string? BrowserPath() => new[]
    {
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    }.FirstOrDefault(File.Exists);

    [LiveDesktopFact]
    public async Task A_browser_is_launched_and_driven_in_the_background_without_touching_the_foreground_or_the_pointer()
    {
        var browser = BrowserPath();
        Assert.True(browser is not null, "neither Chrome nor Edge is installed on this machine");

        // Per-monitor awareness is what the server process declares first thing; without it the window's pixels
        // and the page's scale are in different spaces and the conversion under test would be measuring that.
        DpiAwareness.EnsurePerMonitorV2();

        var port = CdpRegistry.FreePort();
        var profile = Path.Combine(Path.GetTempPath(), "partner-bg-live-" + Guid.NewGuid().ToString("N")[..8]);
        var page = Path.Combine(Path.GetTempPath(), "partner-bg-live.html");

        // A big red button is the ground truth: its position is read back from a picture of the window, so the
        // click is aimed by what is on the screen and not by the rule that converts coordinates.
        await File.WriteAllTextAsync(page, $$"""
            <!doctype html><meta charset="utf-8"><title>{{PageTitle}}</title>
            <body style="margin:0;font:16px sans-serif;background:#fff">
              <button id="press" style="display:block;margin:60px 0 0 80px;width:240px;height:120px;background:#ff0000;border:0">press</button>
              <input id="field" style="display:block;margin:40px 0 0 80px;padding:8px;width:240px">
              <div id="log" style="margin:20px 80px">未点击</div>
              <div style="height:4000px"></div>
              <script>
                document.getElementById('press').addEventListener('click', (event) => {
                  window.__click = { x: event.clientX, y: event.clientY };
                  document.getElementById('log').textContent = '已点击';
                });
              </script>
            </body>
            """);

        var ports = new CdpRegistry();
        var launch = new DesktopLaunchTool(startMenuRoots: null, ports: ports, inputMode: () => InputMode.Background);
        using var automation = new DesktopAutomation(
            cdp: new CdpDomSource(),
            cdpPorts: ports,
            desktopLaunch: launch,
            inputMode: () => InputMode.Background);

        var foregroundBefore = NativeMethods.GetForegroundWindow();
        var processId = 0;

        try
        {
            // ---- launch ------------------------------------------------------------------------------
            var arguments = $"--remote-debugging-port={port} --user-data-dir=\"{profile}\" --no-first-run "
                + $"--no-default-browser-check \"{page}\"";

            var launched = await launch.InvokeAsync(
                JsonSerializer.Serialize(new { target = browser, arguments }),
                CancellationToken.None);

            Assert.True(launched.Ok, launched.Content);
            Assert.Contains("后台启动", launched.Content, StringComparison.Ordinal);
            processId = int.Parse(Regex.Match(launched.Content, @"pid (\d+)").Groups[1].Value);

            var source = new CdpDomSource();
            var snapshot = await WaitForAsync(async () => await source.ReadAsync(port, null, CancellationToken.None) is { Elements.Count: > 0 } s ? s : null);
            Assert.NotNull(snapshot);

            var window = await WaitForAsync(() => Task.FromResult(WindowEnumerator.List().FirstOrDefault(w => w.Title.Contains(PageTitle, StringComparison.Ordinal))));
            Assert.NotNull(window);

            Assert.False(window!.IsForeground, "the browser took the foreground");
            AssertForeground(foregroundBefore, "after the launch", window.Handle);

            var entityId = $"{window.Handle:x}";

            async Task<DesktopAction> ActAsync(ActionKind kind, ActionParams parameters)
            {
                // Observed again before each action: the lease is short, and the point of re-observing is the
                // same as in the tool — act on what is there now.
                automation.Observe(elementsFor: entityId);

                // The foreground is checked around the action itself, not across the whole test: the test runs for
                // seconds on a machine somebody is using, and their own windows would fail an assertion about
                // the agent's.
                var foreground = NativeMethods.GetForegroundWindow();

                var action = await automation.ActAsync(entityId, kind, parameters, CancellationToken.None);

                // The pointer is not compared before and after: the same person is moving it. What proves the
                // agent did not is the channel — every action here must have been carried by the page's
                // protocol or the no-activate restore, and neither has any way to move a pointer.
                if (action.Injection is { } injection)
                {
                    Assert.True(
                        injection.Detail.StartsWith("cdp.", StringComparison.Ordinal)
                        || injection.Detail.StartsWith("bg.", StringComparison.Ordinal),
                        $"{kind} went by another channel: {injection.Detail}");
                }

                AssertForeground(foreground, $"during {kind}", window.Handle);
                return action;
            }

            // ---- a click, aimed by what is on the screen ----------------------------------------------
            // The page answers over the debug port before the window has painted it, so the picture is retried:
            // what is being waited for is the first frame, not a property of the feature.
            CaptureResult frame = null!;
            (int X, int Y)? button = null;
            for (var attempt = 0; attempt < 40 && button is null; attempt++)
            {
                frame = await automation.Capture.CaptureWindowAsync(window.Handle);
                Assert.True(frame.Ok, frame.Error);
                button = FindRed(frame);
                if (button is null)
                {
                    await Task.Delay(250);
                }
            }

            Assert.True(
                button is not null,
                "the red button is not in the picture of the window — PrintWindow returned no page content: "
                + $"{frame.Width}x{frame.Height} via {frame.Method}, iconic={NativeMethods.IsIconic(window.Handle)}, "
                + $"centre pixel BGRA={Convert.ToHexString(frame.Bgra, ((frame.Height / 2 * frame.Width) + (frame.Width / 2)) * 4, 4)}, "
                + $"page pixel BGRA={Convert.ToHexString(frame.Bgra, (((frame.Height / 2) * frame.Width) + 200) * 4, 4)}, "
                + $"visibility={await source.EvaluateAsync(port, "document.visibilityState + \"/focus=\" + document.hasFocus()", null, CancellationToken.None)}");

            var target = (X: frame.ScreenRect!.X + button!.Value.X, Y: frame.ScreenRect.Y + button.Value.Y);

            var click = await ActAsync(ActionKind.LeftClick, new ActionParams { X = target.X, Y = target.Y });
            Assert.True(click.GuardPassed, click.GuardReason);
            Assert.True(click.Injection!.Ok, click.Injection.Detail);
            Assert.StartsWith("cdp.click-at", click.Injection.Detail, StringComparison.Ordinal);
            Assert.False(click.Raised);

            await WaitUntilAsync(async () => await TextOf(source, port, "log") == "已点击", "the page never saw the click");

            // How far off the click was, measured by the page itself: where it says the click arrived against
            // where it says the button is. A big button forgives a conversion that is a toolbar out; this does not.
            var landed = await source.EvaluateAsync(
                port,
                "(() => { const r = document.getElementById('press').getBoundingClientRect(); "
                + "return { dx: window.__click.x - (r.left + r.width / 2), dy: window.__click.y - (r.top + r.height / 2) }; })()",
                null,
                CancellationToken.None);
            using var offsets = JsonDocument.Parse(landed!);
            var offsetX = offsets.RootElement.GetProperty("dx").GetDouble();
            var offsetY = offsets.RootElement.GetProperty("dy").GetDouble();
            Assert.True(
                Math.Abs(offsetX) <= 2 && Math.Abs(offsetY) <= 2,
                $"the click landed ({offsetX:0.##}, {offsetY:0.##}) CSS px from the centre of the button");

            // ---- a point on the browser's own toolbar is refused, not clamped onto the page -----------
            var onToolbar = await ActAsync(ActionKind.LeftClick, new ActionParams { X = target.X, Y = window.Y + 5 });
            Assert.False(onToolbar.Injection!.Ok);
            Assert.Contains("不在页面内容上", onToolbar.Injection.Detail, StringComparison.Ordinal);

            // ---- text and keys, into a field the page has focused -----------------------------------------
            await source.EvaluateAsync(port, "document.getElementById('field').focus()", null, CancellationToken.None);

            var typed = await ActAsync(ActionKind.TypeText, new ActionParams { Text = "你好abc" });
            Assert.True(typed.Injection!.Ok, typed.Injection.Detail);
            Assert.Equal("你好abc", await ValueOf(source, port, "field"));

            var backspace = await ActAsync(ActionKind.KeyPress, new ActionParams { VirtualKey = 0x08 });
            Assert.True(backspace.Injection!.Ok, backspace.Injection.Detail);
            Assert.Equal("你好ab", await ValueOf(source, port, "field"));

            // A shortcut selects and does not type its letter over the selection.
            var selectAll = await ActAsync(ActionKind.KeyPress, new ActionParams
            {
                VirtualKey = 0x41,
                Modifiers = new ushort[] { InputInjector.VirtualControl },
            });
            Assert.True(selectAll.Injection!.Ok, selectAll.Injection.Detail);
            await ActAsync(ActionKind.TypeText, new ActionParams { Text = "X" });
            Assert.Equal("X", await ValueOf(source, port, "field"));

            // ---- the wheel, the way the tool counts it (negative is down) --------------------------------
            var scrolled = await ActAsync(ActionKind.Scroll, new ActionParams { X = target.X, Y = target.Y, ScrollDelta = -3 });
            Assert.True(scrolled.Injection!.Ok, scrolled.Injection.Detail);
            await WaitUntilAsync(
                async () => double.Parse(await source.EvaluateAsync(port, "window.scrollY", null, CancellationToken.None) ?? "0", System.Globalization.CultureInfo.InvariantCulture) > 100,
                "the page did not scroll down");

            // ---- a minimized window is restored without being activated -------------------------------------
            NativeMethods.ShowWindow(window.Handle, 7); // SW_SHOWMINNOACTIVE
            Assert.True(NativeMethods.IsIconic(window.Handle));

            var focus = await ActAsync(ActionKind.Focus, new ActionParams());
            Assert.True(focus.Injection!.Ok, focus.Injection.Detail);
            Assert.False(NativeMethods.IsIconic(window.Handle), "the window was not restored");
            Assert.NotEqual(window.Handle, NativeMethods.GetForegroundWindow());

        }
        finally
        {
            if (processId != 0)
            {
                try
                {
                    Process.GetProcessById(processId).Kill(entireProcessTree: true);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                {
                    // Already gone.
                }
            }

            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (IOException)
            {
                // The browser may still be letting go of it; the temp directory is not worth failing a test over.
            }
        }
    }

    /// <summary>
    /// A real form with a text box and a long list, shown without being activated, on its own message loop. The
    /// patterns can only be proven against live controls: a stub would say nothing about whether the control
    /// really changed, which is the only thing these tests are for.
    /// </summary>
    private sealed class FieldWindow : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _closed = new(false);
        private Form? _form;
        private TextBox? _text;
        private ListBox? _list;

        private sealed class QuietForm : Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        public FieldWindow(string title)
        {
            var thread = new Thread(() =>
            {
                _form = new QuietForm
                {
                    Text = title,
                    ClientSize = new System.Drawing.Size(320, 260),
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(40, 40),
                    ShowInTaskbar = false,
                };

                _text = new TextBox { Name = "field", AccessibleName = "字段", Location = new System.Drawing.Point(10, 10), Width = 280 };
                _list = new ListBox { Name = "items", Location = new System.Drawing.Point(10, 50), Size = new System.Drawing.Size(280, 160) };
                _list.Items.AddRange(Enumerable.Range(1, 300).Select(i => (object)$"item {i}").ToArray());
                _form.Controls.Add(_text);
                _form.Controls.Add(_list);
                _form.Shown += (_, _) =>
                {
                    Handle = _form.Handle;
                    _ready.Set();
                };
                _form.FormClosed += (_, _) => _closed.Set();

                Application.Run(_form);
            })
            { IsBackground = true };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(15)), "the form never came up");
        }

        public nint Handle { get; private set; }

        public string HandleHex => Handle.ToString("x");

        public string Text => (string)_form!.Invoke(new Func<string>(() => _text!.Text));

        public void SetText(string value) => _form!.Invoke(new Action(() => _text!.Text = value));

        public int TopIndex => (int)_form!.Invoke(new Func<int>(() => _list!.TopIndex));

        public void Dispose()
        {
            try
            {
                _form?.BeginInvoke(new Action(() => _form.Close()));
            }
            catch (InvalidOperationException)
            {
                // Already closing.
            }

            _closed.Wait(TimeSpan.FromSeconds(5));
            _ready.Dispose();
            _closed.Dispose();
        }
    }

    [LiveDesktopFact]
    public void A_field_is_appended_to_through_its_value_pattern_and_never_wiped()
    {
        DpiAwareness.EnsurePerMonitorV2();
        using var host = new FieldWindow("partner-bg-field-live");
        using var automation = new DesktopAutomation();

        var observation = automation.Observe(elementsFor: host.HandleHex);
        var field = observation.Elements.FirstOrDefault(e => e.WindowId == host.HandleHex && e.Role == "Edit");
        Assert.NotNull(field);

        NativeMethods.GetCursorPos(out var cursorBefore);
        var typed = new ActionParams { Text = "abc" };

        // Off by default: with the foreground available, real keystrokes are the more faithful action.
        Assert.Null(UiaTree.TryAct(host.Handle, host.HandleHex, field!.ElementId, ActionKind.TypeText, null, typed, background: false));
        Assert.Equal(string.Empty, host.Text);

        var first = UiaTree.TryAct(host.Handle, host.HandleHex, field.ElementId, ActionKind.TypeText, null, typed, background: true);
        Assert.NotNull(first);
        Assert.True(first!.Ok, first.Detail);
        Assert.Equal("uia.value", first.Method);
        Assert.Equal("abc", host.Text);

        // A second call adds to what is there. Replacing it is what ValuePattern.SetValue does on its own,
        // and what would wipe a half-filled form.
        var second = UiaTree.TryAct(host.Handle, host.HandleHex, field.ElementId, ActionKind.TypeText, null, new ActionParams { Text = "def" }, background: true);
        Assert.True(second!.Ok, second.Detail);
        Assert.Equal("abcdef", host.Text);
        Assert.Contains("追加", second.Detail, StringComparison.Ordinal);

        NativeMethods.GetCursorPos(out var cursorAfter);
        Assert.Equal((cursorBefore.X, cursorBefore.Y), (cursorAfter.X, cursorAfter.Y));
    }

    [LiveDesktopFact]
    public void A_list_is_scrolled_by_the_container_not_by_the_wheel()
    {
        DpiAwareness.EnsurePerMonitorV2();
        using var host = new FieldWindow("partner-bg-list-live");
        using var automation = new DesktopAutomation();

        var observation = automation.Observe(elementsFor: host.HandleHex);
        var row = observation.Elements.FirstOrDefault(e => e.WindowId == host.HandleHex && e.Role == "ListItem");
        Assert.NotNull(row);
        Assert.Equal(0, host.TopIndex);

        NativeMethods.GetCursorPos(out var cursorBefore);

        // Negative is down. The row has no scroll pattern of its own; the list above it does.
        var down = UiaTree.TryAct(host.Handle, host.HandleHex, row!.ElementId, ActionKind.Scroll, null, new ActionParams { ScrollDelta = -3 }, background: true);
        Assert.NotNull(down);
        Assert.True(down!.Ok, down.Detail);
        Assert.Equal("uia.scrollby", down.Method);

        var scrolled = host.TopIndex;
        Assert.True(scrolled > 0, "the list did not scroll down");

        var up = UiaTree.TryAct(host.Handle, host.HandleHex, row.ElementId, ActionKind.Scroll, null, new ActionParams { ScrollDelta = 3 }, background: true);
        Assert.True(up!.Ok, up.Detail);
        Assert.True(host.TopIndex < scrolled, "the list did not scroll back up");

        NativeMethods.GetCursorPos(out var cursorAfter);
        Assert.Equal((cursorBefore.X, cursorBefore.Y), (cursorAfter.X, cursorAfter.Y));
    }

    /// <summary>
    /// Fails with who holds the foreground now, because "a different handle" explains nothing.
    /// <para>
    /// A window of the browser itself is let through: a fresh profile shows its own dialogs at moments of its
    /// choosing (measured: a 524x364 owned popup, seconds after start), and that is the browser acting, not the
    /// agent. The window under test being in front is asserted separately and is not let through.
    /// </para>
    /// </summary>
    private static void AssertForeground(nint expected, string when, nint target = 0)
    {
        var actual = NativeMethods.GetForegroundWindow();
        if (actual == expected)
        {
            return;
        }

        NativeMethods.GetWindowThreadProcessId(actual, out var actualProcess);
        NativeMethods.GetWindowThreadProcessId(target, out var targetProcess);
        if (target != 0 && actual != target && actualProcess == targetProcess)
        {
            return;
        }

        static string Who(nint handle)
        {
            NativeMethods.GetWindowThreadProcessId(handle, out var pid);
            string? name = null;
            try
            {
                name = Process.GetProcessById((int)pid).ProcessName;
            }
            catch (ArgumentException)
            {
            }

            return $"0x{handle:x} '{WindowEnumerator.GetTitle(handle)}' [{name ?? pid.ToString()}] {LiveWindowProbe.Describe(handle)}";
        }

        Assert.Fail($"the foreground changed {when}: was {Who(expected)}, is now {Who(actual)}");
    }

    /// <summary>
    /// The centre of the red pixels in a frame, or null when there are too few to be a button. "Red" is
    /// red-dominant rather than exactly #ff0000: a browser that puts a dialog's scrim over the page (Edge does,
    /// on a fresh profile) dims it to about 40%, and the button is still the button.
    /// </summary>
    private static (int X, int Y)? FindRed(CaptureResult frame)
    {
        long count = 0;
        long sumX = 0;
        long sumY = 0;

        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var at = ((y * frame.Width) + x) * 4;
                var blue = frame.Bgra[at];
                var green = frame.Bgra[at + 1];
                var red = frame.Bgra[at + 2];

                if (red >= 0x50 && green < red / 4 && blue < red / 4)
                {
                    count++;
                    sumX += x;
                    sumY += y;
                }
            }
        }

        return count < 500 ? null : ((int)(sumX / count), (int)(sumY / count));
    }

    private static async Task<string?> TextOf(CdpDomSource source, int port, string id) =>
        await source.EvaluateAsync(port, $"document.getElementById('{id}').textContent", null, CancellationToken.None);

    private static async Task<string?> ValueOf(CdpDomSource source, int port, string id) =>
        await source.EvaluateAsync(port, $"document.getElementById('{id}').value", null, CancellationToken.None);

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> read) where T : class
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (await read() is { } found)
            {
                return found;
            }

            await Task.Delay(250);
        }

        return null;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> until, string failure)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await until())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail(failure);
    }
}

internal static class LiveWindowProbe
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassNameW(nint hWnd, System.Text.StringBuilder text, int count);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out NativeMethods.Rect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint command);

    /// <summary>Class, size and ownership: enough to tell a browser's own popup from the window under test.</summary>
    internal static string Describe(nint handle)
    {
        var text = new System.Text.StringBuilder(256);
        GetClassNameW(handle, text, text.Capacity);
        GetWindowRect(handle, out var rect);
        var owner = GetWindow(handle, 4);
        return $"class={text} rect=({rect.Left},{rect.Top} {rect.Width}x{rect.Height}) visible={IsWindowVisible(handle)} owner=0x{owner:x}";
    }
}
