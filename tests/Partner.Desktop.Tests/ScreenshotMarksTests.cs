using System.Windows.Media;
using System.Windows.Media.Imaging;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// The reference marks on a screenshot. Pure imaging — no desktop, no cursor, no window: what is asserted is
/// that a model looking at the picture has something to measure against, which is the whole point.
/// </summary>
public sealed class ScreenshotMarksTests
{
    /// <summary>A 200×100 grey picture, standing in for a captured window of the same pixel size.</summary>
    private static BitmapSource Frame(int width = 200, int height = 100)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0x80);

        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF;
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }

    private static UiElement Element(string name, string role, PixelRect rect) => new(
        ElementId: "1a2b:0",
        WindowId: "1a2b",
        Name: name,
        Role: role,
        Rect: rect,
        IdentityDigest: "identity");

    private static int Pixel(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[0] | pixel[1] << 8 | pixel[2] << 16;
    }

    [Fact]
    public void A_mark_carries_the_click_point_the_server_computed()
    {
        // The model picks a mark; the server already knows the coordinate. Nothing is estimated.
        var button = Element("播放", "Button", new PixelRect(800, 350, 40, 20));

        var (annotated, marks) = ScreenshotMarks.Draw(
            Frame(),
            new PixelRect(752, 320, 200, 100),
            new[] { button });

        var mark = Assert.Single(marks);

        Assert.Equal(1, mark.Index);
        Assert.Equal("播放", mark.Label);
        Assert.Equal("1a2b:0", mark.ElementId);
        Assert.Equal((820, 360), mark.ClickAt);
        Assert.Equal(200, annotated.PixelWidth);
        Assert.Equal(100, annotated.PixelHeight);
    }

    [Fact]
    public void The_window_origin_is_added_to_the_picture_position()
    {
        // The frame is a capture of the window, so picture (0,0) is the window's corner on screen. A mark's
        // click point has to come back in screen coordinates, or the tool call lands off the window entirely.
        var (_, marks) = ScreenshotMarks.Draw(
            Frame(),
            new PixelRect(1000, 500, 200, 100),
            new[] { Element("确定", "Button", new PixelRect(1100, 550, 40, 20)) });

        Assert.Equal((1120, 560), Assert.Single(marks).ClickAt);
    }

    [Fact]
    public void Marks_are_numbered_in_reading_order()
    {
        var (_, marks) = ScreenshotMarks.Draw(
            Frame(),
            new PixelRect(0, 0, 200, 100),
            new[]
            {
                Element("下", "Button", new PixelRect(10, 70, 20, 20)),
                Element("右上", "Button", new PixelRect(150, 5, 20, 20)),
                Element("左上", "Button", new PixelRect(10, 5, 20, 20)),
            });

        Assert.Equal(new[] { "左上", "右上", "下" }, marks.Select(m => m.Label));
        Assert.Equal(new[] { 1, 2, 3 }, marks.Select(m => m.Index));
    }

    [Fact]
    public void A_document_is_not_marked_as_something_to_click()
    {
        // A Chromium window exposes one Document and nothing else. Numbering it would tell the model "click
        // here" about the middle of the page; with no marks the answer says the grid is all there is — true.
        var (_, marks) = ScreenshotMarks.Draw(
            Frame(),
            new PixelRect(0, 0, 200, 100),
            new[] { Element("网易云音乐", "Document", new PixelRect(0, 0, 200, 100)) });

        Assert.Empty(marks);
    }

    [Fact]
    public void A_control_outside_the_window_is_not_marked()
    {
        // It has no place on a picture of this window, and a mark at a coordinate the model cannot see is
        // worse than no mark at all.
        var (_, marks) = ScreenshotMarks.Draw(
            Frame(),
            new PixelRect(0, 0, 200, 100),
            new[]
            {
                Element("里面", "Button", new PixelRect(10, 10, 20, 20)),
                Element("别的窗口", "Button", new PixelRect(400, 10, 20, 20)),
                Element("空矩形", "Button", new PixelRect(0, 0, 0, 0)),
            });

        Assert.Equal("里面", Assert.Single(marks).Label);
    }

    [Fact]
    public void The_grid_is_drawn_in_screen_coordinate_space()
    {
        // 200 px wide window at screen x=752 → 400 px per cell would be silly, so the step is a round number of
        // screen pixels and the lines land on multiples of it. The label is that number.
        var source = Frame();
        var (annotated, _) = ScreenshotMarks.Draw(source, new PixelRect(700, 300, 200, 100), Array.Empty<UiElement>());

        // Step for a 200 px side is 25 px, and 700 is a multiple of 25 → a line sits at picture x=0. The next
        // two are at screen 725 and 750 → picture 25 and 50.
        Assert.NotEqual(Pixel(source, 0, 60), Pixel(annotated, 0, 60));
        Assert.NotEqual(Pixel(source, 25, 60), Pixel(annotated, 25, 60));
        Assert.NotEqual(Pixel(source, 50, 60), Pixel(annotated, 50, 60));

        // (60,60) is clear of every line and of the label column, so it stays exactly as captured — the grid
        // must not repaint the picture it is annotating.
        Assert.Equal(Pixel(source, 60, 60), Pixel(annotated, 60, 60));
    }

    [Fact]
    public void A_mark_is_drawn_where_the_control_is()
    {
        var source = Frame();
        var (annotated, _) = ScreenshotMarks.Draw(
            source,
            new PixelRect(0, 0, 200, 100),
            new[] { Element("播放", "Button", new PixelRect(100, 40, 40, 20)) });

        // The chip sits on the control's top-left corner, so that corner is no longer the original grey.
        Assert.NotEqual(Pixel(source, 100, 40), Pixel(annotated, 100, 40));

        // And the outline runs along the far edge of the control.
        Assert.NotEqual(Pixel(source, 139, 40), Pixel(annotated, 139, 40));
    }

    [Fact]
    public void A_scaled_picture_keeps_the_marks_on_their_controls()
    {
        // The picture is scaled down to fit the token budget; the marks have to follow it, or every mark would
        // point at the wrong place in exactly the case where the model most needs them.
        var source = Frame(400, 200);
        var (annotated, marks) = ScreenshotMarks.Draw(
            source,
            new PixelRect(0, 0, 400, 200),
            new[] { Element("播放", "Button", new PixelRect(200, 100, 40, 20)) });

        Assert.Equal(400, annotated.PixelWidth);
        Assert.Equal((220, 110), Assert.Single(marks).ClickAt);

        // Outline of the control: its left edge is picture x=200 and its right edge x=239.
        Assert.NotEqual(Pixel(source, 200, 100), Pixel(annotated, 200, 100));
        Assert.NotEqual(Pixel(source, 239, 119), Pixel(annotated, 239, 119));
    }

    [Fact]
    public void A_mark_is_drawn_at_its_picture_position_not_its_screen_position()
    {
        // The window sits at (700,300) and the control at screen (760,340), so on the picture the control is at
        // (60,40). Drawing the mark at its screen coordinates instead put it 700 px to the right of where it
        // belongs — off the picture entirely for a window far down the desktop, which is how a real capture
        // caught it.
        var source = Frame();
        var (annotated, _) = ScreenshotMarks.Draw(
            source,
            new PixelRect(700, 300, 200, 100),
            new[] { Element("播放", "Button", new PixelRect(760, 340, 40, 20)) });

        // The chip sits on the control's picture-space corner.
        Assert.NotEqual(Pixel(source, 60, 40), Pixel(annotated, 60, 40));

        // And nothing was painted elsewhere: (160,45) is clear of the grid lines and of the chip.
        Assert.Equal(Pixel(source, 160, 45), Pixel(annotated, 160, 45));
    }

    [Fact]
    public void A_window_with_no_usable_geometry_is_returned_untouched()
    {
        // A minimized window reports a 160×28 stub; there is nothing to mark and no grid to draw on it.
        var source = Frame();
        var (annotated, marks) = ScreenshotMarks.Draw(source, new PixelRect(0, 0, 0, 0), Array.Empty<UiElement>());

        Assert.Empty(marks);
        Assert.Same(source, annotated);
    }
}
