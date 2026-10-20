using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Capture;

/// <summary>
/// Screen capture that asks the browser when the window system cannot see the page.
/// <para>
/// <c>PrintWindow</c> on a GPU-composited browser window returns a flat grey rectangle for the page content,
/// though its border and title bar are correct. The page is fetched from the browser itself
/// (<c>Page.captureScreenshot</c> renders without the window being visible, in front, or on screen) and laid
/// into that rectangle. Used only for windows with a reachable debug port whose content really looks blank;
/// other captures pass through untouched.
/// </para>
/// </summary>
public sealed class CdpAssistedCapture(
    IScreenCapture inner,
    Func<nint, int?> portOfWindow,
    Func<int, string?, CancellationToken, Task<byte[]?>> screenshot,
    Func<nint, PixelRect?> clientRect,
    Func<nint, string?> titleOf) : IScreenCapture
{
    /// <summary>How many points of the client area are sampled to decide it is blank.</summary>
    private const int SampleGrid = 8;

    public async Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default)
    {
        var captured = await inner.CaptureWindowAsync(handle, ct).ConfigureAwait(false);

        if (!captured.Ok
            || captured.ScreenRect is not { } frame
            || portOfWindow(handle) is not { } port
            || clientRect(handle) is not { } client
            || !LooksBlank(captured, client, frame))
        {
            return captured;
        }

        byte[]? png;
        try
        {
            png = await screenshot(port, titleOf(handle), ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return captured;
        }

        return png is null ? captured : Overlay(captured, frame, client, png) ?? captured;
    }

    public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int width, int height, CancellationToken ct = default) =>
        inner.CaptureScreenRegionAsync(x, y, width, height, ct);

    /// <summary>True when a grid of points across the client area all hold one colour.</summary>
    internal static bool LooksBlank(CaptureResult captured, PixelRect client, PixelRect frame)
    {
        var left = client.X - frame.X;
        var top = client.Y - frame.Y;
        if (client.Width < SampleGrid || client.Height < SampleGrid
            || left < 0 || top < 0
            || left + client.Width > captured.Width || top + client.Height > captured.Height)
        {
            return false;
        }

        // Inset the sample grid: the outermost client pixels are often the window border or shadow, which
        // would make a blank page look painted.
        var insetX = Math.Min(8, client.Width / 8);
        var insetY = Math.Min(8, client.Height / 8);

        uint? first = null;
        for (var row = 0; row < SampleGrid; row++)
        {
            for (var column = 0; column < SampleGrid; column++)
            {
                var x = left + insetX + (client.Width - 1 - (2 * insetX)) * column / (SampleGrid - 1);
                var y = top + insetY + (client.Height - 1 - (2 * insetY)) * row / (SampleGrid - 1);
                var offset = ((y * captured.Width) + x) * 4;
                var pixel = BitConverter.ToUInt32(captured.Bgra, offset) | 0xFF000000u;

                first ??= pixel;
                if (pixel != first)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The frame with the browser's own picture of the page laid over the client area, or null if it cannot be decoded.</summary>
    internal static CaptureResult? Overlay(CaptureResult captured, PixelRect frame, PixelRect client, byte[] png)
    {
        BitmapSource page;
        try
        {
            using var stream = new MemoryStream(png);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            page = decoder.Frames[0];
        }
        catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException or InvalidOperationException)
        {
            return null;
        }

        // The browser reports the page at its own pixel density; the client area is in screen pixels.
        if (page.PixelWidth != client.Width || page.PixelHeight != client.Height)
        {
            page = new TransformedBitmap(
                page,
                new ScaleTransform((double)client.Width / page.PixelWidth, (double)client.Height / page.PixelHeight));
        }

        var converted = new FormatConvertedBitmap(page, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var left = client.X - frame.X;
        var top = client.Y - frame.Y;
        var merged = (byte[])captured.Bgra.Clone();

        for (var row = 0; row < height && top + row < captured.Height; row++)
        {
            var columns = Math.Min(width, captured.Width - left);
            if (columns <= 0)
            {
                break;
            }

            Buffer.BlockCopy(pixels, row * stride, merged, (((top + row) * captured.Width) + left) * 4, columns * 4);
        }

        return captured with { Bgra = merged, Method = captured.Method + "+cdp.screenshot" };
    }
}
