using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;
using Partner.Desktop.Windows;

namespace Partner.Desktop.Tests;

/// <summary>
/// Acting without taking the user's screen: which mode allows what, what the page's debug protocol is sent,
/// where a screen point lands on a page, and how a launch is kept from landing on top of the user's work.
/// <para>
/// Everything here is pure or runs against a stand-in debug target. The failures worth pinning are the silent
/// ones — a click one toolbar too high, a Ctrl+A that types an "a", a wheel that scrolls the wrong way — and
/// none of them needs a browser to reproduce.
/// </para>
/// </summary>
public sealed class DesktopBackgroundTests
{

    [Theory]
    [InlineData("foreground", InputMode.Foreground)]
    [InlineData("auto", InputMode.Auto)]
    [InlineData("background", InputMode.Background)]
    [InlineData(null, InputMode.Foreground)]
    [InlineData("", InputMode.Foreground)]
    [InlineData("whatever", InputMode.Foreground)]
    public void A_setting_names_a_mode_and_anything_unknown_is_the_behaviour_that_came_before(string? text, InputMode expected)
    {
        // An older settings file has no such field, and a hand-edited one can say anything. Neither may turn
        // into a mode that takes the screen over less (or more) than the file's author could have meant.
        Assert.Equal(expected, InputModes.Parse(text));
    }

