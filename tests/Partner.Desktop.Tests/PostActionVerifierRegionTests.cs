using Partner.Desktop.Capture;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tests;

/// <summary>
/// Judging one region of a window instead of the whole of it.
/// <para>
/// The decision is a fraction of the judged area, so the region is the resolution of the whole mechanism: a
/// small control inside a large window produces a change too small to clear a whole-window threshold, and a
/// click that demonstrably worked comes back as "not confirmed". These tests hold both sides of that — the
/// button that changed is confirmed, and a change somewhere else in the window still is not.
/// </para>
/// </summary>
public sealed class PostActionVerifierRegionTests
{
    /// <summary>
    /// A window big enough for the arithmetic to bite: 0.5% of 800×600 is 2400 pixels, and a 50×30 button
    /// changing is 1500. Judged against the window that reads as nothing at all.
    /// </summary>
    private const int Window = 800;
    private const int WindowHeight = 600;
    private const int ButtonWidth = 50;
    private const int ButtonHeight = 30;

    private static readonly PixelRect ButtonAt = new(300, 200, ButtonWidth, ButtonHeight);

    /// <summary>Where the test's window sits on the screen — the origin every screen region is mapped through.</summary>
    private static readonly PixelRect WindowOnScreen = new(752, 320, Window, WindowHeight);

    [Fact]
    public async Task A_button_that_changed_is_confirmed_by_its_own_rectangle()
    {
        var verifier = new PostActionVerifier(Capture(
            Frame(Window, WindowHeight, (ButtonAt.X, ButtonAt.Y, ButtonWidth, ButtonHeight))));

        var result = await verifier.VerifyScreenRegionAsync(1, ButtonOnScreen, Before());

        Assert.Equal(VerifyDecision.AnyChange, result.Decision);
        Assert.Equal(1, result.IntersectedAreaRatio);
    }

    [Fact]
    public async Task The_same_click_judged_against_the_whole_window_would_have_read_as_no_change()
    {
        // The measurement the region fixes. Same two frames, same threshold, only the judged area differs —
        // so the old verdict is not an opinion, it is arithmetic.
        var changed = Frame(Window, WindowHeight, (ButtonAt.X, ButtonAt.Y, ButtonWidth, ButtonHeight));
        var buttonPixels = (double)ButtonWidth * ButtonHeight;
        var windowPixels = (double)Window * WindowHeight;

        Assert.True(buttonPixels / windowPixels < PostActionVerifier.MinIntersectedAreaRatio);
        Assert.True(buttonPixels / buttonPixels >= PostActionVerifier.MinIntersectedAreaRatio);

        var wholeWindow = await new PostActionVerifier(Capture(changed)).VerifyAsync(1, WholeFrame(), Before());
        var namedControl = await new PostActionVerifier(Capture(changed)).VerifyScreenRegionAsync(1, ButtonOnScreen, Before());

        Assert.Equal(VerifyDecision.NoChange, wholeWindow.Decision);
        Assert.Equal(VerifyDecision.AnyChange, namedControl.Decision);
    }

    [Fact]
    public async Task A_change_elsewhere_in_the_window_is_still_no_change_for_a_named_control()
    {
        // The other direction: narrowing the region must not turn every action into a confirmation. A repaint
        // far from the control the model named is not evidence that the control was pressed.
        var verifier = new PostActionVerifier(Capture(Frame(Window, WindowHeight, (700, 500, 40, 40))));

        var result = await verifier.VerifyScreenRegionAsync(1, ButtonOnScreen, Before());

        Assert.Equal(VerifyDecision.NoChange, result.Decision);
        Assert.Equal(0, result.IntersectedAreaRatio);
    }

    [Fact]
    public async Task A_screen_region_is_mapped_through_the_frames_own_origin()
    {
        // Element rectangles are screen coordinates; frames are cropped to the window's corner. A window at
        // (752,320) with a control at screen (800,400) sits at frame (48,80) — passing the screen rectangle
        // unmapped would judge a rectangle that is not over the control at all.
        var screenRect = WindowOnScreen;
        var buttonOnScreen = ButtonOnScreen;

        var verifier = new PostActionVerifier(Capture(
            Frame(Window, WindowHeight, (ButtonAt.X, ButtonAt.Y, ButtonWidth, ButtonHeight)),
            screenRect));

        var result = await verifier.VerifyScreenRegionAsync(1, buttonOnScreen, Before(screenRect));

        Assert.Equal(VerifyDecision.AnyChange, result.Decision);
    }

