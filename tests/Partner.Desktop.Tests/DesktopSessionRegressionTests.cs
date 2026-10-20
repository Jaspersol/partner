using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// The four fixes cut from a real session's timeline (17 steps / 328 s, tools 1.77 s): a blind Electron
/// window that said nothing about the escape hatch, a scroll refused for missing coordinates it never
/// needed, aim-only kinds reporting "画面无变化" on actions that promise no picture, and an
/// out-of-rectangle refusal that made the model spend a turn re-observing to find what was nearby.
/// </summary>
public class DesktopSessionRegressionTests
{

    [Fact]
    public void A_window_with_almost_no_elements_gets_the_debug_port_hint()
    {
        var observation = ObservationWith(("280012", "网易云音乐"), elements: 1);
        var note = DesktopObserveTool.BlindWindows(observation);

        Assert.Contains("网易云音乐", note);
        Assert.Contains("--remote-debugging-port=9222", note);
    }

    [Fact]
    public void A_window_with_real_controls_gets_no_hint()
    {
        var observation = ObservationWith(("280012", "播放器"), elements: 5);

        Assert.Equal(string.Empty, DesktopObserveTool.BlindWindows(observation));
    }


    [Fact]
    public void Scroll_with_the_window_centre_passes_the_guard()
    {
        var entity = Entity(100, 200, 800, 600);
        var centre = (entity.Rect.X + entity.Rect.Width / 2, entity.Rect.Y + entity.Rect.Height / 2);

        Assert.Null(DesktopActTool.Guard(ActionKind.Scroll, entity, centre, ScrollArgs()));
    }

    [Fact]
    public void Scroll_still_refused_without_a_point_when_not_defaulted()
    {
        // The tool defaults the point before the guard; the guard's own contract is unchanged and stays
        // pinned so a future edit cannot make "no point anywhere" silently click (0,0).
        Assert.NotNull(DesktopActTool.Guard(ActionKind.Scroll, Entity(), null, ScrollArgs()));
    }


    [Theory]
    [InlineData(ActionKind.Focus)]
    [InlineData(ActionKind.MovePointer)]
    [InlineData(ActionKind.ScrollIntoView)]
    public void Aim_only_kinds_do_not_need_verification(ActionKind kind) =>
        Assert.False(DesktopAutomation.NeedsVerification(kind));

    [Theory]
    [InlineData(ActionKind.LeftClick)]
    [InlineData(ActionKind.KeyPress)]
    [InlineData(ActionKind.Scroll)]
    [InlineData(ActionKind.TypeText)]
    public void Picture_changing_kinds_still_verify(ActionKind kind) =>
        Assert.True(DesktopAutomation.NeedsVerification(kind));


    [Fact]
    public void Nearest_control_finds_the_one_closest_to_a_refused_point()
    {
        var fresh = ElementsIn("ff",
            NamedElement("ff:0", "播放", 200, 300),
            NamedElement("ff:1", "搜索", 700, 300));

        var near = DesktopActTool.NearestControl(fresh, "ff", (720, 310));

        Assert.NotNull(near);
        Assert.Equal("搜索", near!.Value.Element.Name);
    }

    [Fact]
    public void Nearest_control_skips_document_and_offscreen()
    {
        var fresh = ElementsIn("ff",
            NamedElement("ff:0", "正文", 10, 10, role: "Document", offscreen: false),
            NamedElement("ff:1", "横向远控件", 20, 20, offscreen: true));

        Assert.Null(DesktopActTool.NearestControl(fresh, "ff", (15, 15)));
    }

    [Fact]
    public void Nearest_control_returns_null_when_the_window_has_none()
    {
        var fresh = ElementsIn("ff");

        Assert.Null(DesktopActTool.NearestControl(fresh, "ff", (10, 10)));
    }


    private static DesktopActTool.DesktopActArguments ScrollArgs() => new(
        null, null, "Scroll", null, null, null, null, null, null, 3);

    private static UiEntity Entity(int x = 0, int y = 0, int w = 800, int h = 600, string id = "ff", string title = "窗") => new()
    {
        EntityId = id,
        Generation = "g",
        Role = "window",
        Label = title,
        Value = "proc",
        Rect = new PixelRect(x, y, w, h),
        IdentityDigest = id,
        LayoutDigest = id,
        ObservedAtMs = DateTimeOffset.UtcNow,
        Confidence = 1.0,
        ProcessId = 4242,
        ProcessStartTicks = 0,
    };

    private static UiElement NamedElement(
        string id,
        string name,
        int x,
        int y,
        string role = "Button",
        bool offscreen = false) => new(
        id, WindowIdOf(id), name, role, new PixelRect(x, y, 60, 30), IdentityDigest: $"{id}:{name}")
        {
            IsOffscreen = offscreen,
        };

    private static string WindowIdOf(string elementId) =>
        elementId[..elementId.IndexOf(':')];

    private static DesktopObservation ObservationWith((string Id, string Title) window, int elements)
    {
        var entity = Entity(0, 0, 800, 600, window.Id, window.Title);
        var list = Enumerable.Range(0, elements)
            .Select(i => NamedElement($"{entity.EntityId}:{i}", $"控件{i}", i * 50, 0))
            .ToList();

        return new DesktopObservation
        {
            ObsId = "obs-test",
            TargetKey = "desktop",
            Timestamp = DateTimeOffset.UtcNow,
            Sources = new[] { "uia.test" },
            Entities = new[] { entity },
            Elements = list,
        };
    }

    private static DesktopObservation ElementsIn(string windowId = "ff", params UiElement[] elements) => new()
    {
        ObsId = "obs-test",
        TargetKey = "desktop",
        Timestamp = DateTimeOffset.UtcNow,
        Sources = new[] { "uia.test" },
        Entities = new[] { Entity(id: windowId) },
        Elements = elements,
    };
}
