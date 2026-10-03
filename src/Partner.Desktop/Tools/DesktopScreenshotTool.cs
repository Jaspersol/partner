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
        + "不传 entityId 就截当前前台窗口。**图上会叠坐标网格和控件标记**：网格刻度上的数字是屏幕坐标，"
        + "带标记的控件可以直接用它的 elementId 去 desktop_act（点击点由服务端算），没有标记的地方才靠读网格给坐标。"
        + "结果里带窗口 id、窗口在屏幕上的矩形、标记清单和可直接照抄的 desktop_act 调用"
        + "（desktop_act 的 x/y 是**屏幕坐标**，不是图内坐标）。",
        """
        {"type":"object","properties":{
          "entityId":{"type":"string","description":"窗口 id，来自 desktop_observe；省略则截前台窗口"},
          "maxDimension":{"type":"integer","description":"最长边像素上限，默认 1280"},
          "marks":{"type":"boolean","description":"是否叠网格与控件标记，默认 true；只有想看不带标注的原图时才设 false"}
        },"additionalProperties":false}
        """);

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var arguments = Json.Deserialize<ScreenshotArguments>(argumentsJson) ?? new ScreenshotArguments(null, null, null);
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

        var wantsMarks = arguments.Marks ?? true;

        // Elements belong to the observation the model is holding — the same ones it can address by id — so the
        // marks on the picture and the ids in the answer are the same set by construction.
        var elements = observation.Elements
            .Where(element => string.Equals(element.WindowId, entity.EntityId, StringComparison.Ordinal))
            .ToList();

        // The frame is aligned to the rectangle it was rendered at, which is not necessarily the one the
        // observation recorded: a window can move, or be restored, between being observed and being
        // photographed. Drawing the grid in the observed rectangle would label every gridline with a
        // coordinate that is off by however far the window moved, and the model would act on those numbers.
        // The observation's own rectangle is reported alongside when the two disagree, so a stale picture is
        // visible in the answer instead of silently mislabelled.
        var captured = frame.ScreenRect ?? entity.Rect;
        var moved = captured != entity.Rect
            ? $"（注意：截图时窗口已移动到 ({captured.X},{captured.Y}) {captured.Width}×{captured.Height}，"
                + $"网格与标记按这个位置标注）"
            : string.Empty;

        byte[] png;
        int width;
        int height;
        IReadOnlyList<ScreenshotMark> marks;

        try
        {
            (png, width, height, marks) = Encode(frame, maxDimension, captured, elements, wantsMarks);
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

        return new ToolResult(true, Describe(entity, captured, width, height, mediaId, marks, moved), new[] { dataUri });
    }

    /// <summary>
    /// What the model needs in order to act on what it just saw: which window, where that window is on screen,
    /// how the picture maps onto it, what the marks mean, and the call to make.
    /// <para>
    /// The instructions about the grid are part of the contract, not decoration. A measured failure had the
    /// model describe a play button correctly in words and then ask for it 334 px away in empty background —
    /// it had nothing to measure against, and arithmetic against a window offset was the only path from the
    /// picture to a coordinate.
    /// </para>
    /// </summary>
    private static string Describe(
        UiEntity entity,
        PixelRect captured,
        int width,
        int height,
        string mediaId,
        IReadOnlyList<ScreenshotMark> marks,
        string moved)
    {
        var rect = captured;
        var perPicturePixel = width > 0 ? (double)rect.Width / width : 1;

        var lines = new List<string>
        {
            $"{entity.Label}（{entity.Value ?? "?"}）窗口 {entity.EntityId} @ 屏幕 ({rect.X},{rect.Y}) "
                + $"{rect.Width}×{rect.Height}，截图 {width}×{height}（媒体 {mediaId}）。{moved}",

            $"图上叠了坐标网格，**刻度上的数字就是屏幕坐标**：先沿着网格读出目标所在格的屏幕 x/y，再写进 "
                + $"desktop_act 的 x/y。不要凭感觉估。窗口内 (px,py) 与屏幕的关系是 "
                + $"屏幕 = ({rect.X}+px×{perPicturePixel:0.###}, {rect.Y}+py×{perPicturePixel:0.###})。",
        };

        if (marks.Count == 0)
        {
            lines.Add(
                "这个窗口没有暴露任何可寻址的控件（Chromium / Electron / 游戏 / 画布），所以只有网格可用："
                + "没有标记可以引用，坐标必须自己从网格读。");
        }
        else
        {
            var listed = string.Join(
                "；",
                marks.Take(24).Select(mark =>
                    $"标记 {mark.Index} = '{mark.Label}' [{mark.Role}] elementId {mark.ElementId} "
                    + $"点击点屏幕 ({mark.ClickAt.X},{mark.ClickAt.Y})"));

            var more = marks.Count > 24 ? $"；…共 {marks.Count} 个标记" : string.Empty;

            lines.Add(
                $"带标记的控件（图上的数字就是标记号）：{listed}{more}。"
                + "点这些控件**优先用 elementId**（点击点由服务端算，不用给坐标）；图上没有标记的地方才用网格读坐标。");
        }

        lines.Add(
            "点击用 desktop_act：{ \"entityId\":\"" + entity.EntityId + "\", \"kind\":\"LeftClick\", "
            + "\"elementId\":\"<标记上的 elementId>\" } 或 { \"entityId\":\"" + entity.EntityId + "\", "
            + "\"kind\":\"LeftClick\", \"x\":<从网格读出的屏幕x>, \"y\":<从网格读出的屏幕y> }");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Scales the frame to the token budget, overlays the reference marks, and encodes one PNG.
    /// <para>
    /// The marks are drawn <i>before</i> encoding and after scaling, so a gridline's label is the screen
    /// coordinate the tool call needs — not a picture coordinate the model would have to convert.
    /// </para>
    /// </summary>
    private static (byte[] Png, int Width, int Height, IReadOnlyList<ScreenshotMark> Marks) Encode(
        CaptureResult frame,
        int maxDimension,
        PixelRect window,
        IReadOnlyList<UiElement> elements,
        bool marks)
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

        var drawn = marks
            ? ScreenshotMarks.Draw(source, window, elements)
            : (source, Array.Empty<ScreenshotMark>());

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(drawn.Item1));

        using var stream = new MemoryStream();
        encoder.Save(stream);

        return (stream.ToArray(), drawn.Item1.PixelWidth, drawn.Item1.PixelHeight, drawn.Item2);
    }

    /// <summary>The window handle behind an entity id, which the observation wrote as hex.</summary>
    private static nint ParseHandle(string entityId) =>
        nint.Parse(entityId, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

    private sealed record ScreenshotArguments(string? EntityId, int? MaxDimension, bool? Marks);
}
