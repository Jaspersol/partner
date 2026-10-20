using Partner.Desktop.Input;
using Partner.Desktop;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;
using Xunit;

namespace Partner.Desktop.Tests;

/// <summary>
/// The page's DOM as a source of controls, for applications UI Automation cannot see: a Chromium or Electron
/// window exposes one control, the webview's document surface, so every button in it is reachable only by a
/// coordinate the model read off a picture. These tests pin the mapping and the honesty rules, not the socket.
/// </summary>
public sealed class CdpDomTests
{
    private static CdpElementRecord Record(
        string marker = "3",
        string name = "播放",
        string role = "button",
        double x = 120,
        double y = 240,
        double width = 80,
        double height = 32) =>
        new(marker, name, role, x, y, width, height);


    // Which page one port answers with when several tabs share a debug port.

    [Theory]
    [InlineData("音乐 - 网易云音乐", "音乐", true)]
    [InlineData("音乐", "音乐 - 网易云音乐", true)]
    [InlineData("音乐 - 网易云音乐", "播放列表", false)]
    [InlineData("", "", false)]
    public void The_window_title_and_the_page_title_name_the_same_tab(string windowTitle, string pageTitle, bool matched)
    {
        // Chromium titles the window after the page, often with a suffix; Electron may title it after the
        // app. Either direction counts, so both conventions land on their page.
        Assert.Equal(matched, CdpDomSource.Matches(pageTitle, windowTitle));
    }

    [Fact]
    public async Task A_port_serving_two_tabs_answers_the_window_that_asks()
    {
        // Two windows share one debug port; the first page in the list is not the one every window means.
        // The pick goes by title; when a hint matches nothing, the first page still answers.
        var safe = new CdpPage("第一标签", "http://x/1", "ws://first");
        var wanted = new CdpPage("音乐 - 网易云音乐", "http://x/2", "ws://second");

        CdpPage? picked = null;
        var source = new CdpDomSource(connect: (port, hint) =>
        {
            picked = CdpDomSource.Matches(wanted.Title, hint) ? wanted : safe;
            return Task.FromResult<CdpPage?>(picked);
        });

        // The read itself needs a real page (the socket open fails on a fake), so this asserts the pick: which
        // page the hint routed to, because that is where both the read and the press land.
        await source.ReadAsync(9222, "音乐 - 网易云音乐");
        Assert.Equal("ws://second", picked!.WebSocketDebuggerUrl);

        // The un-hinted call keeps first page, first socket.
        picked = null;
        await source.ReadAsync(9222);
        Assert.Equal("ws://first", picked!.WebSocketDebuggerUrl);
    }

    [Fact]
    public void Viewport_coordinates_become_screen_coordinates()
    {
        // A window whose content starts 8px from its left edge and 31px below its top.
        var rect = CdpDomSource.ToScreen(Record(x: 100, y: 200), 8, 31, 1);

        Assert.Equal(108, rect.X);
        Assert.Equal(231, rect.Y);
        Assert.Equal(80, rect.Width);
        Assert.Equal(32, rect.Height);
    }

