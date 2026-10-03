using Partner.Desktop;
using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using Partner.Desktop.Windows;

namespace Partner.Desktop.Tests;

/// <summary>
/// Pointer input is delivered by the OS to whatever window is under the point, so "the point is inside the
/// target's rectangle" is not the same as "the target will receive the click". These pin the rule that keeps
/// a click from silently landing somewhere the model did not name — the failure that reads as "the button did
/// nothing", because the injection succeeds and the target's frame never changes.
/// </summary>
public sealed class DesktopOcclusionTests
{
    private const nint Target = 0x1234;
    private const int TargetProcess = 42;

    /// <summary>A point that belongs to <paramref name="handle"/>, owned by <paramref name="owner"/>.</summary>
    private static Func<int, int, PointTarget> At(nint handle, int processId, string title, nint owner = 0) =>
        (_, _) => new PointTarget(handle, processId, title, owner == 0 ? handle : owner);

    private static ActionParams Params(int x, int y, int toX = 0, int toY = 0) =>
        new() { X = x, Y = y, ToX = toX, ToY = toY };

    [Fact]
    public void The_target_window_under_the_point_is_not_a_cover()
    {
        Assert.Null(DesktopAutomation.Occlusion(
            ActionKind.LeftClick, Params(100, 200), Target, At(Target, TargetProcess, "目标")));
    }

    [Fact]
    public void Another_window_on_the_point_is_refused_by_name()
    {
        // The measured case: the click went to the agent's own window, which covered the point, and the
        // result still said "注入成功 · 画面无变化" three times.
        var reason = DesktopAutomation.Occlusion(
            ActionKind.LeftClick, Params(742, 948), Target, At(0x9999, 7, "Partner"));

        Assert.NotNull(reason);
        Assert.Contains("(742,948)", reason, StringComparison.Ordinal);
        Assert.Contains("Partner", reason, StringComparison.Ordinal);
        Assert.Contains("focus", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_window_of_the_same_program_is_refused_too()
    {
        // A second window of the same process is still a window the model did not name — being the same
        // executable does not make the click land on the target.
        var reason = DesktopAutomation.Occlusion(
            ActionKind.LeftClick, Params(200, 200), Target, At(0x9999, TargetProcess, "另一个窗口"));

        Assert.NotNull(reason);
        Assert.Contains("另一个窗口", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_menu_owned_by_the_target_is_not_a_cover()
    {
        // A click on a menu item lands on the popup window, which the application owns — exactly where a
        // person's click would land. Refusing this would break every menu interaction.
        Assert.Null(DesktopAutomation.Occlusion(
            ActionKind.LeftClick, Params(300, 120), Target, At(0x5678, TargetProcess, "菜单", owner: Target)));
    }

    [Fact]
    public void A_drag_whose_release_point_is_covered_is_refused()
    {
        // The press lands in the target; the drop would land somewhere else — a file dropped into whatever
        // happens to be there.
        static PointTarget At2(int x, int y) => x == 100
            ? new PointTarget(Target, TargetProcess, "目标", Target)
            : new PointTarget(0x9999, 7, "挡住的", 0x9999);

        var reason = DesktopAutomation.Occlusion(ActionKind.Drag, Params(100, 100, 500, 500), Target, At2);

        Assert.NotNull(reason);
        Assert.Contains("(500,500)", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Moving_the_pointer_is_never_refused_for_what_is_under_it()
    {
        Assert.Null(DesktopAutomation.Occlusion(
            ActionKind.MovePointer, Params(742, 948), Target, At(0x9999, 7, "Partner")));
    }

    [Fact]
    public void Scrolling_is_refused_when_the_point_is_covered()
    {
        // The wheel goes to the window under the pointer, exactly like a click.
        Assert.NotNull(DesktopAutomation.Occlusion(
            ActionKind.Scroll, Params(742, 948), Target, At(0x9999, 7, "Partner")));
    }

    [Fact]
    public void A_point_with_no_window_under_it_is_refused()
    {
        var reason = DesktopAutomation.Occlusion(
            ActionKind.LeftClick, Params(10, 10), Target, (_, _) => new PointTarget(0, 0, string.Empty, 0));

        Assert.NotNull(reason);
        Assert.Contains("没有窗口", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_point_that_left_the_window_is_no_longer_inside_it()
    {
        // Checked after a raise: focusing can restore or move a window, and a stale coordinate is worse than a
        // refusal.
        var rect = new PixelRect(100, 100, 200, 200);

        Assert.True(DesktopAutomation.PointsInside(ActionKind.LeftClick, Params(150, 150), rect));
        Assert.False(DesktopAutomation.PointsInside(ActionKind.LeftClick, Params(350, 150), rect));
        Assert.False(DesktopAutomation.PointsInside(ActionKind.Drag, Params(150, 150, 350, 150), rect));
        Assert.True(DesktopAutomation.PointsInside(ActionKind.Drag, Params(150, 150, 250, 250), rect));
    }

    [Fact]
    public void Kinds_without_a_point_have_nothing_to_contain()
    {
        // MovePointer has no window to deliver to, and Focus carries no coordinate at all.
        Assert.True(DesktopAutomation.PointsInside(ActionKind.Focus, Params(0, 0), new PixelRect(0, 0, 0, 0)));
        Assert.True(DesktopAutomation.PointsInside(ActionKind.MovePointer, Params(9_999, 9_999), new PixelRect(0, 0, 10, 10)));
    }

    [Fact]
    public void Focus_carries_no_point_and_is_never_refused()
    {
        // Focus is how a covered target becomes reachable again; refusing it would leave no way out.
        Assert.Null(DesktopAutomation.Occlusion(
            ActionKind.Focus, Params(0, 0), Target, At(0x9999, 7, "Partner")));
    }
}
