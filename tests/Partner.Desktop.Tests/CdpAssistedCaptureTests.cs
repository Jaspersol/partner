using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Partner.Desktop.Capture;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tests;

/// <summary>When the window system prints a browser's page as flat grey, the browser's own picture is laid in.</summary>
public sealed class CdpAssistedCaptureTests
{
    private const int FrameWidth = 100;
    private const int FrameHeight = 80;

    private static CaptureResult Frame(Func<int, int, uint> paint) => new()
    {
        Ok = true,
        Width = FrameWidth,
        Height = FrameHeight,
        Method = "gdi.printwindow",
        ScreenRect = new PixelRect(500, 300, FrameWidth, FrameHeight),
        Bgra = Enumerable.Range(0, FrameWidth * FrameHeight)
            .SelectMany(i => BitConverter.GetBytes(paint(i % FrameWidth, i / FrameWidth)))
            .ToArray(),
    };

    // A 1 px border all round, the rest is the client area.
    private static readonly PixelRect Client = new(501, 301, 98, 78);

    private static uint Pixel(byte[] bgra, int x, int y) => BitConverter.ToUInt32(bgra, ((y * FrameWidth) + x) * 4);

    private static byte[] RedPng(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[(i * 4) + 2] = 255; // R
            pixels[(i * 4) + 3] = 255; // A
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private const uint Grey = 0xFF666666;
    private const uint Frame1 = 0xFF222222;
    private const uint Red = 0xFFFF0000;

    private static uint GreyClientFrame(int x, int y) =>
        x is 0 or FrameWidth - 1 || y is 0 or FrameHeight - 1 ? Frame1 : Grey;

    [Fact]
    public void A_uniform_client_area_is_blank_and_a_painted_one_is_not()
    {
        var blank = Frame(GreyClientFrame);
        var painted = Frame((x, y) => x is 0 or FrameWidth - 1 || y is 0 or FrameHeight - 1 ? Frame1 : x == 50 && y == 40 ? Red : Grey);

        Assert.True(CdpAssistedCapture.LooksBlank(blank, Client, blank.ScreenRect!));

        // The borders being a different colour must not make it look painted: only the client area is judged.
        Assert.NotEqual(Pixel(blank.Bgra, 0, 0), Pixel(blank.Bgra, 50, 40));

        // A varied page is passed through. (The one red pixel is not on the sample grid, so give it real texture.)
        var textured = Frame((x, y) => x is 0 or FrameWidth - 1 || y is 0 or FrameHeight - 1 ? Frame1 : (uint)(0xFF000000 | (uint)((x * 7 + y * 13) % 255) << 8));
        Assert.False(CdpAssistedCapture.LooksBlank(textured, Client, textured.ScreenRect!));
        _ = painted;
    }

    [Fact]
    public void The_browsers_picture_replaces_the_client_area_and_leaves_the_frame_alone()
    {
        var blank = Frame(GreyClientFrame);

        var merged = CdpAssistedCapture.Overlay(blank, blank.ScreenRect!, Client, RedPng(98, 78));

        Assert.NotNull(merged);
        Assert.Equal(Red, Pixel(merged!.Bgra, 50, 40) | 0xFF000000);
        Assert.Equal(Frame1, Pixel(merged.Bgra, 0, 0)); // the window's own border is untouched
        Assert.Contains("cdp.screenshot", merged.Method, StringComparison.Ordinal);
        Assert.Equal(blank.Width, merged.Width);
    }

    [Fact]
    public void A_picture_at_another_density_is_scaled_to_the_client_area()
    {
        var blank = Frame(GreyClientFrame);

        var merged = CdpAssistedCapture.Overlay(blank, blank.ScreenRect!, Client, RedPng(196, 156));

        Assert.NotNull(merged);
        Assert.Equal(Red, Pixel(merged!.Bgra, 1, 1) | 0xFF000000);
        Assert.Equal(Red, Pixel(merged.Bgra, 98, 78) | 0xFF000000);
    }

    [Fact]
    public async Task A_window_without_a_debug_port_is_captured_as_before()
    {
        var blank = Frame(GreyClientFrame);
        var asked = 0;
        var capture = new CdpAssistedCapture(
            new StubCapture(blank),
            _ => null,
            (_, _, _) =>
            {
                asked++;
                return Task.FromResult<byte[]?>(null);
            },
            _ => Client,
            _ => null);

        var result = await capture.CaptureWindowAsync(1);

        Assert.Same(blank, result);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task A_blank_page_with_a_port_is_filled_from_the_browser()
    {
        var blank = Frame(GreyClientFrame);
        var capture = new CdpAssistedCapture(
            new StubCapture(blank),
            _ => 9222,
            (port, _, _) => Task.FromResult<byte[]?>(port == 9222 ? RedPng(98, 78) : null),
            _ => Client,
            _ => "页面");

        var result = await capture.CaptureWindowAsync(1);

        Assert.Contains("cdp.screenshot", result.Method, StringComparison.Ordinal);
        Assert.Equal(Red, Pixel(result.Bgra, 50, 40) | 0xFF000000);
    }

    [Fact]
    public async Task A_browser_that_declines_leaves_the_original_frame()
    {
        var blank = Frame(GreyClientFrame);
        var capture = new CdpAssistedCapture(
            new StubCapture(blank), _ => 9222, (_, _, _) => Task.FromResult<byte[]?>(null), _ => Client, _ => null);

        Assert.Same(blank, await capture.CaptureWindowAsync(1));
    }

    private sealed class StubCapture(CaptureResult result) : IScreenCapture
    {
        public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default) => Task.FromResult(result);

        public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int width, int height, CancellationToken ct = default) =>
            Task.FromResult(result);
    }
}
