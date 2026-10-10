using Partner.Desktop.Input;
using Partner.Desktop.Interop;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;
using Xunit;

namespace Partner.Desktop.Tests;

/// <summary>
/// Input the action surface could not express at all: chords, two clicks in time, a position inside a
/// control, and reaching a control that is below the fold.
/// <para>
/// Each of these was a hard "not supported" before. None of them is a nicety — copy is a chord, opening a
/// folder is two clicks, a split button's arrow is a position inside one control, and everything below the
/// fold was previously a "scroll by a guessed amount, look again, maybe scroll more" loop for the model.
/// </para>
/// </summary>
public sealed class DesktopInputCapabilityTests
{
    private static readonly VirtualDesktop Screen = new(0, 0, 2560, 1440);

    private static InputInjector Injector() => new(() => Screen);

    private static uint FlagsOf(NativeMethods.Input input) => input.Union.Mouse.Flags;

    private static ushort VirtualKeyOf(NativeMethods.Input input) => input.Union.Keyboard.VirtualKey;

    private static bool IsKeyUp(NativeMethods.Input input) =>
        (input.Union.Keyboard.Flags & NativeMethods.KeyeventfKeyup) != 0;

    // ---- chords -------------------------------------------------------------------------------

    [Fact]
    public void A_modifier_is_held_around_the_key_not_folded_into_it()
    {
        Assert.True(Injector().TryBuildKeyPress(0x43, new ushort[] { InputInjector.VirtualControl }, out var inputs, out _));

        // Windows delivers "Ctrl+C" as C pressed while Ctrl is down; a single event carrying both virtual keys
        // is a chord no target recognises, and the result would be a plain "c" in the field.
        Assert.Equal(4, inputs.Length);
        Assert.Equal(InputInjector.VirtualControl, VirtualKeyOf(inputs[0]));
        Assert.False(IsKeyUp(inputs[0]));
        Assert.Equal(0x43, VirtualKeyOf(inputs[1]));
        Assert.Equal(0x43, VirtualKeyOf(inputs[2]));
        Assert.True(IsKeyUp(inputs[2]));

        // Released last, and only after the key it modifies is up.
        Assert.Equal(InputInjector.VirtualControl, VirtualKeyOf(inputs[3]));
        Assert.True(IsKeyUp(inputs[3]));
    }

    [Fact]
    public void Several_modifiers_unwind_in_reverse()
    {
        Assert.True(Injector().TryBuildKeyPress(
            0x53,
            new[] { InputInjector.VirtualControl, InputInjector.VirtualShift },
            out var inputs,
            out _));

        Assert.Equal(6, inputs.Length);
        Assert.Equal(InputInjector.VirtualControl, VirtualKeyOf(inputs[0]));
        Assert.Equal(InputInjector.VirtualShift, VirtualKeyOf(inputs[1]));
        Assert.Equal(0x53, VirtualKeyOf(inputs[2]));
        Assert.Equal(0x53, VirtualKeyOf(inputs[3]));
        Assert.Equal(InputInjector.VirtualShift, VirtualKeyOf(inputs[4]));
        Assert.Equal(InputInjector.VirtualControl, VirtualKeyOf(inputs[5]));
    }

    [Fact]
    public void A_key_with_no_modifier_is_unchanged()
    {
        Assert.True(Injector().TryBuildKeyPress(0x41, null, out var inputs, out _));

        Assert.Equal(2, inputs.Length);
        Assert.Equal(0x41, VirtualKeyOf(inputs[0]));
        Assert.Equal(0x41, VirtualKeyOf(inputs[1]));
    }

    [Fact]
    public void Holding_a_modifier_while_pressing_another_one_is_an_ordinary_chord()
    {
        // Ctrl+Shift+V is something people type. Refusing every modifier-on-modifier would refuse that too.
        Assert.True(Injector().TryBuildKeyPress(
            InputInjector.VirtualShift,
            new[] { InputInjector.VirtualControl },
            out var inputs,
            out _));

        Assert.Equal(4, inputs.Length);
        Assert.Equal(InputInjector.VirtualControl, VirtualKeyOf(inputs[0]));
        Assert.Equal(InputInjector.VirtualShift, VirtualKeyOf(inputs[1]));
    }

