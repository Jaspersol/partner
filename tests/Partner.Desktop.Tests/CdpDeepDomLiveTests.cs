using System.Diagnostics;
using Partner.Desktop.Perception;
using Xunit;

namespace Partner.Desktop.Tests;

/// <summary>
/// Controls inside a shadow root and inside a same-origin frame are reported and can be pressed, against a real
/// browser. Live: it starts Edge or Chrome with a throwaway profile, so it is skipped unless
/// <c>PARTNER_DESKTOP_LIVE=1</c>.
/// </summary>
public sealed class CdpDeepDomLiveTests
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

    private static string? BrowserPath() => new[]
    {
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    }.FirstOrDefault(File.Exists);

    private const string Html = """
        <!doctype html><meta charset="utf-8"><title>partner deep dom</title>
        <body style="margin:0;font:16px sans-serif">
          <button id="plain" style="margin:10px">普通按钮</button>
          <div id="host" style="margin:10px"></div>
          <iframe id="frame" style="display:block;margin:10px;width:400px;height:120px;border:2px solid #888"
                  srcdoc="<button id='inner' onclick=&quot;this.textContent='框架内已点击'&quot;>框架内按钮</button>"></iframe>
          <script>
            const root = document.getElementById('host').attachShadow({ mode: 'open' });
            root.innerHTML = "<button id='deep'>影子按钮</button>";
            root.getElementById('deep').addEventListener('click', (e) => { e.target.textContent = '影子内已点击'; });
          </script>
        </body>
        """;

    [LiveDesktopFact]
    public async Task Controls_in_a_shadow_root_and_in_a_frame_are_reported_and_pressable()
    {
        var browser = BrowserPath();
        Assert.True(browser is not null, "neither Chrome nor Edge is installed on this machine");

        var port = CdpRegistry.FreePort();
        var root = Path.Combine(Path.GetTempPath(), "partner-deep-dom-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var page = Path.Combine(root, "page.html");
        await File.WriteAllTextAsync(page, Html);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = browser!,
            Arguments = $"--remote-debugging-port={port} --user-data-dir=\"{Path.Combine(root, "profile")}\" --no-first-run "
                + $"--no-default-browser-check --window-position=-2000,0 \"{page}\"",
            UseShellExecute = false,
        });

        try
        {
            var source = new CdpDomSource();
            var snapshot = await source.ReadAsync(port, "partner deep dom");
            for (var attempt = 0; attempt < 80 && snapshot is not { Elements.Count: >= 3 }; attempt++)
            {
                await Task.Delay(250);
                snapshot = await source.ReadAsync(port, "partner deep dom");
            }

            Assert.NotNull(snapshot);
            var names = snapshot!.Elements.Select(element => element.Name).ToList();
            Assert.Contains("普通按钮", names);
            Assert.Contains("影子按钮", names);
            Assert.Contains("框架内按钮", names);

            // The frame's button must get a page coordinate that includes the frame's own offset, not one
            // relative to the frame's top-left corner.
            var inner = snapshot.Elements.Single(element => element.Name == "框架内按钮");
            var plain = snapshot.Elements.Single(element => element.Name == "普通按钮");
            Assert.True(inner.Y > plain.Y + 40, $"inner.Y={inner.Y} plain.Y={plain.Y}");

            foreach (var name in new[] { "影子按钮", "框架内按钮" })
            {
                var target = snapshot.Elements.Single(element => element.Name == name);
                var pressed = await source.ActAsync(port, target.Marker, "partner deep dom");
                Assert.NotNull(pressed);
                Assert.True(pressed!.Ok, pressed.Detail);
            }

            var after = await source.ReadAsync(port, "partner deep dom");
            var afterNames = after!.Elements.Select(element => element.Name).ToList();
            Assert.Contains("影子内已点击", afterNames);
            Assert.Contains("框架内已点击", afterNames);
        }
        finally
        {
            try
            {
                process?.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone.
            }

            await Task.Delay(500);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // The browser can hold its profile briefly after it is killed; a temp directory is not worth failing for.
            }
        }
    }

    [LiveDesktopFact]
    public async Task A_debug_port_the_person_opened_is_found_from_the_listener_table()
    {
        var browser = BrowserPath();
        Assert.True(browser is not null, "neither Chrome nor Edge is installed on this machine");

        var port = CdpRegistry.FreePort();
        var root = Path.Combine(Path.GetTempPath(), "partner-discover-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        // Started by hand, as a person would: nothing registers this port with the agent.
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = browser!,
            Arguments = $"--remote-debugging-port={port} --user-data-dir=\"{Path.Combine(root, "profile")}\" --no-first-run "
                + "--no-default-browser-check --window-position=-2000,0 about:blank",
            UseShellExecute = false,
        });

        try
        {
            // The started process may be a launcher that hands over to another and exits; the window belongs to
            // whoever listens on the port, so that is the pid to ask about.
            int? owner = null;
            for (var attempt = 0; attempt < 60 && owner is null; attempt++)
            {
                await Task.Delay(250);
                owner = TcpListeners.OwnerOf(port);
            }

            Assert.NotNull(owner);
            var registry = new CdpRegistry();
            Assert.Null(registry.PortOf(owner!.Value));

            var found = await registry.DiscoverAsync(owner.Value, Path.GetFileName(browser!));

            Assert.Equal(port, found);
            Assert.Equal(port, registry.PortOf(owner.Value));

            // A second look at a process that already has its port is answered from the table, not asked again.
            Assert.Equal(port, await registry.DiscoverAsync(owner.Value, Path.GetFileName(browser!)));
        }
        finally
        {
            try
            {
                process?.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone.
            }

            await Task.Delay(500);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // The browser can hold its profile briefly after it is killed.
            }
        }
    }
}
