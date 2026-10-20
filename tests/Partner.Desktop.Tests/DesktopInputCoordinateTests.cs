using Partner.Desktop.Input;
using Partner.Desktop.Interop;

namespace Partner.Desktop.Tests;

/// <summary>
/// The coordinate contract of the input layer, asserted on the <c>INPUT</c> records themselves rather than on
/// where the cursor ended up — these tests must never move the user's mouse. The records are what prove a
/// screen coordinate carries <c>MOUSEEVENTF_ABSOLUTE</c>; without it the coordinate is applied as a relative
/// mouse delta and every click lands in the corner of the screen.
/// </summary>
public sealed class DesktopInputCoordinateTests
{
    /// <summary>A 2560×1440 display with its origin at (0,0).</summary>
    private static readonly VirtualDesktop Screen = new(0, 0, 2560, 1440);

    private static InputInjector InjectorOn(VirtualDesktop desktop) => new(() => desktop);

    private static uint FlagsOf(NativeMethods.Input input) => input.Union.Mouse.Flags;

    /// <summary>
    /// The mapping Windows actually applies when it turns a normalized coordinate back into a pixel, worked
    /// out independently of <see cref="VirtualDesktop.Normalize"/>. This is the contract, not a restatement
    /// of the implementation: every assertion below is about where the pointer ends up, so a formula that
    /// does not land on the requested pixel fails even if it is internally consistent.
    /// </summary>
    private static int LandsAt(int normalized, int origin, int size) => origin + (int)((normalized * (long)size) / 65536);

    [Fact]
    public void Positional_events_are_marked_absolute()
    {
        var injector = InjectorOn(Screen);

        Assert.True(injector.TryBuildPointer(800, 600, 0, out var moved, out _));
        Assert.True(injector.TryBuildPointer(
            800, 600, NativeMethods.MouseeventfLeftdown | NativeMethods.MouseeventfLeftup, out var clicked, out _));

        Assert.True((FlagsOf(moved[0]) & NativeMethods.MouseeventfAbsolute) != 0, "move is not absolute");
        Assert.True((FlagsOf(clicked[0]) & NativeMethods.MouseeventfAbsolute) != 0, "click is not absolute");

        // VIRTUALDESK only means anything alongside ABSOLUTE, and without it a point on a second monitor is
        // normalized against the primary instead of against the whole desktop.
        Assert.True((FlagsOf(moved[0]) & NativeMethods.MouseeventfVirtualdesk) != 0, "move is not virtual-desktop");
    }

    [Fact]
    public void A_screen_point_becomes_a_normalized_position_not_a_delta()
    {
        Assert.True(InjectorOn(Screen).TryBuildPointer(1280, 720, 0, out var inputs, out _));

        var mouse = inputs[0].Union.Mouse;

        // A relative interpretation would have put 1280 and 720 here — one past the top of the 0..65535 range —
        // and the pointer would have been dragged to the bottom-right corner of the screen.
        Assert.InRange(mouse.Dx, 0, 65535);
        Assert.InRange(mouse.Dy, 0, 65535);

        // And it is the pixel that was asked for, not merely one near it.
        Assert.Equal(1280, LandsAt(mouse.Dx, 0, 2560));
        Assert.Equal(720, LandsAt(mouse.Dy, 0, 1440));
    }

    [Fact]
    public void Every_reachable_pixel_lands_on_itself()
    {
        // The invariant: Windows resolves a normalized coordinate with floor(n * size / 65536), so a scaling
        // that treats the desktop as 65535 evenly-spaced positions puts a measurable number of columns on
        // their left-hand neighbour: measured 8 of 2560 columns and
        // 5 of 1440 rows, always one pixel short. One pixel is invisible on a window-sized target and is
        // the wrong control on a three-pixel-wide one, and nothing in the pipeline reports an error.
        foreach (var (origin, size) in new[]
                 {
                     (0, 2560), (0, 1440), (0, 1920), (0, 3840), (-1920, 4480), (-1920, 5760), (0, 7680),
                 })
        {
            for (var pixel = 0; pixel < size; pixel++)
            {
                var normalized = VirtualDesktop.Normalize(origin + pixel, origin, size);

                Assert.Equal(origin + pixel, LandsAt(normalized, origin, size));
            }
        }
    }

