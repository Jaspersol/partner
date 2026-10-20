using Partner.Core.Media;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// The snap: a point read off a picture — or guessed by a model — may be a couple of pixels off. When no
/// control was named, a point inside a control's neighbourhood is upgraded to that control's centre, where
/// the pattern and the verification know exactly what they are acting on. Beyond the radius, the point stays
/// the point: snapping to what the model did not aim at would be a different click.
/// </summary>
public sealed class PictureSnapTests
{
    private static UiElement Element(
        string id,
        string name = "播放",
        string role = "Button",
        int x = 150,
        int y = 150) => new(
        ElementId: id,
        WindowId: "1a2b",
        Name: name,
        Role: role,
        Rect: new PixelRect(x, y, 100, 40),
        IdentityDigest: $"digest-{id}");

    private static DesktopObservation Observation(params UiElement[] elements) => new()
    {
        ObsId = "obs-1",
        TargetKey = "desktop",
        Timestamp = DateTimeOffset.UnixEpoch,
        Sources = new[] { "test" },
        Entities = new[]
        {
            new UiEntity
            {
                EntityId = "1a2b",
                Generation = "obs-1",
                Role = "window",
                Label = "播放器",
                Value = "player",
                Rect = new PixelRect(100, 100, 500, 400),
                Confidence = 1.0,
                IdentityDigest = "digest-a",
                LayoutDigest = "layout-a",
                ProcessId = 4242,
                ObservedAtMs = DateTimeOffset.UnixEpoch,
            },
        },
        Elements = elements,
    };

    [Fact]
    public void The_nearest_clickable_control_inside_the_radius_is_the_snap()
    {
        var observation = Observation(Element("1a2b:0", x: 200, y: 200)); // centre (250,220)

        var snapped = DesktopActTool.SnapToElement(observation, "1a2b", (254, 222));

        Assert.NotNull(snapped);
        Assert.Equal("1a2b:0", snapped!.ElementId);
    }

    [Fact]
    public void A_point_beyond_the_radius_stays_a_point()
    {
        var observation = Observation(Element("1a2b:0", x: 200, y: 200)); // centre (250,220)

        Assert.Null(DesktopActTool.SnapToElement(observation, "1a2b", (270, 240)));
        Assert.Null(DesktopActTool.SnapToElement(observation, "1a2b", (400, 400)));
    }

    [Fact]
    public void The_neighbour_wins_when_two_controls_are_equally_near()
    {
        var observation = Observation(
            Element("1a2b:1", name: "靠右", role: "Button", x: 200, y: 200),   // centre (250,220)
            Element("1a2b:0", name: "靠左", role: "Button", x: 102, y: 202)); // centre (152,222)

        // Both within the radius of (152,221); the lower distance wins, and an exact tie falls to the
        // stable id, so the model gets the same answer twice for the same picture.
        var snapped = DesktopActTool.SnapToElement(observation, "1a2b", (152, 221));

        Assert.NotNull(snapped);
        Assert.Equal("1a2b:0", snapped!.ElementId);
    }

    [Fact]
    public void Static_text_and_blank_rects_are_not_snapped_to()
    {
        // A Document's rectangle is the whole content area: "snap to its centre" is "click the middle of
        // the document", a click nobody asked for.
        var observation = Observation(
            Element("1a2b:0", name: "文档", role: "Document", x: 100, y: 100));

        Assert.Null(DesktopActTool.SnapToElement(observation, "1a2b", (150, 170)));
    }

    [Fact]
    public void Another_windows_control_is_never_a_snap_target()
    {
        // The point resolves to a window first; the snap only looks inside that window. A control eight
        // pixels across a window border belongs to the other window's click semantics.
        var observation = new DesktopObservation
        {
            ObsId = "obs-1",
            TargetKey = "desktop",
            Timestamp = DateTimeOffset.UnixEpoch,
            Sources = new[] { "test" },
            Entities = Array.Empty<UiEntity>(),
            Elements = new[]
            {
                new UiElement("other:0", "other", "按钮", "Button", new PixelRect(100, 100, 200, 60), "d-o"),
            },
        };

        Assert.Null(DesktopActTool.SnapToElement(observation, "1a2b", (150, 130)));
    }

    [Fact]
    public void Offscreen_controls_are_not_snapped_to()
    {
        var observation = Observation(new UiElement(
            "1a2b:0", "1a2b", "播放", "Button", new PixelRect(150, 150, 100, 40),
            IdentityDigest: "digest-0", IsOffscreen: true));

        Assert.Null(DesktopActTool.SnapToElement(observation, "1a2b", (200, 170)));
    }

}
