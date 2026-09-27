using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// The two checks that stand between a model's decision and the user's mouse: is this still the window that
/// was observed, and would this action land where it claims to.
/// </summary>
public sealed class DesktopActGuardTests
{
    private static UiEntity Entity(
        string id = "1a2b",
        string label = "网易云音乐",
        string process = "cloudmusic",
        PixelRect? rect = null,
        bool foreground = false,
        string? digest = null) => new()
        {
            EntityId = id,
            Generation = "obs-1",
            Role = "window",
            Label = label,
            Value = process,
            Rect = rect ?? new PixelRect(100, 100, 400, 300),
            Confidence = 1.0,
            EvidenceDigest = digest ?? "digest-a",
            ObservedAtMs = DateTimeOffset.UnixEpoch,
            IsForeground = foreground,
        };

    private static DesktopObservation Observation(params UiEntity[] entities) => new()
    {
        ObsId = "obs-2",
        TargetKey = "desktop",
        Timestamp = DateTimeOffset.UnixEpoch,
        Sources = new[] { "test" },
        Entities = entities,
    };

    [Fact]
    public void The_same_window_in_a_fresh_observation_is_accepted()
    {
        // This is what makes a slow approval workable: the lease from the model's observation is long dead by
        // the time a human answers, but the window is unchanged — so the decision is still about this window.
        var seen = Entity();

        Assert.Null(DesktopActTool.Revalidate(seen, Observation(Entity())));
    }

    [Fact]
    public void A_window_that_moved_or_changed_is_refused()
    {
        var seen = Entity();

        Assert.Contains(
            "不同",
            DesktopActTool.Revalidate(seen, Observation(Entity(digest: "digest-b")))!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_window_that_is_gone_is_refused()
    {
        var seen = Entity();

        Assert.Contains(
            "已经不在了",
            DesktopActTool.Revalidate(seen, Observation(Entity(id: "other")))!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_keystroke_is_refused_while_the_target_is_not_in_front()
    {
        // Keystrokes are global: without this the model types into whatever the user was using — which is
        // exactly what a "press C in 网易云音乐" looked like while 网易云音乐 was not even open.
        var blocked = DesktopActTool.Guard(
            ActionKind.KeyPress,
            Entity(foreground: false),
            new DesktopActTool.DesktopActArguments("1a2b", "KeyPress", 0, 0, 67));

        Assert.NotNull(blocked);
        Assert.Contains("前台", blocked, StringComparison.Ordinal);
    }

    [Fact]
    public void A_keystroke_is_allowed_once_the_target_has_focus()
    {
        Assert.Null(DesktopActTool.Guard(
            ActionKind.KeyPress,
            Entity(foreground: true),
            new DesktopActTool.DesktopActArguments("1a2b", "KeyPress", 0, 0, 67)));
    }

    [Fact]
    public void A_click_outside_the_target_window_is_refused()
    {
        // The coordinates are screen coordinates: outside the rect, the click belongs to some other window.
        var blocked = DesktopActTool.Guard(
            ActionKind.LeftClick,
            Entity(),
            new DesktopActTool.DesktopActArguments("1a2b", "LeftClick", 900, 900, 0));

        Assert.NotNull(blocked);
        Assert.Contains("矩形", blocked, StringComparison.Ordinal);
    }

    [Fact]
    public void A_click_inside_the_target_window_is_allowed()
    {
        Assert.Null(DesktopActTool.Guard(
            ActionKind.LeftClick,
            Entity(),
            new DesktopActTool.DesktopActArguments("1a2b", "LeftClick", 200, 200, 0)));
    }

    [Fact]
    public void Focus_is_never_blocked_by_geometry()
    {
        // Focus is how a window becomes the target in the first place; it has no coordinates to check.
        Assert.Null(DesktopActTool.Guard(
            ActionKind.Focus,
            Entity(foreground: false),
            new DesktopActTool.DesktopActArguments("1a2b", "Focus", 0, 0, 0)));
    }
}