    [Fact]
    public void A_point_at_the_very_edge_of_the_desktop_is_still_reachable()
    {
        // Mapping the last pixel to exactly 65535 only holds when the desktop size divides 65536 evenly and
        // pushes the far edge of every other size a pixel short. The last pixel has to be reachable, not
        // mapped to a magic number.
        Assert.Equal(2559, VirtualDesktop.Denormalize(VirtualDesktop.Normalize(2559, 0, 2560), 0, 2560));
        Assert.Equal(1919, VirtualDesktop.Denormalize(VirtualDesktop.Normalize(1919, 0, 1920), 0, 1920));
        Assert.Equal(-1, VirtualDesktop.Denormalize(VirtualDesktop.Normalize(-1, -1920, 4480), -1920, 4480));
    }

    [Fact]
    public void Normalization_covers_both_ends_of_the_desktop()
    {
        Assert.True(InjectorOn(Screen).TryBuildPointer(0, 0, 0, out var topLeft, out _));
        Assert.True(InjectorOn(Screen).TryBuildPointer(2559, 1439, 0, out var bottomRight, out _));

        // Both endpoints inclusive: the far edge has to be reachable, or a click on a right-hand toolbar
        // silently lands a pixel short of it, forever. Reachable means it resolves back to that pixel —
        // not that it happens to equal 65535, which only holds for a size that divides 65536 evenly.
        Assert.Equal(0, LandsAt(topLeft[0].Union.Mouse.Dx, 0, 2560));
        Assert.Equal(0, LandsAt(topLeft[0].Union.Mouse.Dy, 0, 1440));
        Assert.Equal(2559, LandsAt(bottomRight[0].Union.Mouse.Dx, 0, 2560));
        Assert.Equal(1439, LandsAt(bottomRight[0].Union.Mouse.Dy, 0, 1440));
    }

    [Fact]
    public void A_desktop_with_a_negative_origin_is_normalized_as_one_space()
    {
        // A second monitor to the left of the primary: the virtual desktop starts at a negative x.
        var spanning = new VirtualDesktop(-1920, 0, 4480, 1440);
        var injector = InjectorOn(spanning);

        Assert.True(injector.TryBuildPointer(-1920, 0, 0, out var farLeft, out _));
        Assert.True(injector.TryBuildPointer(0, 0, 0, out var boundary, out _));
        Assert.True(injector.TryBuildPointer(2559, 1439, 0, out var primaryRight, out _));

        // Normalizing against the primary's width instead of the desktop's would land this point near the
        // far right of the range while the desktop still had 1920 pixels to the left — a click on the wrong
        // monitor. The assertion is about the landing pixel, which is the thing that has to be right.
        Assert.Equal(-1920, LandsAt(farLeft[0].Union.Mouse.Dx, -1920, 4480));
        Assert.Equal(0, LandsAt(boundary[0].Union.Mouse.Dx, -1920, 4480));
        Assert.Equal(2559, LandsAt(primaryRight[0].Union.Mouse.Dx, -1920, 4480));
    }

