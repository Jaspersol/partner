using System.Text.Json;
using Partner.Core.Agent.Tools;
using Partner.Desktop.Interop;

namespace Partner.Desktop.Tools;

/// <summary>
/// Changes a window's state: restore, maximize, minimize, or move and resize it. The things a person does with the
/// title bar, which the act tool cannot (it clicks controls inside a window, and a minimized window has none).
/// <para>
/// Not read-only and not dangerous: it rearranges what is on screen and leaves the GUI no further than the window
/// itself. The window must be one an observation returned and the policy allows.
/// </para>
/// </summary>
public sealed class DesktopWindowTool(DesktopAutomation automation, DesktopToolPolicy policy) : ITool
{
    public const string ToolName = "desktop_window";

    private const int ShowMaximize = 3;
    private const int ShowMinimize = 6;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public ToolSpec Spec { get; } = new(
        ToolName,
        "改变一个窗口的状态：restore（还原最小化/最大化）、maximize、minimize、move（移动并可改大小）。"
        + "对象是 desktop_observe 返回的 entityId。最小化的窗口没有可点的控件，先 restore 再操作；"
        + "动完之后窗口的位置变了，重新 desktop_observe 再继续。",
        """
        {"type":"object","properties":{
          "entityId":{"type":"string","description":"窗口 id，来自 desktop_observe"},
          "action":{"type":"string","enum":["restore","maximize","minimize","move"]},
          "x":{"type":"integer","description":"move：新的左上角屏幕 x"},
          "y":{"type":"integer","description":"move：新的左上角屏幕 y"},
          "width":{"type":"integer","description":"move：新宽度，不给就保持"},
          "height":{"type":"integer","description":"move：新高度，不给就保持"}
        },"required":["entityId","action"],"additionalProperties":false}
        """);

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        WindowArguments? arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<WindowArguments>(argumentsJson, Json);
        }
        catch (JsonException)
        {
            arguments = null;
        }

        if (arguments is not { EntityId.Length: > 0, Action.Length: > 0 })
        {
            return Task.FromResult(new ToolResult(false, "参数需要 entityId 和 action"));
        }

        var window = automation.LastObservation?.Find(arguments.EntityId);
        if (window is null)
        {
            return Task.FromResult(new ToolResult(false, $"未知窗口 '{arguments.EntityId}'：先调用 desktop_observe"));
        }

        if (!policy.Allows(window.Label))
        {
            return Task.FromResult(new ToolResult(false, $"窗口 '{window.Label}' 不在允许列表内（设置里的桌面段）"));
        }

        if (!long.TryParse(arguments.EntityId, System.Globalization.NumberStyles.HexNumber, null, out var raw))
        {
            return Task.FromResult(new ToolResult(false, $"'{arguments.EntityId}' 不是窗口 id"));
        }

        var handle = (nint)raw;
        bool ok;
        string done;

        switch (arguments.Action.ToLowerInvariant())
        {
            case "restore":
                ok = NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
                done = "已还原";
                break;
            case "maximize":
                ok = NativeMethods.ShowWindow(handle, ShowMaximize);
                done = "已最大化";
                break;
            case "minimize":
                ok = NativeMethods.ShowWindow(handle, ShowMinimize);
                done = "已最小化";
                break;
            case "move":
                if (arguments.X is not { } x || arguments.Y is not { } y)
                {
                    return Task.FromResult(new ToolResult(false, "move 需要 x 和 y（新的左上角，屏幕坐标）"));
                }

                var width = arguments.Width ?? window.Rect.Width;
                var height = arguments.Height ?? window.Rect.Height;
                if (width < 100 || height < 60)
                {
                    return Task.FromResult(new ToolResult(false, "窗口太小（至少 100×60）：这样的大小几乎没法操作"));
                }

                ok = NativeMethods.SetWindowPos(handle, nint.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate);
                done = $"已移到 ({x},{y})，大小 {width}×{height}";
                break;
            default:
                return Task.FromResult(new ToolResult(false, $"未知 action '{arguments.Action}'；可用：restore / maximize / minimize / move"));
        }

        return Task.FromResult(ok
            ? new ToolResult(true, $"'{window.Label}' {done}。位置变了：重新 desktop_observe 再继续。")
            : new ToolResult(false, $"窗口 '{window.Label}' 没有响应这个操作（可能已关闭，或权限更高的程序）"));
    }

    private sealed record WindowArguments(string? EntityId, string? Action, int? X, int? Y, int? Width, int? Height);
}
