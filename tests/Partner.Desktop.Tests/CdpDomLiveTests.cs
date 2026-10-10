using System.Diagnostics;
using Partner.Desktop;
using Partner.Desktop.Input;
using Partner.Desktop.Interop;
using Partner.Desktop.Perception;
using Partner.Desktop.Windows;
using Xunit;

namespace Partner.Desktop.Tests;

/// <summary>
/// The DOM path against a real browser.
/// <para>
/// Everything else about this feature is arithmetic, and arithmetic can be asserted anywhere. What only a real
/// browser can answer is the part that decides whether the feature is worth having: does a page launched with
/// a debug port actually report its buttons, do their coordinates land inside the window, and does pressing one
/// reach the page — rather than a socket that answered and an element list nobody verified.
/// </para>
/// <para>
/// Opt-in on <c>PARTNER_DESKTOP_LIVE=1</c> for the same reason as the other live tests: it starts a browser.
/// The browser is closed again in a <c>finally</c>, because leaving one open would make the next observation in
/// the run slower for everyone after it.
/// </para>
/// </summary>
public sealed class CdpDomLiveTests
{
    private sealed class LiveDesktopFactAttribute : FactAttribute
    {
        public LiveDesktopFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARTNER_DESKTOP_LIVE") != "1")
            {
                Skip = "live desktop test: set PARTNER_DESKTOP_LIVE=1 to run (it starts a browser)";
            }
        }
    }

    private static string? BrowserPath()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    [LiveDesktopFact]
    public async Task A_page_reports_its_buttons_and_one_of_them_can_be_pressed()
    {
        var browser = BrowserPath();
        Assert.True(browser is not null, "neither Chrome nor Edge is installed on this machine");

        var port = CdpRegistry.FreePort();
        var profile = Path.Combine(Path.GetTempPath(), "partner-cdp-live-" + Guid.NewGuid().ToString("N")[..8]);
        var page = Path.Combine(Path.GetTempPath(), "partner-cdp-live.html");

        await File.WriteAllTextAsync(page, """
            <!doctype html><meta charset="utf-8"><title>partner cdp live</title>
            <body style="margin:0;font:16px sans-serif">
              <h1 id="title">CDP live</h1>
              <button id="press" style="margin:40px;padding:12px 24px">提交</button>
              <input id="field" style="margin:40px;padding:8px">
              <div id="log">未点击</div>
              <script>
                document.getElementById('press').addEventListener('click', () => {
                  document.getElementById('log').textContent = '已点击';
                });
              </script>
            </body>
            """);

        using var browserProcess = Start(browser!, port, profile, page);

        try
        {
            var registry = new CdpRegistry();
            registry.Register(browserProcess.Id, port);

            var source = new CdpDomSource();
            var snapshot = await WaitForPageAsync(source, port);

            Assert.NotNull(snapshot);
            Assert.Contains(snapshot!.Elements, element => element.Name == "提交" && element.Role == "button");
            Assert.Contains(snapshot.Elements, element => element.Role == "textbox");

            // The coordinates the server would hand the model have to be inside the window they name. The
            // conversion is viewport + client origin + device scale, and leaving out any one of the three lands
            // the point somewhere plausible and wrong.
            var window = await WaitForWindowAsync(Path.GetFileNameWithoutExtension(browser!));
            Assert.NotNull(window);

            var clientOrigin = new NativeMethods.Point { X = 0, Y = 0 };
            Assert.True(NativeMethods.ClientToScreen(window!.Handle, ref clientOrigin));

            foreach (var record in snapshot.Elements)
            {
                var screen = CdpDomSource.ToScreen(record, clientOrigin.X, clientOrigin.Y, snapshot.DevicePixelRatio);
                Assert.True(
                    screen == screen.Intersect(new PixelRect(window.X, window.Y, window.Width, window.Height)),
                    $"{record.Name} mapped to {screen}, which is not inside the window it was read from");
            }

            // The press reaches the page, not a coordinate near it.
            var button = snapshot.Elements.First(element => element.Name == "提交");
            var press = await source.ActAsync(port, button.Marker, CancellationToken.None);

            Assert.NotNull(press);
            Assert.True(press!.Ok, press.Detail);
            Assert.Equal("cdp.click", press.Method);

            await WaitForAsync(async () => (await EvaluateAsync(port, "document.getElementById('log').textContent")) == "已点击");
        }
        finally
        {
            try
            {
                if (!browserProcess.HasExited)
                {
                    browserProcess.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
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

    private static Process Start(string browser, int port, string profile, string page)
    {
        // A dedicated profile, because a real one is already in use by whatever the developer has open and the
        // browser would hand the command line to that process instead of opening a port — which is the very
        // single-instance case this test exists to prove the other half of.
        var startInfo = new ProcessStartInfo(browser)
        {
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add($"--remote-debugging-port={port}");
        startInfo.ArgumentList.Add($"--user-data-dir={profile}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add(page);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("the browser did not start");
    }

    private static async Task<DesktopWindow?> WaitForWindowAsync(string processName)
    {
        // The page answers over the debug port well before its window is on screen, and a window that is not
        // there yet is not a coordinate this test can check anything against.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var found = WindowEnumerator.Find(processName);
            if (found is { Width: > 0, Height: > 0 })
            {
                return found;
            }

            await Task.Delay(250);
        }

        return null;
    }

private static async Task<CdpDomSource.CdpDomSnapshot?> WaitForPageAsync(CdpDomSource source, int port)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var snapshot = await source.ReadAsync(port, CancellationToken.None);
            if (snapshot is { Elements.Count: > 0 })
            {
                return snapshot;
            }

            await Task.Delay(250);
        }

        return null;
    }

    private static async Task WaitForAsync(Func<Task<bool>> until)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await until())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("the page never reported the click");
    }

    private static async Task<string?> EvaluateAsync(int port, string expression) =>
        await new CdpDomSource().EvaluateAsync(port, expression, CancellationToken.None);
}