    [Fact]
    public void A_point_outside_the_virtual_desktop_is_refused_not_clamped()
    {
        // Silently clamping would turn "click the Save button" into "click the screen edge" — the exact class
        // of quiet wrongness this layer exists to prevent.
        Assert.False(InjectorOn(Screen).TryBuildPointer(2560, 700, 0, out var inputs, out var error));

        Assert.Empty(inputs);
        Assert.Contains("虚拟桌面", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_drag_waypoint_is_absolute_and_in_range()
    {
        Assert.True(InjectorOn(Screen).TryBuildDrag(100, 100, 900, 700, out var inputs, out _));

        // A press, four waypoints, a release.
        Assert.Equal(6, inputs.Length);

        for (var step = 0; step < inputs.Length; step++)
        {
            Assert.True((FlagsOf(inputs[step]) & NativeMethods.MouseeventfAbsolute) != 0, $"waypoint {step} is not absolute");
            Assert.InRange(inputs[step].Union.Mouse.Dx, 0, 65535);
            Assert.InRange(inputs[step].Union.Mouse.Dy, 0, 65535);
        }

        // The press and the release have to be at the points the caller named — the same way, by where the
        // pointer lands rather than by the number that happened to be produced for it.
        Assert.Equal(100, LandsAt(inputs[0].Union.Mouse.Dx, 0, 2560));
        Assert.Equal(100, LandsAt(inputs[0].Union.Mouse.Dy, 0, 1440));
        Assert.Equal(900, LandsAt(inputs[^1].Union.Mouse.Dx, 0, 2560));
        Assert.Equal(700, LandsAt(inputs[^1].Union.Mouse.Dy, 0, 1440));

        // The press carries the button down and the release carries it up; nothing in between does.
        Assert.True((FlagsOf(inputs[0]) & NativeMethods.MouseeventfLeftdown) != 0);
        Assert.True((FlagsOf(inputs[0]) & NativeMethods.MouseeventfLeftup) == 0);
        Assert.True((FlagsOf(inputs[^1]) & NativeMethods.MouseeventfLeftup) != 0);
        Assert.True((FlagsOf(inputs[^1]) & NativeMethods.MouseeventfLeftdown) == 0);
    }

    [Fact]
    public void A_drag_off_the_desktop_is_refused_before_any_event_is_built()
    {
        // Nothing half-sent: a refused drag must not be able to leave a button held down.
        Assert.False(InjectorOn(Screen).TryBuildDrag(100, 100, 100, 5000, out var inputs, out var error));

        Assert.Empty(inputs);
        Assert.Contains("虚拟桌面", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_and_denormalize_round_trip_to_the_same_pixel()
    {
        var desktop = new VirtualDesktop(-1920, -200, 4480, 1640);

        for (var x = desktop.X; x < desktop.X + desktop.Width; x++)
        {
            // Exactly, not within a pixel: the mapping is lossless in the direction a click depends on.
            Assert.Equal(x, VirtualDesktop.Denormalize(desktop.NormalizeX(x), desktop.X, desktop.Width));
        }
    }

    [Fact]
    public void Normalize_is_monotonic_across_the_desktop()
    {
        var desktop = new VirtualDesktop(0, 0, 2560, 1440);
        var previous = -1;

        for (var x = 0; x < desktop.Width; x++)
        {
            var normalized = desktop.NormalizeX(x);
            Assert.InRange(normalized, previous, 65535);
            previous = normalized;
        }
    }

    [Fact]
    public void A_degenerate_desktop_does_not_divide_by_zero()
    {
        // A service session, or a disconnected RDP session, reports zeroes for every metric.
        var degenerate = new VirtualDesktop(0, 0, 1, 1);

        Assert.Equal(0, degenerate.NormalizeX(0));
        Assert.Equal(0, degenerate.NormalizeY(0));
        Assert.False(degenerate.Contains(1, 1));
    }

    /// <summary>
    /// The wheel is the one input path whose numbers were passed through unscaled. Windows counts whole
    /// units of <c>WHEEL_DELTA</c> and accumulates the rest, so a raw notch count of 3 delivers no movement
    /// at all — while the injection still reports success and the model concludes there is nothing to scroll.
    /// </summary>
    [Fact]
    public void A_scroll_is_sent_in_whole_wheel_units_not_in_the_number_the_model_asked_for()
    {
        foreach (var notches in new[] { 1, 2, 3, 7, 10, -1, -3, -10 })
        {
            Assert.True(InjectorOn(Screen).TryBuildWheel(notches, out var inputs, out _));

            var wheel = inputs[0].Union.Mouse;

            // The control's own unit, so `notches` is what it counts.
            Assert.Equal((uint)(notches * InputInjector.WheelDelta), wheel.MouseData);

            // Negative is down, and it stays negative: an unsigned wrap here scrolls the other way.
            Assert.Equal(notches < 0, (int)wheel.MouseData < 0);
            Assert.True((wheel.Flags & NativeMethods.MouseeventfWheel) != 0);
        }
    }

    [Fact]
    public void A_scroll_of_no_notches_is_refused_rather_than_sent_as_nothing()
    {
        // A zero wheel event is not a no-op the system ignores; it is an event the window has to process.
        Assert.False(InjectorOn(Screen).TryBuildWheel(0, out var inputs, out var error));

        Assert.Empty(inputs);
        Assert.Equal("no delta", error);
    }

    [Fact]
    public void A_scroll_count_too_large_for_the_wheel_field_is_refused_not_wrapped()
    {
        // mouseData is a DWORD. Multiplying a large model-supplied count by 120 overflows it, and the
        // wrapped value is a scroll in the *opposite* direction — so this has to be refused, not truncated.
        Assert.False(InjectorOn(Screen).TryBuildWheel(int.MaxValue, out var inputs, out var error));

        Assert.Empty(inputs);
        Assert.Contains("out of range", error, StringComparison.Ordinal);
    }
}
