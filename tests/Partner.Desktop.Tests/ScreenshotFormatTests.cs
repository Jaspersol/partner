using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>A picture can be handed to the model as a JPEG, which is a fraction of the size of the PNG.</summary>
public sealed class ScreenshotFormatTests
{
    private static byte[] NoisyPng(int width, int height)
    {
        var random = new Random(7);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] FlatPng(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;
            pixels[i + 1] = 200;
            pixels[i + 2] = 200;
            pixels[i + 3] = 255;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void The_recoded_picture_is_a_jpeg_of_the_same_size_and_much_smaller_when_it_is_detailed()
    {
        var png = NoisyPng(320, 240);

        var jpeg = DesktopScreenshotTool.RecodeAsJpeg(png);

        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);
        using var stream = new MemoryStream(jpeg);
        var decoded = new JpegBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(320, decoded.PixelWidth);
        Assert.Equal(240, decoded.PixelHeight);
        Assert.True(jpeg.Length < png.Length, $"jpeg {jpeg.Length} should beat png {png.Length} on a detailed picture");
    }

    [Fact]
    public void A_flat_picture_round_trips_without_error()
    {
        Assert.NotEmpty(DesktopScreenshotTool.RecodeAsJpeg(FlatPng(64, 64)));
    }
}