    [Fact]
    public void Pressing_a_key_that_is_also_a_modifier_is_refused()
    {
        Assert.False(Injector().TryBuildKeyPress(
            InputInjector.VirtualControl,
            new[] { InputInjector.VirtualControl },
            out _,
            out var error));

        Assert.Contains("already held", error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ctrl", InputInjector.VirtualControl)]
    [InlineData("CTRL", InputInjector.VirtualControl)]
    [InlineData("control", InputInjector.VirtualControl)]
    [InlineData("shift", InputInjector.VirtualShift)]
    [InlineData("alt", InputInjector.VirtualMenu)]
    [InlineData("win", InputInjector.VirtualWin)]
    public void The_modifier_names_a_model_writes_are_the_keys_it_means(string name, ushort expected)
    {
        Assert.Equal(expected, DesktopActTool.Modifiers(new[] { name })[0]);
        Assert.Empty(DesktopActTool.UnknownModifiers(new[] { name }));
    }

    [Fact]
    public void A_misspelt_modifier_is_reported_rather_than_silently_dropped()
    {
        // Dropping it would send a plain "C" and report a Ctrl+C — a wrong action that looks like a right one.
        Assert.Equal(new[] { "shft" }, DesktopActTool.UnknownModifiers(new[] { "ctrl", "shft" }));
        Assert.Single(DesktopActTool.Modifiers(new[] { "ctrl", "shft" }));
    }

    [Fact]
    public void A_name_that_is_a_real_key_is_not_treated_as_a_modifier()
    {
        Assert.Equal(new[] { "a" }, DesktopActTool.UnknownModifiers(new[] { "a" }));
        Assert.Empty(DesktopActTool.Modifiers(new[] { "a" }));
    }

    // ---- two clicks in time -------------------------------------------------------------------

    [Fact]
    public void A_double_click_is_two_clicks_separated_by_a_pause()
    {
        Assert.True(Injector().TryBuildClick(
            800,
            600,
            NativeMethods.MouseeventfLeftdown,
            NativeMethods.MouseeventfLeftup,
            count: 2,
            out var inputs,
            out _));

        // Windows assembles a double click from two clicks in time. A down/up/down/up burst is one click that
        // moved, and a folder that opens on the second press never sees it.
        // Every mouse event carries the positional flags as well, so the button is read as a bit rather than as the
// whole value.
var presses = inputs
            .Count(input => input.Type == NativeMethods.InputMouse
                && (FlagsOf(input) & NativeMethods.MouseeventfLeftdown) != 0);
        var releases = inputs
            .Count(input => input.Type == NativeMethods.InputMouse
                && (FlagsOf(input) & NativeMethods.MouseeventfLeftup) != 0);

        Assert.Equal(2, presses);
        Assert.Equal(2, releases);
        Assert.Equal(5, inputs.Length);
    }

    [Fact]
    public void The_pause_between_two_clicks_is_a_real_one()
    {
        Assert.True(Injector().TryBuildClick(
            800, 600,
            NativeMethods.MouseeventfLeftdown, NativeMethods.MouseeventfLeftup,
            count: 2, out var inputs, out _));

        // A no-op keyboard event: the only way to spend time inside one SendInput batch.
        var pause = inputs[2];
        Assert.Equal(NativeMethods.InputKeyboard, pause.Type);
        Assert.Equal(0, pause.Union.Keyboard.VirtualKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void A_click_count_that_is_not_one_or_two_is_refused_before_anything_is_built(int count)
    {
        Assert.False(Injector().TryBuildClick(
            800, 600,
            NativeMethods.MouseeventfLeftdown, NativeMethods.MouseeventfLeftup,
            count, out var inputs, out var error));

        Assert.Empty(inputs);
        Assert.Contains("click count", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_single_click_is_still_one_press_and_one_release()
    {
        Assert.True(Injector().TryBuildClick(
            800, 600,
            NativeMethods.MouseeventfLeftdown, NativeMethods.MouseeventfLeftup,
            count: 1, out var inputs, out _));

        Assert.Equal(2, inputs.Length);
    }

    // ---- a position inside a control ---------------------------------------------------------

    private static UiElement Element(int x, int y, int width, int height) => new(
        "w:0", "w", "提交", "Button", new PixelRect(x, y, width, height), "digest");

    [Theory]
    [InlineData(0.5, 50, 100)]
    [InlineData(0.0, 25, 100)]
    [InlineData(1.0, 75, 100)]
    [InlineData(0.9, 70, 100)]
    public void An_offset_picks_a_position_inside_the_control(double fractionX, int expectedX, int expectedY)
    {
        // A 50×50 control at (25,100): every fraction has to land inside it, and none may be clamped.
        var at = DesktopActTool.Within(Element(25, 100, 50, 50), new RelativePoint(fractionX, 0));

        Assert.Equal(expectedX, at.X);
        Assert.Equal(expectedY, at.Y);
        Assert.InRange(at.X, 25, 75);
        Assert.InRange(at.Y, 100, 150);
    }

    [Fact]
    public void No_offset_means_the_centre_of_the_control()
    {
        // Not a corner of the screen, not the control's top-left: "this control" with no position in it means
        // the middle of it, which is what a person would click.
        var at = DesktopActTool.Within(Element(25, 100, 50, 50), null);

        Assert.Equal(50, at.X);
        Assert.Equal(125, at.Y);
    }

    [Fact]
    public void The_right_edge_of_a_split_button_is_not_its_centre()
    {
        // The reason offsets exist at all: a split button's arrow and its text are one element, and "click the
        // arrow" has no other way to be said.
        var element = Element(100, 200, 80, 30);

        var centre = DesktopActTool.Within(element, null);
        var arrow = DesktopActTool.Within(element, new RelativePoint(0.9, 0.5));

        Assert.Equal(140, centre.X);
        Assert.Equal(172, arrow.X);
        Assert.True(arrow.X > centre.X + 20, "the offset must actually move the point");
    }

    [Theory]
    [InlineData(-0.1, 0.5)]
    [InlineData(1.1, 0.5)]
    [InlineData(0.5, -1)]
    [InlineData(0.5, 2)]
    public void An_offset_outside_the_control_is_refused_rather_than_clamped(double x, double y)
    {
        var point = new RelativePoint(x, y);

        Assert.False(point.IsInside);
        Assert.NotNull(
            DesktopActTool.Guard(
                ActionKind.LeftClick,
                Window(),
                (100, 100),
                new DesktopActTool.DesktopActArguments(
                    null, "w:0", "LeftClick", null, null, null, null, null, null, null, Offset: point)));
    }

    private static UiEntity Window() => new()
    {
        EntityId = "w",
        Generation = "g",
        Role = "window",
        Label = "记事本",
        Rect = new PixelRect(0, 0, 800, 600),
        Confidence = 1,
        IdentityDigest = "i",
        LayoutDigest = "l",
        ProcessId = 1,
        ObservedAtMs = DateTimeOffset.UnixEpoch,
    };

    // ---- what the guard says about the new kinds ---------------------------------------------

    [Fact]
    public void A_scroll_into_view_needs_a_control_not_a_distance()
    {
        var without = DesktopActTool.Guard(
            ActionKind.ScrollIntoView,
            Window(),
            null,
            new DesktopActTool.DesktopActArguments(null, null, "ScrollIntoView", null, null, null, null, null, null, null));

        Assert.NotNull(without);
        Assert.Contains("elementId", without!, StringComparison.Ordinal);

        // With a control there is nothing to guess: the control reports how far it is from the viewport.
        Assert.Null(DesktopActTool.Guard(
            ActionKind.ScrollIntoView,
            Window(),
            null,
            new DesktopActTool.DesktopActArguments(null, "w:0", "ScrollIntoView", null, null, null, null, null, null, null)));
    }

    [Fact]
    public void A_double_click_is_treated_like_a_click_everywhere_a_click_is()
    {
        var arguments = new DesktopActTool.DesktopActArguments(
            null, null, "DoubleClick", 700, 500, null, null, null, null, null);

        Assert.Null(DesktopActTool.Guard(ActionKind.DoubleClick, Window(), (700, 500), arguments));

        // Off the window, and minimized: the same two refusals a single click gets, for the same reasons.
        Assert.Contains(
            "不在",
            DesktopActTool.Guard(ActionKind.DoubleClick, Window(), (5000, 5000), arguments)!,
            StringComparison.Ordinal);
        Assert.Contains(
            "最小化",
            DesktopActTool.Guard(ActionKind.DoubleClick, Window() with { IsMinimized = true }, (700, 500), arguments)!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_click_count_the_action_does_not_support_is_refused()
    {
        var arguments = new DesktopActTool.DesktopActArguments(
            null, null, "LeftClick", 700, 500, null, null, null, null, null, ClickCount: 4);

        Assert.Contains(
            "clickCount",
            DesktopActTool.Guard(ActionKind.LeftClick, Window(), (700, 500), arguments)!,
            StringComparison.Ordinal);
    }
}
/// <summary>
/// A control that is not on screen, and the one action that can change that.
/// <para>
/// The walk used to drop offscreen elements outright, which made "scroll down a bit and click it" impossible
/// to express: there was no element to name, so the only route was a guessed scroll amount followed by another
/// observation, and the model had to get the amount right by trial.
/// </para>
/// </summary>
public sealed class OffscreenElementTests
{
    private static readonly PixelRect Rect = new(100, 200, 80, 30);

    private static UiElement Element(bool offscreen) => new(
        "w:3", "w", "第七项", "ListItem", Rect, "digest", Value: null, IsOffscreen: offscreen);

    [Fact]
    public void An_offscreen_control_is_not_drawn_on_the_picture()
    {
        // Its rectangle is somewhere else on the screen; a mark there would invite a click into the void, which
        // is exactly what the grid is there to prevent.
        var marks = ScreenshotMarks.Marks(
            new[] { Element(offscreen: true), Element(offscreen: false) },
            new PixelRect(0, 0, 800, 600));

        Assert.Single(marks);
        Assert.Equal("第七项", marks[0].Label);
    }

    [Fact]
    public void An_offscreen_control_still_appears_in_the_answer()
    {
        // Dropping it is what made it unreachable. Reported with the flag that says it is not somewhere to
        // click yet, but somewhere to scroll to.
        var element = Element(offscreen: true);

        Assert.True(element.IsOffscreen);
        Assert.Equal("w:3", element.ElementId);
        Assert.Equal((140, 215), element.Centre);
    }

    [Fact]
    public void A_visible_control_is_not_marked_offscreen()
    {
        Assert.False(Element(offscreen: false).IsOffscreen);
    }
}