    [Fact]
    public async Task A_capture_that_does_not_know_where_on_the_screen_it_came_from_judges_the_whole_frame()
    {
        // There is no honest way to map a screen coordinate onto a frame with no origin, and guessing one
        // would judge a rectangle that is not where the control is. The whole frame is the honest fallback.
        var verifier = new PostActionVerifier(Capture(
            Frame(Window, WindowHeight, (ButtonAt.X, ButtonAt.Y, ButtonWidth, ButtonHeight))));

        var result = await verifier.VerifyScreenRegionAsync(1, ButtonOnScreen, Before(withoutOrigin: true));

        Assert.Equal(VerifyDecision.NoChange, result.Decision);
        Assert.Contains("dirty region intersects only", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unchanged_control_is_no_change_even_at_window_scale()
    {
        var verifier = new PostActionVerifier(Capture(Frame(Window, WindowHeight)));

        var result = await verifier.VerifyScreenRegionAsync(1, ButtonOnScreen, Before());

        Assert.Equal(VerifyDecision.NoChange, result.Decision);
        Assert.Contains("no dirty region", result.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, 100, 50, 0, 0, 0, 0)]
    [InlineData(752, 320, 100, 50, 752, 320, 0, 0)]
    [InlineData(900, 500, 60, 30, 800, 400, 100, 100)]
    public void A_screen_region_lands_where_the_control_actually_is(
        int regionX,
        int regionY,
        int regionW,
        int regionH,
        int originX,
        int originY,
        int expectedX,
        int expectedY)
    {
        var mapped = PostActionVerifier.Region(
            frameRegion: null,
            screenRegion: new PixelRect(regionX, regionY, regionW, regionH),
            frameScreenRect: new PixelRect(originX, originY, 800, 600));

        Assert.NotNull(mapped);
        Assert.Equal(new PixelRect(expectedX, expectedY, regionW, regionH), mapped!);
    }

    [Fact]
    public void A_region_given_in_frame_coordinates_is_taken_as_it_is()
    {
        var given = new PixelRect(3, 4, 5, 6);

        Assert.Equal(given, PostActionVerifier.Region(given, null, new PixelRect(100, 100, 800, 600)));
    }

    [Fact]
    public void A_region_that_cannot_be_placed_is_null_rather_than_a_guess()
    {
        Assert.Null(PostActionVerifier.Region(null, new PixelRect(1, 2, 3, 4), null));
        Assert.Null(PostActionVerifier.Region(null, null, new PixelRect(0, 0, 800, 600)));
        Assert.Null(PostActionVerifier.Region(null, null, null));
    }

    private static PixelRect WholeFrame() => new(0, 0, Window, WindowHeight);

    /// <summary>
    /// The same verdict, obtained by copying the button's own pixels instead of re-rendering the window.
    /// <para>
    /// PrintWindow renders the whole window through DWM and is the most expensive call on the action path, and a
    /// click changes a rectangle the size of a control. This is the cheap route — but only the decision has to
    /// match: it is a different set of pixels, and if the verdict differed between the two that would be a
    /// measurement bug wearing a performance win.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Capturing_only_the_region_reaches_the_same_verdict_without_rendering_the_window()
    {
        var changed = Frame(Window, WindowHeight, (ButtonAt.X, ButtonAt.Y, ButtonWidth, ButtonHeight));

        var capture = new FixedCapture(changed, Window, WindowHeight, WindowOnScreen);
        var verifier = new PostActionVerifier(capture);

        var result = await verifier.VerifyScreenRegionAsync(1, ButtonOnScreen, Before(), screenCaptureOk: true);

        Assert.Equal(VerifyDecision.AnyChange, result.Decision);
        Assert.Equal("test.region", result.Source);
        Assert.Equal(1, capture.RegionCaptures);
        Assert.Equal(0, capture.WindowCaptures);
    }

    [Fact]
    public async Task An_unchanged_control_is_still_no_change_when_only_the_region_is_copied()
    {
        var capture = new FixedCapture(Frame(Window, WindowHeight), Window, WindowHeight, WindowOnScreen);

        var result = await new PostActionVerifier(capture)
            .VerifyScreenRegionAsync(1, ButtonOnScreen, Before(), screenCaptureOk: true);

        Assert.Equal(VerifyDecision.NoChange, result.Decision);
        Assert.Equal(1, capture.RegionCaptures);
    }

    [Fact]
    public async Task A_change_outside_the_copied_region_is_not_seen()
    {
        // The flip side, and the reason the cheap path is gated on the window being in front: the copy is what
        // is at those pixels, so anything outside them is genuinely not part of the measurement. Judging the
        // button alone is the point; it is only honest when nothing else can be drawn there.
        var capture = new FixedCapture(
            Frame(Window, WindowHeight, (700, 500, 40, 40)),
            Window,
            WindowHeight,
            WindowOnScreen);

        var result = await new PostActionVerifier(capture)
            .VerifyScreenRegionAsync(1, ButtonOnScreen, Before(), screenCaptureOk: true);

        Assert.Equal(VerifyDecision.NoChange, result.Decision);
    }

    [Fact]
    public async Task The_window_is_rendered_again_when_the_region_capture_is_not_allowed()
    {
        // Anything drawn on top of the target would be copied by a screen read and compared against the
        // target's own content — a change the target never made. Without the guarantee, the window capture is
        // the honest source even though it is the expensive one.
        var capture = new FixedCapture(
            Frame(Window, WindowHeight, (ButtonAt.X, ButtonAt.Y, ButtonWidth, ButtonHeight)),
            Window,
            WindowHeight,
            WindowOnScreen);

        var result = await new PostActionVerifier(capture).VerifyScreenRegionAsync(1, ButtonOnScreen, Before());

        Assert.Equal(VerifyDecision.AnyChange, result.Decision);
        Assert.Equal(0, capture.RegionCaptures);
        Assert.Equal(1, capture.WindowCaptures);
    }

    [Fact]
    public async Task A_before_frame_that_does_not_cover_the_region_falls_back_to_the_window_capture()
    {
        // The region is named in screen coordinates and the frame knows its own origin; without both there is
        // nothing to crop, so the whole window is captured rather than the wrong rectangle.
        var capture = new FixedCapture(
            Frame(Window, WindowHeight, (ButtonAt.X, ButtonAt.Y, ButtonWidth, ButtonHeight)),
            Window,
            WindowHeight,
            WindowOnScreen);

        var result = await new PostActionVerifier(capture).VerifyScreenRegionAsync(
            1,
            ButtonOnScreen,
            Before(withoutOrigin: true),
            screenCaptureOk: true);

        Assert.Equal(0, capture.RegionCaptures);
        Assert.Equal(1, capture.WindowCaptures);

        // And it degrades to exactly the old whole-window judgement, which for a small control is the
        // false negative the region path exists to remove. That is the honest price of not knowing where the
        // frame was taken — a guess would have been worse than either answer.
        Assert.Equal(VerifyDecision.NoChange, result.Decision);
    }

    [Fact]
    public void A_region_outside_the_frame_is_refused_rather_than_clamped()
    {
        // Clamping would compare a different rectangle against the frame and call it the same measurement.
        var frame = new CaptureResult
        {
            Ok = true,
            Width = 100,
            Height = 100,
            Bgra = Frame(100, 100),
            Method = "test",
        };

        Assert.Null(PostActionVerifier.Crop(frame, -1, 0, 10, 10));
        Assert.Null(PostActionVerifier.Crop(frame, 95, 0, 10, 10));
        Assert.Null(PostActionVerifier.Crop(frame, 0, 0, 0, 10));
        Assert.NotNull(PostActionVerifier.Crop(frame, 90, 90, 10, 10));
        Assert.Equal(frame, PostActionVerifier.Crop(frame, 0, 0, 100, 100));
    }

    /// <summary>The same control, named the way the observation names it: in screen coordinates.</summary>
    private static PixelRect ButtonOnScreen => new(
        WindowOnScreen.X + ButtonAt.X,
        WindowOnScreen.Y + ButtonAt.Y,
        ButtonWidth,
        ButtonHeight);

    private static CaptureResult Before(PixelRect? screenRect = null, bool withoutOrigin = false) => new()
    {
        Ok = true,
        Width = Window,
        Height = WindowHeight,
        Bgra = Frame(Window, WindowHeight),
        Method = "test",
        ScreenRect = withoutOrigin ? null : screenRect ?? WindowOnScreen,
    };

    private static IScreenCapture Capture(byte[] frame, PixelRect? screenRect = null) =>
        new FixedCapture(frame, Window, WindowHeight, screenRect ?? WindowOnScreen);

    private static byte[] Frame(int width, int height, params (int X, int Y, int W, int H)[] dirty)
    {
        var buffer = new byte[width * height * 4];
        Array.Fill(buffer, (byte)0x20);

        foreach (var (x, y, w, h) in dirty)
        {
            for (var row = y; row < y + h; row++)
            {
                for (var col = x; col < x + w; col++)
                {
                    var index = ((row * width) + col) * 4;
                    buffer[index] = 0xFF;
                    buffer[index + 1] = 0xFF;
                    buffer[index + 2] = 0xFF;
                }
            }
        }

        return buffer;
    }

    private sealed class FixedCapture(byte[] frame, int width, int height, PixelRect? screenRect = null) : IScreenCapture
    {
        public int RegionCaptures { get; private set; }

        public int WindowCaptures { get; private set; }

        public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default)
        {
            WindowCaptures++;
            return Task.FromResult(new CaptureResult
            {
                Ok = true,
                Width = width,
                Height = height,
                Bgra = frame,
                Method = "test",
                ScreenRect = screenRect,
            });
        }

        public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int w, int h, CancellationToken ct = default)
        {
            RegionCaptures++;

            // What a real screen copy returns: the pixels at that rectangle, and that rectangle as its origin.
            var buffer = new byte[w * h * 4];
            for (var row = 0; row < h; row++)
            {
                Array.Copy(frame, (((y - screenRect!.Y) + row) * width + (x - screenRect.X)) * 4, buffer, row * w * 4, w * 4);
            }

            return Task.FromResult(new CaptureResult
            {
                Ok = true,
                Width = w,
                Height = h,
                Bgra = buffer,
                Method = "test.region",
                ScreenRect = new PixelRect(x, y, w, h),
            });
        }
    }
}