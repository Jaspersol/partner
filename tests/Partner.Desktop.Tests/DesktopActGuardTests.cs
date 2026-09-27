using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// The checks that stand between a model's decision and the user's mouse: is this still the window (or the
/// control) that was observed, and would this action land where it claims to.
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

    private static UiElement Element(
        string id = "1a2b:0",
        string name = "播放",
        string role = "Button",
        string? digest = null) => new(
        ElementId: id,
        WindowId: "1a2b",
        Name: name,
        Role: role,
        Rect: new PixelRect(150, 150, 100, 40),
        Digest: digest ?? "element-a");

    private static DesktopObservation Observation(params UiEntity[] entities) => new()
    {
        ObsId = "obs-2",
        TargetKey = "desktop",
        Timestamp = DateTimeOffset.UnixEpoch,
        Sources = new[] { "test" },
        Entities = entities,
    };

    private static DesktopObservation WithElements(IReadOnlyList<UiEntity> entities, IReadOnlyList<UiElement> elements) => new()
    {
        ObsId = "obs-2",
        TargetKey = "desktop",
        Timestamp = DateTimeOffset.UnixEpoch,
        Sources = new[] { "test" },
        Entities = entities,
        Elements = elements,
    };

    /// <summary>Named so the tests read as the action they describe, not as a ten-argument record.</summary>
    private static DesktopActTool.DesktopActArguments Args(
        string kind,
        int x = 0,
        int y = 0,
        ushort key = 0,
        string? text = null,
        int delta = 0,
        int toX = 0,
        int toY = 0) => new(null, null, kind, x, y, toX, toY, key, text, delta);

    [Fact]
    public void The_same_window_in_a_fresh_observation_is_accepted()
    {
        // This is what makes a slow approval workable: the lease from the model's observation is long dead by
        // the time a human answers, but the window is unchanged — so the decision is still about this window.
        Assert.Null(DesktopActTool.Revalidate(Entity(), Observation(Entity())));
    }

    [Fact]
    public void A_window_that_moved_or_changed_is_refused()
    {
        Assert.Contains(
            "不同",
            DesktopActTool.Revalidate(Entity(), Observation(Entity(digest: "digest-b")))!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_window_that_is_gone_is_refused()
    {
        Assert.Contains(
            "已经不在了",
            DesktopActTool.Revalidate(Entity(), Observation(Entity(id: "other")))!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_control_in_a_fresh_observation_is_accepted()
    {
        // Discover-then-act: the model named a control, and the control is still the same one.
        Assert.Null(DesktopActTool.RevalidateElement(
            Element(),
            WithElements(new[] { Entity() }, new[] { Element() })));
    }

    [Fact]
    public void A_control_that_moved_is_refused()
    {
        // The layout changed, so the click point the model saw is no longer where that control is.
        Assert.Contains(
            "不同",
            DesktopActTool.RevalidateElement(
                Element(),
                WithElements(new[] { Entity() }, new[] { Element(digest: "element-b") }))!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_control_that_is_gone_is_refused()
    {
        Assert.Contains(
            "已经不在了",
            DesktopActTool.RevalidateElement(
                Element(),
                WithElements(new[] { Entity() }, new[] { Element(id: "1a2b:7") }))!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_keystroke_is_refused_while_the_target_is_not_in_front()
    {
        // Keystrokes are global: without this the model types into whatever the user was using — which is
        // exactly what a "press C in 网易云音乐" looked like while 网易云音乐 was not even open.
        var blocked = DesktopActTool.Guard(ActionKind.KeyPress, Entity(foreground: false), (0, 0), Args("KeyPress", key: 67));

        Assert.NotNull(blocked);
        Assert.Contains("前台", blocked, StringComparison.Ordinal);
    }

    [Fact]
    public void A_keystroke_is_allowed_once_the_target_has_focus()
    {
        Assert.Null(DesktopActTool.Guard(ActionKind.KeyPress, Entity(foreground: true), (0, 0), Args("KeyPress", key: 67)));
    }

    [Fact]
    public void Typing_is_refused_while_the_target_is_not_in_front()
    {
        // Same rule as a keystroke: typed text goes to whatever has focus.
        Assert.NotNull(DesktopActTool.Guard(
            ActionKind.TypeText, Entity(foreground: false), (0, 0), Args("TypeText", text: "你好")));
    }

    [Fact]
    public void A_click_outside_the_target_window_is_refused()
    {
        // The coordinates are screen coordinates: outside the rect, the click belongs to some other window.
        var blocked = DesktopActTool.Guard(ActionKind.LeftClick, Entity(), (900, 900), Args("LeftClick", 900, 900));

        Assert.NotNull(blocked);
        Assert.Contains("矩形", blocked, StringComparison.Ordinal);
    }

    [Fact]
    public void A_click_inside_the_target_window_is_allowed()
    {
        Assert.Null(DesktopActTool.Guard(ActionKind.LeftClick, Entity(), (200, 200), Args("LeftClick", 200, 200)));
    }

    [Fact]
    public void A_drag_that_leaves_the_window_is_refused()
    {
        // Both ends are checked: a drag that starts inside and releases outside drops something somewhere else.
        Assert.NotNull(DesktopActTool.Guard(
            ActionKind.Drag, Entity(), (200, 200), Args("Drag", 200, 200, toX: 900, toY: 900)));

        Assert.Null(DesktopActTool.Guard(
            ActionKind.Drag, Entity(), (200, 200), Args("Drag", 200, 200, toX: 300, toY: 300)));
    }

    [Fact]
    public void Typing_needs_text_and_scrolling_needs_a_delta()
    {
        // The two kinds carry their payload in the arguments; an empty one would be a silent no-op.
        Assert.NotNull(DesktopActTool.Guard(ActionKind.TypeText, Entity(foreground: true), (200, 200), Args("TypeText")));
        Assert.NotNull(DesktopActTool.Guard(ActionKind.Scroll, Entity(foreground: true), (200, 200), Args("Scroll")));

        Assert.Null(DesktopActTool.Guard(
            ActionKind.TypeText, Entity(foreground: true), (200, 200), Args("TypeText", text: "你好")));
    }

    [Fact]
    public void Focus_is_never_blocked_by_geometry()
    {
        // Focus is how a window becomes the target in the first place; it has no coordinates to check.
        Assert.Null(DesktopActTool.Guard(ActionKind.Focus, Entity(foreground: false), (0, 0), Args("Focus")));
    }

    [Fact]
    public void The_danger_classification_is_about_leaving_the_gui()
    {
        // The middle gear lets clicking and typing through: a person can see those happen. What it stops is
        // everything that reaches past the window — a command, a launch, a clipboard write.
        Assert.True(DesktopToolCatalog.IsDangerous("desktop_exec"));
        Assert.True(DesktopToolCatalog.IsDangerous("desktop_launch"));
        Assert.True(DesktopToolCatalog.IsDangerous("desktop_clipboard_set"));

        Assert.False(DesktopToolCatalog.IsDangerous("desktop_observe"));
        Assert.False(DesktopToolCatalog.IsDangerous("desktop_screenshot"));
        Assert.False(DesktopToolCatalog.IsDangerous("desktop_act"));
        Assert.False(DesktopToolCatalog.IsDangerous("desktop_clipboard_get"));

        // Looking — including taking a picture — never asks.
        Assert.True(DesktopToolCatalog.IsReadOnly("desktop_screenshot"));

        // Every tool is in the catalog: the host walks this list to decide what to allow, so a tool missing
        // from it would never be allowed to run.
        Assert.Contains("desktop_act", DesktopToolCatalog.All);
        Assert.Contains("desktop_exec", DesktopToolCatalog.All);
        Assert.Equal(7, DesktopToolCatalog.All.Count);
    }
}