    [Theory]
    [InlineData("foreground", true)]
    [InlineData("auto", true)]
    [InlineData("background", true)]
    [InlineData("Auto", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_three_names_validate(string? text, bool valid)
    {
        Assert.Equal(valid, InputModes.IsValid(text));
    }


    [Theory]
    // Foreground: nothing changes, whatever the window offers.
    [InlineData(InputMode.Foreground, ActionKind.LeftClick, true, Route.Legacy)]
    [InlineData(InputMode.Foreground, ActionKind.TypeText, false, Route.Legacy)]
    // Auto: a page is reached through its protocol; anything else keeps the foreground path.
    [InlineData(InputMode.Auto, ActionKind.LeftClick, true, Route.Cdp)]
    [InlineData(InputMode.Auto, ActionKind.KeyPress, true, Route.Cdp)]
    [InlineData(InputMode.Auto, ActionKind.Focus, true, Route.Cdp)]
    [InlineData(InputMode.Auto, ActionKind.LeftClick, false, Route.Legacy)]
    [InlineData(InputMode.Auto, ActionKind.Focus, false, Route.Legacy)]
    // Background: a page is still the channel; a window without one is refused, except that it can be restored.
    [InlineData(InputMode.Background, ActionKind.Drag, true, Route.Cdp)]
    [InlineData(InputMode.Background, ActionKind.TypeText, false, Route.Refuse)]
    [InlineData(InputMode.Background, ActionKind.LeftClick, false, Route.Refuse)]
    [InlineData(InputMode.Background, ActionKind.Scroll, false, Route.Refuse)]
    [InlineData(InputMode.Background, ActionKind.Focus, false, Route.RestoreOnly)]
    public void The_mode_and_the_window_decide_the_channel(InputMode mode, ActionKind kind, bool hasPage, Route expected)
    {
        Assert.Equal(expected, BackgroundRoute.Decide(mode, kind, hasPage));
    }

    [Fact]
    public void A_refusal_names_the_ways_out_instead_of_only_saying_no()
    {
        // "Refused" alone sends a model back to try the same call. What it needs is which channel would have
        // carried the action.
        var reason = BackgroundRoute.Refusal(ActionKind.LeftClick, "记事本");

        Assert.Contains("记事本", reason, StringComparison.Ordinal);
        Assert.Contains("elementId", reason, StringComparison.Ordinal);
        Assert.Contains("desktop_launch", reason, StringComparison.Ordinal);
        Assert.Contains("auto", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_foreground_rule_for_keys_stands_unless_the_action_does_not_depend_on_the_foreground()
    {
        // A key sent while another window is in front goes to that other window — that is the rule. A page
        // reached through its debug port is addressed directly, so the rule has nothing to protect there.
        var window = new UiEntity
        {
            EntityId = "1a2b",
            Generation = "obs-1",
            Role = "window",
            Label = "网易云音乐",
            Value = "cloudmusic",
            Rect = new PixelRect(100, 100, 400, 300),
            Confidence = 1.0,
            IdentityDigest = "d",
            LayoutDigest = "l",
            ProcessId = 4242,
            ObservedAtMs = DateTimeOffset.UnixEpoch,
            IsForeground = false,
        };

        var arguments = new DesktopActTool.DesktopActArguments(null, null, "KeyPress", null, null, null, null, 0x0D, null, null);

        Assert.Contains("不在前台", DesktopActTool.Guard(ActionKind.KeyPress, window, null, arguments)!, StringComparison.Ordinal);
        Assert.Null(DesktopActTool.Guard(ActionKind.KeyPress, window, null, arguments, background: true));
    }


    [Fact]
    public void Enter_types_a_line_break_and_an_arrow_types_nothing()
    {
        Assert.Equal("\r", CdpInput.KeyOf(0x0D)!.Text);
        Assert.Null(CdpInput.KeyOf(0x25)!.Text);
        Assert.Equal("ArrowLeft", CdpInput.KeyOf(0x25)!.Key);
    }

    [Fact]
    public void A_letter_follows_shift_and_a_key_the_protocol_cannot_name_is_not_guessed()
    {
        Assert.Equal("a", CdpInput.KeyOf(0x41)!.Key);
        Assert.Equal("A", CdpInput.KeyOf(0x41, shifted: true)!.Key);
        Assert.Equal("KeyA", CdpInput.KeyOf(0x41)!.Code);
        Assert.Equal("F5", CdpInput.KeyOf(0x74)!.Key);

        // 0xFF is no key at all. Sending something else in its place would type what nobody asked for.
        Assert.Null(CdpInput.KeyOf(0xFF));
    }

    [Fact]
    public void A_shortcut_is_a_raw_key_event_so_it_does_not_also_type_its_letter()
    {
        // Ctrl+A with text on the key-down would select everything and then insert an "a" over it.
        var commands = CdpInput.KeyCommands(0x41, new ushort[] { InputInjector.VirtualControl })!;

        var types = commands.Select(command => (string)((IDictionary<string, object>)command.Params)["type"]).ToArray();
        Assert.Equal(new[] { "rawKeyDown", "rawKeyDown", "keyUp", "keyUp" }, types);

        var key = (IDictionary<string, object>)commands[1].Params;
        Assert.False(key.ContainsKey("text"));
        Assert.Equal(2, key["modifiers"]);

        // The modifier is held around the key and let go of after it, and the mask on its own release is empty.
        Assert.Equal("Control", ((IDictionary<string, object>)commands[0].Params)["key"]);
        Assert.Equal("Control", ((IDictionary<string, object>)commands[3].Params)["key"]);
        Assert.Equal(0, ((IDictionary<string, object>)commands[3].Params)["modifiers"]);
    }

    [Fact]
    public void A_plain_character_key_carries_its_text()
    {
        var commands = CdpInput.KeyCommands(0x0D, Array.Empty<ushort>())!;

        var down = (IDictionary<string, object>)commands[0].Params;
        Assert.Equal("keyDown", down["type"]);
        Assert.Equal("\r", down["text"]);
        Assert.Equal(13, down["windowsVirtualKeyCode"]);
    }

    [Fact]
    public void An_unknown_key_or_modifier_refuses_the_whole_chord()
    {
        Assert.Null(CdpInput.KeyCommands(0xFF, Array.Empty<ushort>()));
        Assert.Null(CdpInput.KeyCommands(0x41, new ushort[] { 0xE8 }));
    }

    [Fact]
    public void Modifiers_combine_into_the_protocol_bitmask()
    {
        Assert.Equal(0, CdpInput.ModifierMask(Array.Empty<ushort>()));
        Assert.Equal(2 | 8, CdpInput.ModifierMask(new ushort[] { InputInjector.VirtualControl, InputInjector.VirtualShift }));
        Assert.Equal(1 | 4, CdpInput.ModifierMask(new ushort[] { InputInjector.VirtualMenu, InputInjector.VirtualWin }));
    }


    [Fact]
    public void A_double_click_is_two_presses_whose_count_climbs()
    {
        // This is how a page tells a double click from two clicks. Two presses that both say 1 are two clicks.
        var commands = CdpInput.ClickCommands(10, 20, right: false, count: 2);

        var events = commands
            .Select(command => (IDictionary<string, object>)command.Params)
            .Select(body => ((string)body["type"], (int)body["clickCount"]))
            .ToArray();

        Assert.Equal(
            new[] { ("mouseMoved", 0), ("mousePressed", 1), ("mouseReleased", 1), ("mousePressed", 2), ("mouseReleased", 2) },
            events);
    }

    [Fact]
    public void A_right_click_holds_the_right_button()
    {
        var press = (IDictionary<string, object>)CdpInput.ClickCommands(1, 2, right: true, count: 1)[1].Params;

        Assert.Equal("right", press["button"]);
        Assert.Equal(2, press["buttons"]);
    }

    [Fact]
    public void The_wheel_turns_the_way_the_tool_says_not_the_way_the_protocol_does()
    {
        // The tool: positive scrolls up. The protocol: positive deltaY scrolls the page down. One flip, here.
        var up = (IDictionary<string, object>)CdpInput.WheelCommands(5, 5, notches: 3)[1].Params;
        var down = (IDictionary<string, object>)CdpInput.WheelCommands(5, 5, notches: -2)[1].Params;

        Assert.Equal(-300.0, up["deltaY"]);
        Assert.Equal(200.0, down["deltaY"]);
    }

    [Fact]
    public void A_drag_travels_in_steps_and_lets_go_where_it_was_aimed()
    {
        var commands = CdpInput.DragCommands(0, 0, 80, 40);
        var bodies = commands.Select(command => (IDictionary<string, object>)command.Params).ToList();

        Assert.Equal("mousePressed", bodies[1]["type"]);
        Assert.Equal("mouseReleased", bodies[^1]["type"]);
        Assert.Equal(80.0, bodies[^1]["x"]);
        Assert.Equal(40.0, bodies[^1]["y"]);

        // Every move while the button is held says so — a page tracking the pointer ignores one that does not.
        var moves = bodies.Where(body => (string)body["type"] == "mouseMoved" && (int)body["buttons"] == 1).ToList();
        Assert.Equal(8, moves.Count);
        Assert.True(commands.Where(c => c.DelayMs > 0).Count() >= 8);
    }


    [Fact]
    public void A_window_with_no_chrome_starts_the_page_at_the_client_area()
    {
        // Electron: the client area is the page.
        var origin = CdpInput.ViewportOrigin(new PixelRect(100, 200, 1000, 700), 1000, 700, 1);

        Assert.Equal((100, 200), origin!.Value);
    }

    [Fact]
    public void A_browsers_toolbar_pushes_the_page_down_by_the_part_of_the_client_area_that_is_not_page()
    {
        // 1500x1050 physical pixels of client area at 150%; the page is 1000x640 CSS px = 1500x960 physical.
        // The 90 left over is the tab strip and the address bar, and it is above the page.
        var origin = CdpInput.ViewportOrigin(new PixelRect(0, 0, 1500, 1050), 1000, 640, 1.5);

        Assert.Equal((0, 90), origin!.Value);
    }

    [Fact]
    public void A_frame_inside_the_client_area_is_taken_from_the_sides_and_the_bottom_not_added_to_the_toolbar()
    {
        // Measured on a real Edge window: client area 1249x1364, page 1241x1280 at 100%. The 8 px of width left
        // over is a resize border of 4 px on each side, and the same border runs along the bottom — so of the
        // 84 px of height left over, 80 is toolbar. Counting all 84 put every click 4 px too low.
        var origin = CdpInput.ViewportOrigin(new PixelRect(100, 50, 1249, 1364), 1241, 1280, 1);

        Assert.Equal((104, 130), origin!.Value);
    }

    [Theory]
    [InlineData(1000, 700, 1)] // narrower page: a side panel or a docked inspector this cannot place
    [InlineData(1200, 700, 1)] // a page wider than its window: the metrics are stale
    [InlineData(1100, 100, 1)] // 600 px of unexplained height is not a toolbar
    [InlineData(1100, 800, 1)] // a page taller than its window
    public void A_geometry_that_does_not_fit_is_refused_rather_than_approximated(double innerWidth, double innerHeight, double ratio)
    {
        Assert.Null(CdpInput.ViewportOrigin(new PixelRect(0, 0, 1100, 700), innerWidth, innerHeight, ratio));
    }

    [Fact]
    public void A_screen_point_becomes_a_page_point_and_one_off_the_page_is_not_clamped()
    {
        var origin = (X: 100, Y: 190);

        // 150% scaling: 150 physical px from the page's origin is 100 CSS px.
        Assert.Equal((100.0, 50.0), CdpInput.ToViewport(250, 265, origin, 1000, 640, 1.5)!.Value);

        // On the toolbar, above the page: not a click on the page's first row.
        Assert.Null(CdpInput.ToViewport(250, 150, origin, 1000, 640, 1.5));

        // Past the page's right edge.
        Assert.Null(CdpInput.ToViewport(100 + 1500, 300, origin, 1000, 640, 1.5));
    }


    [Fact]
    public void A_browser_launched_behind_other_windows_is_kept_painting()
    {
        var plain = BackgroundLaunch.WithRenderingFlags("--remote-debugging-port=9222");

        Assert.Contains("--disable-backgrounding-occluded-windows", plain, StringComparison.Ordinal);
        Assert.Contains("--disable-features=CalculateNativeWinOcclusion", plain, StringComparison.Ordinal);
        Assert.StartsWith("--remote-debugging-port=9222", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_feature_list_is_extended_not_replaced()
    {
        // A repeated switch is not a union: the last one wins, so adding a second would silently drop the first.
        var merged = BackgroundLaunch.WithRenderingFlags("--disable-features=Translate,Foo --x=1");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(merged, "--disable-features="));
        Assert.Contains("--disable-features=Translate,Foo,CalculateNativeWinOcclusion", merged, StringComparison.Ordinal);
        Assert.Contains("--x=1", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void The_flags_are_not_added_twice()
    {
        var once = BackgroundLaunch.WithRenderingFlags("--remote-debugging-port=9222");

        Assert.Equal(once, BackgroundLaunch.WithRenderingFlags(once));
        Assert.Equal(once, BackgroundLaunch.WithRenderingFlags(BackgroundLaunch.WithRenderingFlags(once)));
    }

    [Fact]
    public void No_arguments_still_get_the_flags()
    {
        var result = BackgroundLaunch.WithRenderingFlags(null);

        Assert.Contains("CalculateNativeWinOcclusion", result, StringComparison.Ordinal);
        Assert.False(result.StartsWith(' '));
    }

    [Fact]
    public void Only_new_windows_of_the_started_program_are_its_windows()
    {
        // In the seconds a launch is watched the user may open windows of their own. Those are not to be sent to
        // the bottom, and the foreground is not to be taken back from them.
        static DesktopWindow Window(nint handle, int process, long? started) => new()
        {
            Handle = handle,
            Title = $"w{handle}",
            ProcessId = process,
            ProcessStartTicks = started,
            X = 0,
            Y = 0,
            Width = 10,
            Height = 10,
            IsForeground = false,
        };

        var before = new BackgroundLaunch.Snapshot(1, new HashSet<nint> { 1, 2 }, StartedAtTicks: 1000);

        var launched = BackgroundLaunch.LaunchedWindows(before, processId: 77, new[]
        {
            Window(1, 5, 10),      // was there before the launch
            Window(3, 77, null),   // the process that was started (its start time may be unreadable)
            Window(4, 90, 2000),   // a process that began after the launch: a launcher handing off to the real program
            Window(5, 91, 500),    // a new window of a program that was already running — the user's own
            Window(6, 92, null),   // someone else, and nothing to say it began after the launch
        });

        Assert.Equal(new nint[] { 3, 4 }, launched.Select(window => window.Handle).ToArray());
    }

    [Theory]
    [InlineData(0, false, false, "没有看到新窗口")]
    [InlineData(1, false, false, "没有动你的前台窗口")]
    [InlineData(2, true, true, "已还给原来的窗口")]
    [InlineData(1, true, false, "还回去没成功")]
    public void The_launch_says_what_happened_to_the_screen(int parked, bool taken, bool restored, string expected)
    {
        var text = BackgroundLaunch.Describe(new BackgroundLaunch.Outcome(parked, restored, taken));

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }
}
