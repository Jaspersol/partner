using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Partner.Core.Agent.Tools;
using Partner.Core.Media;
using Partner.Desktop.Capture;
using Partner.Desktop.Interop;
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
        + "不传 entityId 就截当前前台窗口。结果里带窗口 id、窗口在屏幕上的矩形、图与窗口的比例，以及可直接照抄的 desktop_act 调用"
        + "（desktop_act 的 x/y 是**屏幕坐标**，不是图内坐标）。",
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

        // Asked of the window, not of the observation: a focus that restored it may have happened after the
        // observation was taken, and the log showed exactly that — restored at one step, still "minimized" at
        // the next, because the picture was refused from a stale snapshot.
        if (NativeMethods.IsIconic(handle))
        {
            // Windows reports a minimized window as a 160×28 stub at -32000,-32000. Capturing that returns a
            // blank sliver, and the model then reasons about — and clicks — coordinates in the void.
            return new ToolResult(false, $"'{entity.Label}' 已最小化（只有 160×28 的占位矩形，截不到内容）："
                + "先 desktop_act focus 把它还原，再截图");
        }

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

        // A bare point is resolvable to this window, because this is the picture the coordinates came from.
        automation.MarkShown(entity.EntityId);

        return new ToolResult(true, Describe(entity, width, height, mediaId), new[] { dataUri });
    }

    /// <summary>
    /// What the model needs in order to act on what it just saw: which window, where that window is on screen,
    /// how the picture maps onto it, and the call to make. Without the window id the model has to guess, and a
    /// click with coordinates but no window is exactly what it will send.
    /// </summary>
    private static string Describe(UiEntity entity, int width, int height, string mediaId)
    {
        var rect = entity.Rect;
        var perPicturePixel = width > 0 ? (double)rect.Width / width : 1;

        var mapping = Math.Abs(perPicturePixel - 1) < 0.001
            ? $"图与窗口同尺寸：图内 (px,py) → 屏幕 ({rect.X}+px, {rect.Y}+py)"
            : $"图是缩小的 {1 / perPicturePixel:0.###}×：图内 (px,py) → 屏幕 "
                + $"({rect.X}+px×{perPicturePixel:0.###}, {rect.Y}+py×{perPicturePixel:0.###})";

        var template = "{\"entityId\":\"" + entity.EntityId + "\",\"kind\":\"LeftClick\",\"x\":<屏幕x>,\"y\":<屏幕y>}";

        return $"{entity.Label}（{entity.Value ?? "?"}）窗口 {entity.EntityId} @ 屏幕 ({rect.X},{rect.Y}) "
            + $"{rect.Width}×{rect.Height}，截图 {width}×{height}（媒体 {mediaId}）。{mapping}。"
            + $"点击用 desktop_act（x/y 是屏幕坐标）：{template}";
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