    [Fact]
    public void A_scaled_display_is_converted_rather_than_assumed_away()
    {
        // A viewport rectangle is in CSS pixels; at 150% three physical pixels sit where the page drew one.
        // An unconverted point lands up to a third of the screen away, on a neighbouring control, with every
        // step reporting success.
        var atOne = CdpDomSource.ToScreen(Record(x: 100), 0, 0, 1);
        var atOneAndAHalf = CdpDomSource.ToScreen(Record(x: 100), 0, 0, 1.5);

        Assert.Equal(100, atOne.X);
        Assert.Equal(150, atOneAndAHalf.X);
        Assert.Equal(120, atOneAndAHalf.Width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_absurd_or_missing_scale_factor_does_not_produce_nonsense(double ratio)
    {
        // A page reporting devicePixelRatio 0 has told us nothing usable; treating it as 1 keeps the answer
        // readable on a known-unscaled machine instead of collapsing every element to a point at the corner.
        var rect = CdpDomSource.ToScreen(Record(x: 100, width: 80), 0, 0, ratio);

        Assert.Equal(100, rect.X);
        Assert.Equal(80, rect.Width);
    }

    [Theory]
    [InlineData("button", "Button")]
    [InlineData("link", "Hyperlink")]
    [InlineData("textbox", "Edit")]
    [InlineData("combobox", "ComboBox")]
    [InlineData("checkbox", "CheckBox")]
    [InlineData("radio", "RadioButton")]
    [InlineData("tab", "TabItem")]
    [InlineData("menuitem", "MenuItem")]
    [InlineData("listitem", "ListItem")]
    [InlineData("slider", "Slider")]
    [InlineData("switch", "CheckBox")]
    public void A_page_role_becomes_a_role_the_model_already_knows(string pageRole, string expected)
    {
        Assert.Equal(expected, CdpDomSource.RoleOf(pageRole));
    }

    [Fact]
    public void A_role_the_page_invents_falls_back_to_something_pressable()
    {
        // A page can invent a role such as role="carousel-thing". Pressing it is the only thing anyone means by
        // seeing it as a control, so it becomes a Button rather than being dropped or passed through.
        Assert.Equal("Button", CdpDomSource.RoleOf("carousel-thing"));
    }

    [Fact]
    public void A_page_element_is_the_same_shape_as_a_ui_automation_one()
    {
        // Everything downstream (numbering, screenshot marks, the server-computed click point, revalidation) is
        // unchanged by where a control came from, which is what makes the DOM a source rather than a second kind
        // of thing.
        var element = CdpDomSource.ToElement(Record(), "1a2b", 8, 31, 1);

        Assert.Equal("1a2b#cdp:3", element.ElementId);
        Assert.Equal("1a2b", element.WindowId);
        Assert.Equal("播放", element.Name);
        Assert.Equal("Button", element.Role);
        Assert.Equal((168, 287), element.Centre);
        Assert.False(element.IsOffscreen);
        Assert.Equal(
            DesktopObservation.ElementIdentity("1a2b", "播放", "Button"),
            element.IdentityDigest);
    }

    [Fact]
    public void The_marker_is_part_of_the_id_because_it_is_the_only_thing_that_survives()
    {
        // An index would name whatever sits at that position after a re-render, the failure the whole
        // element-addressed path exists to avoid.
        Assert.Contains("#cdp:3", CdpDomSource.ToElement(Record(), "w", 0, 0, 1).ElementId, StringComparison.Ordinal);
        Assert.NotEqual(
            CdpDomSource.ToElement(Record(marker: "3"), "w", 0, 0, 1).ElementId,
            CdpDomSource.ToElement(Record(marker: "4"), "w", 0, 0, 1).ElementId);
    }

    [Fact]
    public void A_dom_element_is_told_apart_from_a_ui_automation_one_by_its_window()
    {
        var dom = CdpDomSource.ToElement(Record(), "w", 0, 0, 1);
        var uia = new UiElement("w:3", "w", "播放", "Button", new PixelRect(0, 0, 10, 10), "d");

        Assert.True(DesktopAutomation.IsDomElementOf(dom, "w"));
        Assert.False(DesktopAutomation.IsDomElementOf(uia, "w"));
        Assert.False(DesktopAutomation.IsDomElementOf(dom, "other"));
    }

    [Fact]
    public void An_offscreen_page_element_is_kept_and_marked()
    {
        // Same reason as the UI Automation walk: something thrown away can never be scrolled to.
        var element = CdpDomSource.ToElement(Record() with { Offscreen = true }, "w", 0, 0, 1);

        Assert.True(element.IsOffscreen);
    }

    [Fact]
    public async Task The_walk_returns_nothing_at_all_when_there_is_no_page()
    {
        // No port, no page, a browser that is not there: all the same answer, and none of them an exception.
        // An observation that threw would cost the whole turn, over one window out of nine.
        using var automation = new DesktopAutomation(cdp: new CdpDomSource(), cdpPorts: new CdpRegistry());

        Assert.Equal(0, await automation.AddDomElementsAsync());
    }

    [Fact]
    public async Task Without_a_dom_source_the_walk_is_simply_absent()
    {
        using var automation = new DesktopAutomation();

        Assert.Null(automation.Cdp);
        Assert.Equal(0, await automation.AddDomElementsAsync());
        Assert.Null(await automation.TryActDomAsync(1, "w", "w:0", ActionKind.LeftClick, default));
    }

    [Fact]
    public async Task An_element_that_is_not_a_pages_is_left_to_the_other_path()
    {
        using var automation = new DesktopAutomation(cdp: new CdpDomSource(), cdpPorts: new CdpRegistry());

        Assert.Null(await automation.TryActDomAsync(1, "w", "w:3", ActionKind.LeftClick, default));
        Assert.Null(await automation.TryActDomAsync(1, "w", "not-an-element-id", ActionKind.LeftClick, default));
    }

    [Fact]
    public async Task An_id_with_a_marker_but_nothing_after_it_is_not_a_handle()
    {
        using var automation = new DesktopAutomation(cdp: new CdpDomSource(), cdpPorts: new CdpRegistry());

        Assert.Null(await automation.TryActDomAsync(1, "w", "w#cdp:", ActionKind.LeftClick, default));
    }

    [Fact]
    public async Task An_action_that_is_not_a_press_is_not_a_pages_to_handle()
    {
        using var automation = new DesktopAutomation(cdp: new CdpDomSource(), cdpPorts: new CdpRegistry());

        // Typing into a page element is what a real keystroke is for; a DOM node has no opinion about it, and
        // faking one would be worse than sending the keys.
        Assert.Null(await automation.TryActDomAsync(1, "w", "w#cdp:3", ActionKind.TypeText, default));
    }
}

/// <summary>
/// Which programs get a debug port, and what the launch says about it.
/// </summary>
public sealed class CdpRegistryTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData(@"D:\apps\my-app\node_modules\electron\dist\electron.exe")]
    [InlineData(@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe")]
    public void A_chromium_program_is_recognised_by_its_executable(string path) =>
        Assert.True(CdpRegistry.IsChromiumFamily(path));

    [Theory]
    [InlineData(@"C:\game\WangYi\CloudMusic\cloudmusic.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Slack\slack.exe")]
    public void An_electron_program_named_after_its_product_is_not_recognised_by_name(string path)
    {
        // Stated rather than papered over: 网易云音乐 is an Electron app and nothing in "cloudmusic" says so.
        // The name list covers only what it can see; the flag in the arguments covers the rest.
        Assert.False(CdpRegistry.IsChromiumFamily(path));
    }

    [Theory]
    [InlineData("--remote-debugging-port=9222", 9222)]
    [InlineData("--remote-debugging-port 9333", 9333)]
    [InlineData("--no-first-run --remote-debugging-port=4444", 4444)]
    [InlineData("  --REMOTE-DEBUGGING-PORT=5555", 5555)]
    public void A_port_the_caller_asked_for_is_read_out_of_the_arguments(string arguments, int expected) =>
        Assert.Equal(expected, DesktopLaunchTool.RequestedPort(arguments));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--no-first-run")]
    [InlineData("--remote-debugging-port=99999")]
    [InlineData("--remote-debugging-port=abc")]
    [InlineData("--remote-debugging-port")]
    public void Arguments_with_no_usable_port_in_them_are_left_alone(string? arguments) =>
        Assert.Null(DesktopLaunchTool.RequestedPort(arguments));

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not(string? path) =>
        Assert.False(CdpRegistry.IsChromiumFamily(path));

    [Fact]
    public void A_port_is_remembered_for_the_process_that_opened_it()
    {
        var registry = new CdpRegistry();

        // Real pids: an entry for a process that does not exist is dropped on read, by design.
        var mine = Environment.ProcessId;

        Assert.Null(registry.PortOf(mine));
        registry.Register(mine, 9222);
        Assert.Equal(9222, registry.PortOf(mine));
        Assert.Null(registry.PortOf(mine + 100_000));
    }

    [Fact]
    public void A_process_that_is_gone_is_forgotten()
    {
        var registry = new CdpRegistry();
        registry.Register(42, 9222);

        registry.Forget(42);

        Assert.Null(registry.PortOf(42));
    }

    [Fact]
    public void A_free_port_is_one_nothing_is_listening_on()
    {
        var port = CdpRegistry.FreePort();

        Assert.InRange(port, 1, 65535);
    }
}

/// <summary>
/// What the launch says about the port it opened.
/// </summary>
public sealed class LaunchResultTests
{
    [Fact]
    public void A_browser_whose_port_opened_says_the_buttons_are_there()
    {
        var result = DesktopLaunchTool.BuildResult("chrome", null, 1234, chromium: true, opened: true, port: 9222);

        Assert.True(result.Ok);
        Assert.Contains("9222", result.Content!, StringComparison.Ordinal);
        Assert.Contains("elementId", result.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_browser_whose_port_did_not_open_says_why_and_what_to_do()
    {
        // The likeliest reason is not a bug and the model cannot guess it: an already-running single-instance
        // application takes the new command line and never opens a port. Saying "no controls" would send the
        // model looking for a control that was never going to appear.
        var result = DesktopLaunchTool.BuildResult("chrome", null, 1234, chromium: true, opened: false, port: 9222);

        Assert.True(result.Ok);
        Assert.Contains("单实例", result.Content!, StringComparison.Ordinal);
        Assert.Contains("关掉", result.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_program_that_is_not_a_browser_never_claims_a_port()
    {
        var result = DesktopLaunchTool.BuildResult("记事本", null, 1234, chromium: false, opened: false, port: 0);

        Assert.True(result.Ok);
        Assert.DoesNotContain("端口", result.Content!, StringComparison.Ordinal);
        Assert.Contains("desktop_observe", result.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void Where_it_came_from_is_still_said()
    {
        var result = DesktopLaunchTool.BuildResult(
            "网易云音乐", "开始菜单：cloudmusic", 1234, chromium: false, opened: false, port: 0);

        Assert.Contains("开始菜单", result.Content!, StringComparison.Ordinal);
        Assert.Contains("1234", result.Content!, StringComparison.Ordinal);
    }
}