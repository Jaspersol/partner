using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Partner.Core.Agent.Tools;
using Partner.Core.Media;
using Partner.Desktop.Capture;
using Partner.Desktop.Interop;
using Partner.Desktop.Perception;
using Partner.Desktop.Windows;

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

    /// <summary>Half the side of a zoomed crop, in screen pixels. The crop is the second stage of the two
    /// step aiming: the model reads a rough position off the whole window, then looks at a piece of it
    /// full-size — where a 1280-wide whole-window picture is one picture pixel per ~two screen pixels, the
    /// crop is delivered at three times its native resolution, and its grid is pinned to a 20 px step.
    /// </summary>
    internal const int ZoomRadius = 96;

    /// <summary>A zoom crop is upscaled this much for legibility. Upscaling adds no information — the grid
    /// and the marks already carry the exact numbers — but reading an 11 pt label inside a 192 px image is
    /// a strain, and the model that has to read the numbers is the whole point.</summary>
    private const int ZoomUpscale = 3;

    /// <summary>The screen-pixel grid step a zoomed crop is drawn with (whole-window pictures keep the
    /// about-8-cells rule): every cell of it is 20 px wide, so a line-to-line reading is ±10 px on screen.</summary>
    internal const int ZoomGridStep = 20;

    public ToolSpec Spec { get; } = new(
        "desktop_screenshot",
        "把一个窗口的截图发给模型看（UIA 看不见的应用——Electron、游戏、画布——靠这个）。"
        + "不传 entityId 就截当前前台窗口。**图上会叠坐标网格和控件标记**：网格刻度上的数字是屏幕坐标，"
        + "带标记的控件可以直接用它的 elementId 去 desktop_act（点击点由服务端算），没有标记的地方才靠读网格给坐标。"
        + "结果里带窗口 id、窗口在屏幕上的矩形、标记清单和可直接照抄的 desktop_act 调用"
        + "（desktop_act 的 x/y 是**屏幕坐标**，不是图内坐标）。"
        + "看任务栏/托盘/通知/桌面图标用 scope=desktop（整个虚拟桌面，带窗口编号标记，不需要先 observe）。",
        """
        {"type":"object","properties":{
          "entityId":{"type":"string","description":"窗口 id，来自 desktop_observe；省略则截前台窗口"},
          "scope":{"type":"string","enum":["window","desktop"],"default":"window","description":"desktop = 截**整个虚拟桌面**（任务栏、托盘、通知、桌面图标这些不属于任何顶层窗口的东西）。带窗口编号标记：图上的数字对应结果里的 entityId，可直接给 desktop_act；不需要先 desktop_observe"},
          "maxDimension":{"type":"integer","description":"最长边像素上限，默认 1280"},
          "marks":{"type":"boolean","description":"是否叠网格与控件标记，默认 true；只有想看不带标注的原图时才设 false"},
          "zoomAt":{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"}},"description":"放大复查：屏幕上的一个粗略点位（从整窗网格读出）。返回以它为中心的一小片原图（约 192×192 屏幕 px，放大 3 倍显示，网格 20 px 一格），图上刻度仍是屏幕坐标，另带 mediaId 供 desktop_act 用 px/py 精确点击"}
        },"additionalProperties":false}
        """);

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var arguments = Json.Deserialize<ScreenshotArguments>(argumentsJson)
            ?? new ScreenshotArguments(null, null, null, null, null);

        if (string.Equals(arguments.Scope, "desktop", StringComparison.OrdinalIgnoreCase))
        {
            return await CaptureDesktopPicture(ct).ConfigureAwait(false);
        }

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

        // Where inside the frame a zoom asked for: clamped to the window, refused when the point the model
        // read off the whole window is not on it at all — a zoom into a neighbouring window would answer a
        // question nobody was asking.
        (PixelRect CropScreen, PixelRect CropFrame)? zoom = null;
        if (arguments.ZoomAt is { } at)
        {
            var zoomAt = (X: at.X, Y: at.Y);
            if (!ZoomRect(captured, zoomAt, out var cropScreen, out var cropFrame, out var zoomError))
            {
                return new ToolResult(false, zoomError);
            }

            zoom = (cropScreen, cropFrame);
        }

        byte[] png;
        int width;
        int height;
        IReadOnlyList<ScreenshotMark> marks;

        try
        {
            (png, width, height, marks) = zoom is { } crop
                ? EncodeZoom(frame, crop.CropFrame, crop.CropScreen, elements)
                : Encode(frame, maxDimension, captured, elements, wantsMarks);
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

        // Recorded with its media id, so the model can ask desktop_act for px/py <i>on this picture</i> and
        // the server does the conversion — from the rectangle the picture was actually taken at, not one the
        // model converted.
        var pictureScreen = zoom?.CropScreen ?? captured;
        automation.RecordPicture(mediaId, new ShownPicture(
            entity.EntityId,
            pictureScreen,
            width,
            height,
            observation.ObsId));

        automation.RecordToolPicture("desktop_screenshot", entity.Label, width, height, zoom is not null);

        return new ToolResult(true, zoom is { } done
            ? DescribeZoom(entity, done.CropScreen, mediaId, marks)
            : Describe(entity, captured, width, height, mediaId, marks, moved), new[] { dataUri });
    }

    /// <summary>
    /// The visible state after an action, as the act tool's answer itself.
    /// <para>
    /// An action's result used to be text only, so the model had to spend a second tool call guessing what
    /// the result looked like — and a GUI task is precisely the case where wanting to see is the norm, not
    /// the exception. This is the same pipeline <see cref="InvokeAsync"/> runs, aimed at the window that was
    /// just acted on: capture, scale to the token budget, draw the grid and the element marks, store under a
    /// media id, and return the data URI for the wire. Every step degrades silently to "no picture" rather
    /// than failing the action that already succeeded: a missing picture costs the model one
    /// <c>desktop_screenshot</c> call, a failed action costs the whole turn.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> a re-observe of anything but this window's own elements: the ids the marks are
    /// drawn with come from the <b>fresh</b> observation the act just re-checked against, so what the picture
    /// labels is exactly what the model can address by id next. Element ids minted from a stale observation
    /// would be refused by the revalidation on first use.
    /// </para>
    /// </summary>
    internal static async Task<PostActionPicture?> RenderAfterActionAsync(
        DesktopAutomation automation,
        IMediaStore media,
        UiEntity target,
        IReadOnlyList<UiElement> elements,
        string obsId,
        CancellationToken ct)
    {
        var handle = ParseHandle(target.EntityId);

        // Same trap as the explicit tool: the stub rectangle at -32000 has no pixels to show, and the answer
        // for it is in the action's result text ("focus first"), not in a blank picture. The fresh
        // observation is the truth here — it measured IsIconic when the act re-checked the window, which is
        // never staler than re-asking — and the handle is asked as well, so a window that minimized in the
        // last few milliseconds is caught either way.
        if (target.IsMinimized || NativeMethods.IsIconic(handle))
        {
            return null;
        }

        CaptureResult frame;
        try
        {
            frame = await automation.Capture.CaptureWindowAsync(handle, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }

        if (!frame.Ok)
        {
            return null;
        }

        // Aligned to where the frame was actually rendered: the window may have moved — even by the action
        // itself, which is exactly a restore — and a grid labelled from the observation's rectangle would
        // carry coordinates that are wrong by however far it moved.
        var captured = frame.ScreenRect ?? target.Rect;

        byte[] png;
        int width;
        int height;
        IReadOnlyList<ScreenshotMark> marks;
        try
        {
            (png, width, height, marks) = Encode(frame, DefaultMaxDimension, captured, elements, marks: true);
        }
        catch (Exception)
        {
            return null;
        }

        string mediaId;
        try
        {
            using var stream = new MemoryStream(png);
            mediaId = await media.SaveAsync("image/png", stream, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }

        var dataUri = media.TryRead(mediaId)?.ToDataUri();
        if (dataUri is null)
        {
            return null;
        }

        // The same bookkeeping the explicit tool does: the model can address this picture with px/py +
        // mediaId, the server owns the conversion, and a bare point resolves to this window.
        automation.MarkShown(target.EntityId);
        automation.RecordPicture(mediaId, new ShownPicture(target.EntityId, captured, width, height, obsId));

        return new PostActionPicture(mediaId, dataUri, width, height, captured, marks);
    }

    /// <summary>What the post-action picture carries onto the wire, at one glance per field.</summary>
    internal sealed record PostActionPicture(
        string MediaId,
        string DataUri,
        int Width,
        int Height,
        PixelRect ScreenRect,
        IReadOnlyList<ScreenshotMark> Marks);

    /// <summary>
    /// The desktop, not a window: one frame of the whole virtual desktop with a grid whose labels are
    /// screen coordinates and numbered chips on the windows. The model starts here when what it is looking
    /// for lives under the window level — the tray icon it wants clicked, the toast it wants dismissed —
    /// and before any observation exists, which is why this path skips the observe-first refusal the
    /// per-window branch keeps: a first look cannot be conditioned on already having looked.
    /// <para>
    /// A window's chip carries its entityId, so "act on that window" needs no separate observe; the px/py
    /// on this picture are served by the act as a bare screen point (which is what they map to), where the
    /// usual guards — which window does the point land on, is it covered — do the rest.
    /// </para>
    /// </summary>
    private async Task<ToolResult> CaptureDesktopPicture(CancellationToken ct)
    {
        var frame = await automation.CaptureDesktopAsync(ct).ConfigureAwait(false);
        if (!frame.Ok)
        {
            return new ToolResult(false, $"全屏截图失败：{frame.Error ?? frame.Method}");
        }

        var desktop = frame.ScreenRect
            ?? new PixelRect(0, 0, frame.Width, frame.Height);

        var maxDimension = DesktopScreenshotTool.DefaultMaxDimension;
        var windows = WindowEnumerator.List().ToList();

        byte[] png;
        int width;
        int height;
        IReadOnlyList<ScreenshotMark> marks;
        try
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
                var factor = (double)maxDimension / longest;
                source = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(factor, factor));
            }

            (png, width, height, marks) = DesktopScreenshotTool.EncodeDesktop(source, desktop, windows);
        }
        catch (Exception error)
        {
            return new ToolResult(false, $"编码全屏截图失败：{error.Message}");
        }

        string mediaId;
        try
        {
            using var stream = new MemoryStream(png);
            mediaId = await media.SaveAsync("image/png", stream, ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return new ToolResult(false, $"保存全屏截图失败：{error.Message}");
        }

        if (media.TryRead(mediaId)?.ToDataUri() is not { } dataUri)
        {
            return new ToolResult(false, "全屏截图已保存但读不回来");
        }

        // Recorded like any other picture the model is shown, so px/py + mediaId on it resolves — under
        // this entity id, "desktop", which the act maps to a bare screen point.
        automation.MarkShown("desktop");
        automation.RecordPicture(mediaId, new ShownPicture(
            "desktop",
            desktop,
            width,
            height,
            automation.LastObservation?.ObsId ?? "desktop"));

        automation.RecordToolPicture("desktop_screenshot", "desktop", width, height, zoom: false);

        return new ToolResult(true, DescribeDesktop(desktop, width, height, mediaId, marks, windows), new[] { dataUri });
    }

    private static string DescribeDesktop(
        PixelRect desktop,
        int width,
        int height,
        string mediaId,
        IReadOnlyList<ScreenshotMark> marks,
        IReadOnlyList<DesktopWindow> windows)
    {
        var perPicturePixelX = width > 0 ? desktop.Width / (double)width : 1;
        var perPicturePixelY = height > 0 ? desktop.Height / (double)height : 1;
        var numbered = marks.ToDictionary(mark => mark.Index, mark => mark);

        var lines = new List<string>
        {
            $"桌面全景（虚拟屏幕 @ ({desktop.X},{desktop.Y}) {desktop.Width}×{desktop.Height}），"
                + $"截图 {width}×{height}（媒体 {mediaId}）。",

            $"图上叠了坐标网格，刻度上的数字是**屏幕坐标**。窗口内 (px,py) 与屏幕的关系是："
                + $"屏幕 = ({desktop.X}+px×{perPicturePixelX:0.####}, {desktop.Y}+py×{perPicturePixelY:0.####})。"
                + $"更稳的做法：直接把 px/py 和媒体 {mediaId} 给 desktop_act（服务端换算），"
                + "它落在哪个窗口就属于哪个窗口；或用图上的窗口编号标记拿 entityId。",
        };

        if (marks.Count == 0)
        {
            lines.Add("没有可标记的窗口（都最小化了，或桌面是空的）：只有网格可用。");
        }
        else
        {
            var listed = string.Join("；", marks.Take(12).Select(mark =>
                $"标记 {mark.Index} = '{mark.Label}'（entityId {mark.ElementId}，中心 ({mark.ClickAt.X},{mark.ClickAt.Y})）"));
            lines.Add($"窗口编号标记：{listed}。对某个窗口操作，用 desktop_act {{entityId:<标记上的 id>}}。");
        }

        // Everything still open that a mark did not fit — a minimized window, or a desktop full enough to
        // trip the cap — stays answerable from the observation, which is where ids come from anyway.
        var omitted = windows.Where(window => numbered.Values.All(mark => !string.Equals(mark.ElementId, window.Handle.ToString("x"), StringComparison.Ordinal))).ToList();
        if (omitted.Count > 0)
        {
            var note = string.Join("；", omitted.Take(6).Select(window => $"'{window.Title}'（已最小化）"));
            lines.Add($"未标记的窗口（desktop_observe 里仍可见）：{note}");
        }

        lines.Add($"下一步：{{ \"elementId\": \"<标记上的 entityId>\", \"kind\": \"LeftClick\" }} 或 {{ \"kind\": \"LeftClick\", \"x\":<屏幕x>, \"y\":<屏幕y> }} 或 {{ \"kind\": \"LeftClick\", \"px\":<图上x>, \"py\":<图上y>, \"mediaId\": \"{mediaId}\" }}");
        return string.Join("\n", lines);
    }

    private static (byte[] Png, int Width, int Height, IReadOnlyList<ScreenshotMark> Marks) EncodeDesktop(
        BitmapSource source,
        PixelRect desktop,
        IReadOnlyList<DesktopWindow> windows)
    {
        var (annotated, marks) = ScreenshotMarks.DrawDesktop(source, desktop, windows);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(annotated));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return (stream.ToArray(), annotated.PixelWidth, annotated.PixelHeight, marks);
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
        var perPicturePixelX = width > 0 ? rect.Width / (double)width : 1;
        var perPicturePixelY = height > 0 ? rect.Height / (double)height : 1;

        var lines = new List<string>
        {
            $"{entity.Label}（{entity.Value ?? "?"}）窗口 {entity.EntityId} @ 屏幕 ({rect.X},{rect.Y}) "
                + $"{rect.Width}×{rect.Height}，截图 {width}×{height}（媒体 {mediaId}）。{moved}",

            $"图上叠了坐标网格，**刻度上的数字就是屏幕坐标**：先沿着网格读出目标所在格的屏幕 x/y，再写进 "
                + $"desktop_act 的 x/y。不要凭感觉估。窗口内 (px,py) 与屏幕的关系是（两轴比例分别给，宽高不必同比例）："
                + $"屏幕 = ({rect.X}+px×{perPicturePixelX:0.####}, {rect.Y}+py×{perPicturePixelY:0.####})。"
                + $"更稳的做法：直接把 px/py 和媒体 {mediaId} 给 desktop_act，由服务端换算。",
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
                marks.Take(24).Select(Describe));

            var more = marks.Count > 24 ? $"；…共 {marks.Count} 个标记" : string.Empty;

            lines.Add(
                $"带标记的控件（图上的数字就是标记号，格式 号=名称(elementId)）：{listed}{more}。"
                + "点这些控件**优先用 elementId**（点击点由服务端算，不用给坐标）；图上没有标记的地方才用网格读坐标。");
        }

        lines.Add(
            "点击用 desktop_act：{ \"entityId\":\"" + entity.EntityId + "\", \"kind\":\"LeftClick\", "
            + "\"elementId\":\"<标记上的 elementId>\" } 或 { \"entityId\":\"" + entity.EntityId + "\", "
            + "\"kind\":\"LeftClick\", \"x\":<从网格读出的屏幕x>, \"y\":<从网格读出的屏幕y> }");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// One mark in the legend: its number, name and elementId, and nothing else. The click point is left out on
    /// purpose — an action given the elementId computes it on the server, and the same coordinates repeated for
    /// twenty-four marks are most of what the legend used to cost.
    /// </summary>
    private static string Describe(ScreenshotMark mark) => $"{mark.Index}={mark.Label}({mark.ElementId})";

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

    /// <summary>
    /// Where inside the frame a zoom asked for, clamped to the window by growing the crop toward the sides
    /// that have room — so the point is never at the crop's edge, where the model would run out of picture
    /// exactly where it needs to look. Refused, not clamped, when the point is not on this window: a zoom
    /// into the window next door answers a question nobody was asking.
    /// </summary>
    internal static bool ZoomRect(
        PixelRect window,
        (int X, int Y) at,
        out PixelRect cropScreen,
        out PixelRect cropFrame,
        out string error)
    {
        cropScreen = new PixelRect(0, 0, 0, 0);
        cropFrame = new PixelRect(0, 0, 0, 0);

        if (window.Width <= 0 || window.Height <= 0)
        {
            error = "窗口矩形无效，无法放大";
            return false;
        }

        if (!window.Contains(at.X, at.Y))
        {
            error = $"({at.X},{at.Y}) 不在窗口矩形 {window.Width}×{window.Height} 内：放大的是这个窗口的区域，先给窗口内的点";
            return false;
        }

        // A fixed-size crop (when the window affords it), shifted — never clipped — so the point keeps as
        // much reading room as the window allows: a crop clipped on one side would put the model's reading
        // target at the picture's edge, where the grid and the neighbours it needs to read against run out.
        var width = Math.Min(window.Width, 2 * ZoomRadius);
        var height = Math.Min(window.Height, 2 * ZoomRadius);

        var left = Clamp(at.X - ZoomRadius, window.X, window.X + window.Width - width);
        var top = Clamp(at.Y - ZoomRadius, window.Y, window.Y + window.Height - height);
        cropScreen = new PixelRect(left, top, width, height);

        // Frame pixels: the capture's origin is the window's corner, already in screen coordinates.
        cropFrame = new PixelRect(cropScreen.X - window.X, cropScreen.Y - window.Y, width, height);
        error = string.Empty;
        return true;
    }

    private static int Clamp(int value, int low, int high) =>
        Math.Clamp(value, Math.Min(low, high), Math.Max(low, high));

    /// <summary>
    /// Encodes a zoomed crop: cut at native resolution from the freshly captured frame, upscaled for
    /// legibility, then drawn with the pinned 20 px grid. The crop keeps what the downscaled whole-window
    /// picture threw away — real pixels — and that is the whole point of the second stage.
    /// </summary>
    private static (byte[] Png, int Width, int Height, IReadOnlyList<ScreenshotMark> Marks) EncodeZoom(
        CaptureResult frame,
        PixelRect cropFrame,
        PixelRect cropScreen,
        IReadOnlyList<UiElement> elements)
    {
        var fullFrame = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            palette: null,
            frame.Bgra,
            frame.Width * 4);

        var crop = new System.Windows.Media.Imaging.CroppedBitmap(
            fullFrame,
            new System.Windows.Int32Rect(cropFrame.X, cropFrame.Y, cropFrame.Width, cropFrame.Height));

        var zoomed = new TransformedBitmap(crop, new System.Windows.Media.ScaleTransform(ZoomUpscale, ZoomUpscale));

        var drawn = ScreenshotMarks.Draw(zoomed, cropScreen, elements, ZoomGridStep);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(drawn.Item1));

        using var stream = new MemoryStream();
        encoder.Save(stream);

        return (stream.ToArray(), drawn.Item1.PixelWidth, drawn.Item1.PixelHeight, drawn.Item2);
    }

    /// <summary>
    /// What the model needs to finish the aiming on a zoomed crop: the size of its grid, the exact
    /// conversion — which it should not do — and the call to make next.
    /// </summary>
    private static string DescribeZoom(
        UiEntity entity,
        PixelRect cropScreen,
        string mediaId,
        IReadOnlyList<ScreenshotMark> marks)
    {
        var lines = new List<string>
        {
            $"放大复查：'{entity.Label}'（{entity.Value ?? "?"}）窗口 {entity.EntityId} 的区域 @ 屏幕 "
                + $"({cropScreen.X},{cropScreen.Y}) {cropScreen.Width}×{cropScreen.Height}（媒体 {mediaId}）。",

            $"这张图是真像素放大 {ZoomUpscale} 倍，网格一格 = 屏幕 {ZoomGridStep} px，刻度上的数字是屏幕坐标。"
                + $"读数后仍然用 desktop_act：可直接给 x/y（屏幕坐标），更稳的做法是给 px/py + mediaId={mediaId}，"
                + $"由服务端换算。",
        };

        if (marks.Count > 0)
        {
            var listed = string.Join("；", marks.Take(24).Select(Describe));
            lines.Add($"带标记的控件：{listed}。这些优先用 elementId，不用坐标。");
        }

        lines.Add($"下一步：{{ \"entityId\":\"{entity.EntityId}\", \"kind\":\"LeftClick\", \"mediaId\":\"{mediaId}\", \"px\":<图上x>, \"py\":<图上y> }}");
        return string.Join("\n", lines);
    }

    /// <summary>The window handle behind an entity id, which the observation wrote as hex.</summary>
    private static nint ParseHandle(string entityId) =>
        nint.Parse(entityId, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

    private sealed record ScreenshotArguments(string? EntityId, int? MaxDimension, bool? Marks, ZoomAt? ZoomAt, string? Scope = null);

    public sealed record ZoomAt(int X, int Y);
}
