using System.Runtime.InteropServices;
using Partner.Desktop.Interop;
using Partner.Desktop.Perception;
using Partner.Desktop.Windows;

namespace Partner.Desktop.Capture;

/// <summary>A captured frame in BGRA32 (top-down rows).</summary>
public sealed record CaptureResult
{
    public required bool Ok { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required byte[] Bgra { get; init; }

    /// <summary>Which backend produced the pixels (audit / manifest evidence).</summary>
    public required string Method { get; init; }

    /// <summary>
    /// Where the frame's top-left pixel sits on the screen. A window capture measures the window's bounds as it
    /// renders, so this is the rectangle the picture is aligned to, and the one the grid and marks drawn on the
    /// picture are expressed in. The observation's rectangle can be stale (the window may have moved between
    /// observation and capture); drawing it over the new pixels would silently shift every coordinate.
    /// </summary>
    public PixelRect? ScreenRect { get; init; }

    public string? Error { get; init; }

    public static CaptureResult Failed(string method, string error) => new()
    {
        Ok = false,
        Width = 0,
        Height = 0,
        Bgra = Array.Empty<byte>(),
        Method = method,
        Error = error,
    };
}

public interface IScreenCapture
{
    Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default);

    Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int width, int height, CancellationToken ct = default);
}

/// <summary>
/// GDI capture: <c>PrintWindow(PW_RENDERFULLCONTENT)</c> first (works for occluded windows and is the only
/// way to capture a window that is not on top), then a screen-DC <c>BitBlt</c> fallback for the window's
/// visible region. See <see cref="NativeMethods"/> for why WGC is not used.
/// </summary>
public sealed class GdiScreenCapture : IScreenCapture
{
    public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default)
    {
        if (handle == 0)
        {
            return Task.FromResult(CaptureResult.Failed("gdi", "no window handle"));
        }

        return Task.Run(() => CaptureWindow(handle), ct);
    }

    public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int width, int height, CancellationToken ct = default) =>
        Task.Run(() => CaptureRegion(x, y, width, height), ct);

    private static CaptureResult CaptureWindow(nint handle)
    {
        var bounds = WindowEnumerator.GetBounds(handle);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return CaptureResult.Failed("gdi", "window has no visible bounds");
        }

        // PrintWindow renders from the window rectangle's origin, which on Windows 10+ sits a few pixels
        // outside the visible frame (the invisible resize border). Rendering into a visible-frame-sized bitmap
        // would shift every pixel of the picture while the grid and marks stay in visible-frame coordinates.
        // Render at the outer size and crop the invisible border away instead.
        var outer = WindowEnumerator.GetOuterBounds(handle);

        var printed = RenderToBuffer(
            Math.Max(outer.Width, bounds.Width),
            Math.Max(outer.Height, bounds.Height),
            (hdc, bitmap) => NativeMethods.PrintWindow(handle, hdc, NativeMethods.PwRenderFullContent),
            "gdi.printwindow");

        // Bounds are read at the moment of the render, and that reading is the frame's origin: the picture's
        // grid and marks must be drawn in the rectangle the window occupies now, not the observed one.
        var screenRect = new PixelRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

        if (printed.Ok)
        {
            return Crop(printed, bounds.Left - outer.Left, bounds.Top - outer.Top, bounds.Width, bounds.Height)
                with
            { ScreenRect = screenRect };
        }

        // Fallback: copy the window's on-screen region (fails for fully occluded windows). Already aligned —
        // the region *is* the visible frame.
        return CaptureRegion(bounds.Left, bounds.Top, bounds.Width, bounds.Height) with { ScreenRect = screenRect };
    }

    /// <summary>
    /// Cuts the visible frame out of a window-rectangle-sized render, so the picture's origin matches the
    /// origin every other part of the pipeline uses.
    /// </summary>
    internal static CaptureResult Crop(CaptureResult frame, int x, int y, int width, int height)
    {
        if (x == 0 && y == 0 && width == frame.Width && height == frame.Height)
        {
            return frame;
        }

        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > frame.Width || y + height > frame.Height)
        {
            // Refused rather than shrunk or returned misaligned: a picture whose pixels do not match its
            // rectangle is exactly the failure this crop exists to prevent.
            return CaptureResult.Failed(
                frame.Method,
                $"the render does not contain the visible frame ({x},{y} {width}x{height} of {frame.Width}x{frame.Height})");
        }

        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            Array.Copy(frame.Bgra, (((y + row) * frame.Width) + x) * 4, pixels, row * width * 4, width * 4);
        }

        return frame with { Width = width, Height = height, Bgra = pixels };
    }

    private static CaptureResult CaptureRegion(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return CaptureResult.Failed("gdi.bitblt", "region has no area");
        }

        var screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0)
        {
            return CaptureResult.Failed("gdi.bitblt", "GetDC failed");
        }

        try
        {
            return RenderToBuffer(
                width,
                height,
                (hdc, _) => NativeMethods.BitBlt(hdc, 0, 0, width, height, screenDc, x, y, NativeMethods.Srccopy),
                "gdi.bitblt");
        }
        finally
        {
            NativeMethods.ReleaseDC(0, screenDc);
        }
    }

    private static CaptureResult RenderToBuffer(int width, int height, Func<nint, nint, bool> render, string method)
    {
        var screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0)
        {
            return CaptureResult.Failed(method, "GetDC failed");
        }

        nint memoryDc = 0;
        nint bitmap = 0;
        nint previous = 0;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == 0)
            {
                return CaptureResult.Failed(method, "CreateCompatibleDC failed");
            }

            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == 0)
            {
                return CaptureResult.Failed(method, "CreateCompatibleBitmap failed");
            }

            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            if (!render(memoryDc, bitmap))
            {
                return CaptureResult.Failed(method, "render call returned false");
            }

            var stride = width * 4;
            var buffer = new byte[stride * height];

            var info = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                    Width = width,
                    // Negative height requests a top-down DIB, so row 0 is the top of the image.
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = NativeMethods.BiRgb,
                    SizeImage = (uint)buffer.Length,
                },
            };

            var scanned = NativeMethods.GetDIBits(
                memoryDc, bitmap, 0, (uint)height, buffer, ref info, NativeMethods.DibRgbColors);

            if (scanned == 0)
            {
                return CaptureResult.Failed(method, "GetDIBits failed");
            }

            return new CaptureResult
            {
                Ok = true,
                Width = width,
                Height = height,
                Bgra = buffer,
                Method = method,
            };
        }
        finally
        {
            if (previous != 0 && memoryDc != 0)
            {
                NativeMethods.SelectObject(memoryDc, previous);
            }

            if (bitmap != 0)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != 0)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            NativeMethods.ReleaseDC(0, screenDc);
        }
    }
}
