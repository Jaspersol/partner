using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Partner.Core.Agent.Tools;
using Partner.Core.Media;
using Partner.Desktop.Capture;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tools;

/// <summary>
/// Shows the model a window.
/// <para>
/// This is the fallback for what UI Automation cannot see — Electron apps, games, canvases, remote desktops —
/// and it replaces OCR rather than supplementing it: a vision model reads a screen better than any local
/// recogniser, and the pixels are the ground truth either way. The tool is only registered when the configured
/// model claims to take images (<c>model.supportsImages</c>), because sending one to a text-only model fails
/// the request.
/// </para>
/// <para>
/// Deliberately does <b>not</b> re-observe: element ids are positions inside an observation, so minting a new
/// one would invalidate the ids the model is holding — and "look at it, then click that button" is exactly the
/// sequence this tool exists for.
/// </para>
/// </summary>
public sealed class DesktopScreenshotTool(DesktopAutomation automation, IMediaStore media) : ITool
{
    /// <summary>Longest side of the picture sent to the model. Bigger costs tokens, not accuracy.</summary>
    private const int DefaultMaxDimension = 1280;

    public ToolSpec Spec { get; } = new(
        "desktop_screenshot",
        "把一个窗口的截图发给模型看（UIA 看不见的应用——Electron、游戏、画布——靠这个）。"
        + "不传 entityId 就截当前前台窗口。图片会随下一次请求作为图像提供；坐标可以据此估算，但点击仍受窗口矩形约束。",
        """
        {"type":"object","properties":{
          "entityId":{"type":"string","description":"窗口 id，来自 desktop_observe；省略则截前台窗口"},
          "maxDimension":{"type":"integer","description":"最长边像素上限，默认 1280"}
        },"additionalProperties":false}
        """);

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var arguments = Json.Deserialize<ScreenshotArguments>(argumentsJson) ?? new ScreenshotArguments(null, null);
        var observation = automation.LastObservation;

        if (observation is null)
        {
            return new ToolResult(false, "还没有观察过：先调用 desktop_observe");
        }

        var entity = arguments.EntityId is { Length: > 0 } id
            ? observation.Find(id)
            : observation.Entities.FirstOrDefault(candidate => candidate.IsForeground);

        if (entity is null)
        {
            return new ToolResult(false, arguments.EntityId is { Length: > 0 }
                ? $"未知窗口 '{arguments.EntityId}'：先调用 desktop_observe"
                : "没有前台窗口可截：先调用 desktop_observe 并指定 entityId");
        }

        var handle = ParseHandle(entity.EntityId);
        var frame = await automation.Capture.CaptureWindowAsync(handle, ct).ConfigureAwait(false);

        if (!frame.Ok)
        {
            return new ToolResult(false, $"截图失败：{frame.Error ?? frame.Method}");
        }

        var maxDimension = arguments.MaxDimension is > 0 and <= 4096 ? arguments.MaxDimension.Value : DefaultMaxDimension;
        byte[] png;
        int width;
        int height;

        try
        {
            (png, width, height) = Encode(frame, maxDimension);
        }
        catch (Exception error)
        {
            return new ToolResult(false, $"编码截图失败：{error.Message}");
        }

        // Stored like any other attachment, so the log keeps an id and the bytes live in the media directory.
        string mediaId;
        try
        {
            using var stream = new MemoryStream(png);
            mediaId = await media.SaveAsync("image/png", stream, ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return new ToolResult(false, $"保存截图失败：{error.Message}");
        }

        var stored = media.TryRead(mediaId);
        var dataUri = stored?.ToDataUri();
        if (dataUri is null)
        {
            return new ToolResult(false, "截图已保存但读不回来");
        }

        return new ToolResult(
            true,
            $"{entity.Label}（{entity.Value ?? "?"}）{width}×{height} 截图（媒体 {mediaId}）；"
            + "坐标以该窗口矩形为参照，点击仍须落在矩形内。",
            new[] { dataUri });
    }

    private static (byte[] Png, int Width, int Height) Encode(CaptureResult frame, int maxDimension)
    {
        var source = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            palette: null,
            frame.Bgra,
            frame.Width * 4);

        var longest = Math.Max(frame.Width, frame.Height);
        if (longest > maxDimension)
        {
            // Scaling rather than cropping: the model needs the whole window to reason about layout.
            var scale = (double)maxDimension / longest;
            source = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = new MemoryStream();
        encoder.Save(stream);

        return (stream.ToArray(), source.PixelWidth, source.PixelHeight);
    }

    /// <summary>The window handle behind an entity id, which the observation wrote as hex.</summary>
    private static nint ParseHandle(string entityId) =>
        nint.Parse(entityId, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

    private sealed record ScreenshotArguments(string? EntityId, int? MaxDimension);
}
