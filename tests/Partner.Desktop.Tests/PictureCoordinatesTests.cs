using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// The server-owned conversion from the picture the model looks at to the screen the pointer moves on, and
/// the staleness guard that keeps that conversion honest: a picture whose window has moved is not remapped
/// into a guess, it is refused.
/// </summary>
public sealed class PictureCoordinatesTests
{
    private static readonly PixelRect Whole = new(100, 200, 800, 600);

    [Fact]
    public void A_picture_pixel_maps_through_the_rectangle_it_was_taken_at()
    {
        // 1:1 picture: pixel (10,10) of the picture is pixel (10,10) of the window, and the window's own
        // origin carries it onto the screen.
        var picture = new ShownPicture("w", Whole, 800, 600, "obs-1");

        var mapped = DesktopActTool.MapPicturePoint(picture, 10, 10);

        Assert.Equal((110, 210), mapped);
    }

    [Fact]
    public void The_two_axes_scale_independently()
    {
        // A capture cropped to the visible frame and rescaled to the token budget need not keep its aspect:
        // each axis needs its own scale.
        var scaledDown = new ShownPicture("w", Whole, 400, 300, "obs-1");

        var mapped = DesktopActTool.MapPicturePoint(scaledDown, 100, 100);

        Assert.Equal((100 + 200, 200 + 200), mapped);
    }

    [Fact]
    public void Scaling_up_a_zoom_crop_reaches_the_same_screen_point()
    {
        // The zoom crop: a small piece of the window, upscaled for legibility, whose own rectangle is the
        // origin. The model reads (60,90) off the 3× picture of a crop sitting at (1000,1010).
        var crop = new ShownPicture("w", new PixelRect(1000, 1010, 100, 100), 300, 300, "obs-1");

        var mapped = DesktopActTool.MapPicturePoint(crop, 60, 90);

        Assert.Equal((1020, 1040), mapped);
    }

    [Fact]
    public void Rounding_is_to_nearest_not_floor()
    {
        var picture = new ShownPicture("w", Whole, 300, 200, "obs-1");

        // scale.X = 800/300 ≈ 2.667: px 10 → 26.67 → 27, not 26.
        var mapped = DesktopActTool.MapPicturePoint(picture, 10, 0);

        Assert.Equal(100 + 27, mapped.X);
    }

    [Fact]
    public void A_fresh_picture_maps_through()
    {
        var picture = new ShownPicture("w", Whole, 800, 600, "obs-1");

        Assert.Null(DesktopActTool.StalePicture(Whole, picture));
    }

    [Fact]
    public void A_picture_of_a_moved_window_is_refused_rather_than_remapped()
    {
        var picture = new ShownPicture("w", Whole, 800, 600, "obs-1");
        var now = new PixelRect(150, 250, 800, 600);

        var reason = DesktopActTool.StalePicture(now, picture);

        Assert.NotNull(reason);
        Assert.Contains("已过期", reason, StringComparison.Ordinal);
        Assert.Contains("重新 desktop_screenshot", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_of_a_resized_window_is_stale_too()
    {
        // The map itself is wrong when the content area changed, not just the origin: a fullscreen→restored
        // transition can leave the top-left where it was and everything else different.
        var picture = new ShownPicture("w", Whole, 800, 600, "obs-1");
        var now = new PixelRect(100, 200, 500, 400);

        Assert.NotNull(DesktopActTool.StalePicture(now, picture));
    }

    [Theory]
    [InlineData(400, 300)]   // the middle of a 800×600 window: a full crop fits on every side
    [InlineData(120, 210)]   // near the left edge: the crop grows rightward
    [InlineData(140, 750)]   // near the bottom: grows upward
    public void A_zoom_crop_stays_inside_the_window_and_keeps_the_point_off_its_edge(
        int atX,
        int atY)
    {
        var ok = DesktopScreenshotTool.ZoomRect(
            Whole, (atX, atY), out var cropScreen, out var cropFrame, out var error);

        Assert.True(ok, error);

        // Inside the window, around the point, never sticking out of it.
        Assert.True(Whole.Contains(cropScreen.X, cropScreen.Y));
        Assert.True(Whole.Contains(cropScreen.X + cropScreen.Width - 1, cropScreen.Y + cropScreen.Height - 1));
        Assert.True(cropScreen.Contains(atX, atY));
        // The point never sits at the crop's edge: there is reading room on both sides of it.
        Assert.True(atX > cropScreen.X, "the point has reading room to its left");
        Assert.True(atX < cropScreen.X + cropScreen.Width - 1, "the point has reading room to its right");
        Assert.True(atY > cropScreen.Y, "the point has reading room above it");
        Assert.True(atY < cropScreen.Y + cropScreen.Height - 1, "the point has reading room below it");

        // The frame rectangle is the same crop, expressed from the capture's origin.
        Assert.Equal(cropScreen.Width, cropFrame.Width);
        Assert.Equal(cropScreen.Height, cropFrame.Height);
        Assert.Equal(cropScreen.X - Whole.X, cropFrame.X);
        Assert.Equal(cropScreen.Y - Whole.Y, cropFrame.Y);
    }

    [Fact]
    public void A_zoom_crop_refuses_a_point_outside_the_window()
    {
        var ok = DesktopScreenshotTool.ZoomRect(Whole, (1000, 300), out var _, out var _, out var error);

        Assert.False(ok);
        Assert.Contains("不在窗口", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zoom_crop_bounded_on_one_side_shifts_rather_than_shrinks()
    {
        // A point 30 px from the left edge cannot centre a 192 px crop; the fixed-size crop shifts so that
        // it fills the room the window affords and the point keeps reading room into the crop.
        var at = (X: Whole.X + 30, Y: Whole.Y + 100);

        var ok = DesktopScreenshotTool.ZoomRect(Whole, at, out var cropScreen, out var _, out var error);

        Assert.True(ok, error);
        Assert.Equal(Whole.X, cropScreen.X);
        Assert.Equal(2 * DesktopScreenshotTool.ZoomRadius, cropScreen.Width);
        Assert.True(cropScreen.Contains(at.X, at.Y));
    }
}
