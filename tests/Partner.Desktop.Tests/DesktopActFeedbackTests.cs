using System.Drawing;
using Partner.Core.Media;
using Partner.Desktop.Capture;
using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// The state an action leaves behind, as its own answer sees it.
/// <para>
/// An action used to answer in text only, so the model spent a second tool call — a whole extra model
/// round trip — just to find out what the click did. The picture now rides along. What has to hold: the
/// marking matches what the model can address by id next, every no-picture case is a deliberate one
/// rather than a coincidence, and a failed render must not fail an action that already succeeded.
/// </para>
/// </summary>
public sealed class DesktopActFeedbackTests
{
    // ---- the decision matrix -------------------------------------------------------------------

    [Theory]
    [InlineData(ActionKind.Focus)]
    [InlineData(ActionKind.MovePointer)]
    [InlineData(ActionKind.ScrollIntoView)]
    public void A_kind_without_a_visual_consequence_carries_no_picture(ActionKind kind)
    {
        // Focus/ScrollIntoView exist to prepare an aim, and MovePointer only places the cursor: attaching a
        // picture there would pay a model round trip of tokens for a screen the next action re-shows.
        Assert.False(DesktopActTool.WantsPicture(kind, "auto", supportsImages: true, guardPassed: true));
    }

    [Fact]
    public void A_refusal_injects_nothing_so_it_shows_nothing_new()
    {
        Assert.False(DesktopActTool.WantsPicture(ActionKind.LeftClick, "auto", supportsImages: true, guardPassed: false));
    }

    [Fact]
    public void A_caller_who_said_off_gets_no_picture()
    {
        Assert.False(DesktopActTool.WantsPicture(ActionKind.LeftClick, "off", supportsImages: true, guardPassed: true));
    }

    [Fact]
    public void A_text_only_model_gets_no_picture()
    {
        // Sending one to a text-only model fails the request — the same reason the screenshot tool is not
        // registered for it. The act tool has to answer with the same honesty.
        Assert.False(DesktopActTool.WantsPicture(ActionKind.LeftClick, "auto", supportsImages: false, guardPassed: true));
    }

    [Fact]
    public void The_visual_kinds_ask_for_a_picture_by_default()
    {
        foreach (var kind in new[]
                 {
                     ActionKind.LeftClick, ActionKind.RightClick, ActionKind.DoubleClick,
                     ActionKind.KeyPress, ActionKind.TypeText, ActionKind.Scroll, ActionKind.Drag,
                 })
        {
            Assert.True(DesktopActTool.WantsPicture(kind, null, supportsImages: true, guardPassed: true), kind.ToString());
        }
    }

    // ---- the render, end to end but with a stub capture ---------------------------------------

    /// <summary>
    /// The picture the act tool attaches is a real PNG, stored under a media id the server can map — and its
    /// marks come from the <b>fresh</b> observation the act re-checked against, so what is labelled is what
    /// the model can address next. A stub capture keeps the user's desktop out of it.
    /// </summary>
    [Fact]
    public async Task The_post_action_picture_is_a_mappable_png_recorded_under_its_id()
    {
        var workspace = Directory.CreateTempSubdirectory("partner-act-feedback-");
        try
        {
            const int width = 320;
            const int height = 200;
            var screenRect = new PixelRect(50, 60, width, height);

            var capture = new StubCapture(screenRect);
            using var automation = new DesktopAutomation(capture: capture);
            var media = new FileMediaStore(workspace.FullName);

            var target = new UiEntity
            {
                EntityId = "1a2b",
                Generation = "obs-1",
                Role = "window",
                Label = "测试窗口",
                Value = "test",
                Rect = screenRect,
                Confidence = 1.0,
                IdentityDigest = "digest-a",
                LayoutDigest = DesktopObservation.Layout(screenRect),
                ProcessId = 4242,
                ObservedAtMs = DateTimeOffset.UnixEpoch,
            };

            var picture = await DesktopScreenshotTool.RenderAfterActionAsync(
                automation, media, target,
                new List<UiElement> { new("e-1", "1a2b", "播放", "Button", new PixelRect(60, 70, 80, 30), "element-a") },
                obsId: "obs-1",
                ct: CancellationToken.None);

            Assert.NotNull(picture);
            Assert.StartsWith("data:image/png;base64,", picture!.DataUri);
            Assert.Equal(width, picture.Width);
            Assert.Equal(height, picture.Height);
            Assert.Equal(screenRect, picture.ScreenRect);

            // The bookkeeping the explicit screenshot tool does: px/py on this picture resolves through the
            // server, and a bare point on it resolves to this window.
            var mapped = automation.PictureOf(picture.MediaId);
            Assert.NotNull(mapped);
            Assert.Equal(screenRect, mapped!.ScreenRect);
            Assert.Equal("obs-1", mapped.ObsId);
            Assert.Equal("1a2b", automation.LastShownEntityId);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    /// <summary>A render that cannot produce anything must come out as none, not as a failure: the action
    /// already succeeded, and its text result stands on its own.</summary>
    [Fact]
    public async Task A_failed_capture_degrades_to_no_picture_rather_than_failing_the_action()
    {
        var workspace = Directory.CreateTempSubdirectory("partner-act-fail-");
        try
        {
            using var automation = new DesktopAutomation(capture: new StubCapture(null));
            var target = new UiEntity
            {
                EntityId = "1a2b",
                Generation = "obs-1",
                Role = "window",
                Label = "测试窗口",
                Value = "test",
                Rect = new PixelRect(10, 10, 200, 100),
                Confidence = 1.0,
                IdentityDigest = "digest-a",
                LayoutDigest = "layout-a",
                ProcessId = 4242,
                ObservedAtMs = DateTimeOffset.UnixEpoch,
            };

            var picture = await DesktopScreenshotTool.RenderAfterActionAsync(
                automation, new FileMediaStore(workspace.FullName), target,
                Array.Empty<UiElement>(), obsId: "obs-1", ct: CancellationToken.None);

            Assert.Null(picture);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    /// <summary>The minimized stub rectangle has no pixels to show, so it answers none — the action's text
    /// already tells the model to focus first.</summary>
    [Fact]
    public async Task A_minimized_window_is_not_photographed()
    {
        var workspace = Directory.CreateTempSubdirectory("partner-act-minimized-");
        try
        {
            using var automation = new DesktopAutomation(capture: new StubCapture(new PixelRect(0, 0, 10, 10)));

            // A real minimized window's stub, exactly what observation reports for one.
            var minimized = new UiEntity
            {
                EntityId = "1a2b",
                Generation = "obs-1",
                Role = "window",
                Label = "测试窗口",
                Value = "test",
                Rect = new PixelRect(-32000, -32000, 160, 28),
                Confidence = 1.0,
                IdentityDigest = "digest-a",
                LayoutDigest = "layout-a",
                ProcessId = 4242,
                ObservedAtMs = DateTimeOffset.UnixEpoch,
                IsMinimized = true,
            };

            var picture = await DesktopScreenshotTool.RenderAfterActionAsync(
                automation, new FileMediaStore(workspace.FullName), minimized,
                Array.Empty<UiElement>(), obsId: "obs-1", ct: CancellationToken.None);

            Assert.Null(picture);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
}

/// <summary>A capture stub: one positioned frame for every window, or a hard failure — so tests drive the
/// render without touching the real screen.</summary>
file sealed class StubCapture(PixelRect? at) : IScreenCapture
{
    public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default) => Task.FromResult(
        at is { } rect
            ? new CaptureResult
            {
                Ok = true,
                Width = rect.Width,
                Height = rect.Height,
                Bgra = new byte[rect.Width * rect.Height * 4],
                Method = "test.stub",
                ScreenRect = rect,
            }
            : CaptureResult.Failed("test.stub", "injected failure"));

    public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int width, int height, CancellationToken ct = default) =>
        CaptureWindowAsync(0, ct);
}
