using System.Text.Json;
using Partner.Core.Agent.Tools;
using Partner.Desktop.Input;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tools;

/// <summary>
/// Which windows the model may act on.
/// <para>
/// An empty list means any window. The gate that actually protects the user is the approval gear; a title
/// filter that matched nothing would look like a broken tool rather than a policy, so this is for narrowing
/// an installation, not for being the only guard.
/// </para>
/// </summary>
public sealed record DesktopToolPolicy(IReadOnlyList<string> AllowedWindowTitles)
{
    public static DesktopToolPolicy Any { get; } = new(Array.Empty<string>());

    /// <summary>True when the window's title is inside the policy (or the policy is open).</summary>
    public bool Allows(string? title)
    {
        if (AllowedWindowTitles.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(title))
        {
            return false;
        }

        return AllowedWindowTitles.Any(allowed =>
            !string.IsNullOrWhiteSpace(allowed)
            && title.Contains(allowed, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Looks at the desktop: the windows, and the controls inside them.
/// <para>
/// This is the discover half of discover-then-act. Returning controls — with names and click points — is what
/// makes "click 播放" possible instead of "click at (412,338)", which is how a model ends up clicking whatever
/// happens to be at those coordinates. Read-only, so it never needs approval.
/// </para>
/// </summary>
public sealed class DesktopObserveTool(DesktopAutomation automation, DesktopToolPolicy policy) : ITool
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ToolSpec Spec { get; } = new(
        "desktop_observe",
        "看当前桌面：可见窗口（entityId、标题、进程、矩形）以及窗口内的可交互控件（elementId、名称、类型、点击点）。"
        + "动作只能针对这里返回的 entityId / elementId。若你要操作的程序不在列表里，它就没在运行：先用 desktop_launch 打开它，不要对别的窗口动手。",
        """{"type":"object","properties":{},"additionalProperties":false}""");

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        DesktopObservation observation;
        try
        {
            observation = automation.Observe();
        }
        catch (Exception error)
        {
            return Task.FromResult(new ToolResult(false, $"观察失败：{error.Message}"));
        }

        var entities = observation.Entities
            .Where(entity => policy.Allows(entity.Label))
            .Select(entity => new
            {
                entityId = entity.EntityId,
                title = entity.Label,
                process = entity.Value,
                foreground = entity.IsForeground,
                rect = new
                {
                    x = entity.Rect.X,
                    y = entity.Rect.Y,
                    width = entity.Rect.Width,
                    height = entity.Rect.Height,
                },
                elements = observation.Elements
                    .Where(element => element.WindowId == entity.EntityId)
                    .Select(element => new
                    {
                        elementId = element.ElementId,
                        name = element.Name,
                        role = element.Role,
                        value = element.Value ?? null,
                        clickAt = new { x = element.Centre.X, y = element.Centre.Y },
                    })
                    .ToList(),
            })
            .ToList();

        return Task.FromResult(new ToolResult(
            true,
            JsonSerializer.Serialize(new { obsId = observation.ObsId, count = entities.Count, entities }, Json)));
    }
}

/// <summary>
/// Acts on a window, or on a control inside it.
/// <para>
/// Irreversible: it moves the user's pointer, sends keystrokes, or types text. Governance decides whether it
/// runs unattended (see the approval gear). Two rules survive every gear:
/// </para>
/// <list type="bullet">
///   <item><b>The target is re-checked, not the lease.</b> A human can take minutes to answer, which outlives
///   any sane lease; refusing then would make approval useless. The desktop is observed again and the action
///   proceeds only if the window — and the control, when one was named — is still the same.</item>
///   <item><b>Global inputs are guarded.</b> Keystrokes and typed text go to whatever has focus, and pointer
///   actions go to whatever is at those coordinates, so anything that would land outside the named window is
///   refused before input is injected.</item>
/// </list>
/// </summary>
public sealed class DesktopActTool(DesktopAutomation automation, DesktopToolPolicy policy) : ITool
{
    public ToolSpec Spec { get; } = new(
        "desktop_act",
        "对 desktop_observe 返回的窗口或控件执行一次动作。kind 取 focus / movePointer / leftClick / rightClick / "
        + "keyPress / typeText / scroll / drag。给了 elementId 就用该控件的中心点，不必再给坐标；否则用 x/y（必须落在窗口矩形内）。"
        + "typeText 需要 text，keyPress 需要 virtualKey（该窗口须已在前台，否则先 focus），scroll 需要 scrollDelta，drag 需要 x/y + toX/toY。"
        + "执行前会重新观察并核对目标是否仍是同一个窗口/控件。",
        """
        {"type":"object","properties":{
          "entityId":{"type":"string","description":"窗口 id；给了 elementId 时可省略"},
          "elementId":{"type":"string","description":"控件 id，来自 desktop_observe"},
          "kind":{"type":"string","enum":["Focus","MovePointer","LeftClick","RightClick","KeyPress","TypeText","Scroll","Drag"]},
          "x":{"type":"integer"},
          "y":{"type":"integer"},
          "toX":{"type":"integer"},
          "toY":{"type":"integer"},
          "virtualKey":{"type":"integer"},
          "text":{"type":"string"},
          "scrollDelta":{"type":"integer"}
        },"required":["kind"],"additionalProperties":false}
        """);

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var arguments = Json.Deserialize<DesktopActArguments>(argumentsJson);
        if (arguments is null)
        {
            return new ToolResult(false, "参数不是合法 JSON");
        }

        if (!Enum.TryParse<ActionKind>(arguments.Kind, ignoreCase: true, out var kind))
        {
            return new ToolResult(false, $"未知动作 '{arguments.Kind}'；可用：{string.Join(", ", Enum.GetNames<ActionKind>())}");
        }

        // What the model decided on, before looking again.
        var before = automation.LastObservation;
        if (before is null)
        {
            return new ToolResult(false, "还没有观察过：先调用 desktop_observe");
        }

        var seenElement = arguments.ElementId is { Length: > 0 } elementId ? before.FindElement(elementId) : null;
        if (arguments.ElementId is { Length: > 0 } && seenElement is null)
        {
            return new ToolResult(false, $"未知控件 '{arguments.ElementId}'：先调用 desktop_observe");
        }

        var entityId = seenElement?.WindowId ?? arguments.EntityId;
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return new ToolResult(false, "缺少 entityId 或 elementId：先调用 desktop_observe");
        }

        var seenWindow = before.Find(entityId);
        if (seenWindow is null)
        {
            return new ToolResult(false, $"未知窗口 '{entityId}'：先调用 desktop_observe");
        }

        if (!policy.Allows(seenWindow.Label))
        {
            return new ToolResult(false, $"窗口 '{seenWindow.Label}' 不在允许列表内（设置里的桌面段）");
        }

        // Approval can outlive the lease by minutes. Looking again keeps the guarantee the lease was for —
        // act only on what was seen — without making a slow human answer a failure.
        DesktopObservation fresh;
        try
        {
            fresh = automation.Observe();
        }
        catch (Exception error)
        {
            return new ToolResult(false, $"重新观察失败：{error.Message}");
        }

        if (Revalidate(seenWindow, fresh) is { } stale)
        {
            return new ToolResult(false, stale);
        }

        var target = fresh.Find(entityId)!;

        if (seenElement is not null && RevalidateElement(seenElement, fresh) is { } staleElement)
        {
            return new ToolResult(false, staleElement);
        }

        var element = seenElement is null ? null : fresh.FindElement(seenElement.ElementId);

        // A named control carries its own click point: the model should not have to do geometry, and the point
        // is inside the window by construction.
        var point = element?.Centre ?? (arguments.X, arguments.Y);

        if (Guard(kind, target, point, arguments) is { } blocked)
        {
            return new ToolResult(false, blocked);
        }

        var parameters = new ActionParams
        {
            X = point.Item1,
            Y = point.Item2,
            VirtualKey = arguments.VirtualKey,
            Text = arguments.Text,
            ScrollDelta = arguments.ScrollDelta,
            ToX = arguments.ToX,
            ToY = arguments.ToY,
        };

        var action = await automation.ActAsync(entityId, kind, parameters, ct).ConfigureAwait(false);

        return new ToolResult(action.GuardPassed, Describe(action, kind, target, element));
    }

    /// <summary>
    /// Whether a fresh observation still shows the same window the decision was made on. Returns the reason it
    /// does not, or null when it is safe to act.
    /// </summary>
    public static string? Revalidate(UiEntity seen, DesktopObservation fresh)
    {
        ArgumentNullException.ThrowIfNull(seen);
        ArgumentNullException.ThrowIfNull(fresh);

        var now = fresh.Find(seen.EntityId);
        if (now is null)
        {
            return $"窗口 '{seen.Label}' 已经不在了：重新 desktop_observe 再决定";
        }

        return now.EvidenceDigest == seen.EvidenceDigest
            ? null
            : $"窗口 '{seen.Label}' 与观察时不同（标题/进程/位置/大小变了）：重新 desktop_observe 再决定";
    }

    /// <summary>The same check for a named control: the layout moved, so the coordinates are not trustworthy.</summary>
    public static string? RevalidateElement(UiElement seen, DesktopObservation fresh)
    {
        ArgumentNullException.ThrowIfNull(seen);
        ArgumentNullException.ThrowIfNull(fresh);

        var now = fresh.FindElement(seen.ElementId);
        if (now is null)
        {
            return $"控件 '{seen.Name}' 已经不在了：重新 desktop_observe 再决定";
        }

        return now.Digest == seen.Digest
            ? null
            : $"控件 '{seen.Name}' 与观察时不同（名称或位置变了）：重新 desktop_observe 再决定";
    }

    /// <summary>
    /// What would make this action land somewhere other than the window it names. Pointer and key input are
    /// global — the OS sends them to a coordinate or to whatever has focus — so without these the model can
    /// type into the user's editor while believing it is talking to another window.
    /// </summary>
    public static string? Guard(ActionKind kind, UiEntity target, (int X, int Y) point, DesktopActArguments arguments) => kind switch
    {
        ActionKind.KeyPress or ActionKind.TypeText when !target.IsForeground =>
            $"'{target.Label}' 不在前台，输入会打到别的窗口：先对它执行 focus",

        ActionKind.MovePointer or ActionKind.LeftClick or ActionKind.RightClick
            when !target.Rect.Contains(point.X, point.Y) =>
            $"({point.X},{point.Y}) 不在 '{target.Label}' 的矩形内：坐标是屏幕坐标，会点到别处",

        ActionKind.Drag when !target.Rect.Contains(point.X, point.Y) || !target.Rect.Contains(arguments.ToX, arguments.ToY) =>
            $"拖拽的起点或终点不在 '{target.Label}' 的矩形内",

        ActionKind.TypeText when string.IsNullOrEmpty(arguments.Text) => "typeText 需要 text",
        ActionKind.Scroll when arguments.ScrollDelta == 0 => "scroll 需要 scrollDelta",

        _ => null,
    };

    /// <summary>
    /// The result, with injection and verification kept apart: they answer different questions, and collapsing
    /// them into one word is how "it clicked" and "nothing visibly happened" end up looking identical.
    /// </summary>
    private static string Describe(DesktopAction action, ActionKind kind, UiEntity target, UiElement? element)
    {
        var who = element is null
            ? $"{target.Label}（{target.Value ?? "?"}）"
            : $"'{element.Name}' [{element.Role}] @ {target.Label}";

        if (!action.GuardPassed)
        {
            return $"未注入：{action.GuardReason} · 目标 {who}";
        }

        var injected = action.Injection?.Ok == true ? "注入成功" : $"注入失败（{action.Injection?.Detail}）";
        var verified = action.PostVerify?.Decision switch
        {
            VerifyDecision.AnyChange => "校验：画面已变化",
            VerifyDecision.NoChange => "校验：画面无变化（动作已发出，但效果未被确认）",
            VerifyDecision.Indeterminate => $"校验：无法判断（{action.PostVerify?.Reason}）",
            _ => "校验：未执行",
        };

        return $"{kind} · {injected} · {verified} · 目标 {who}";
    }

    /// <summary>What the model asked for, as it arrives from the model.</summary>
    public sealed record DesktopActArguments(
        string? EntityId,
        string? ElementId,
        string? Kind,
        int X,
        int Y,
        int ToX,
        int ToY,
        ushort VirtualKey,
        string? Text,
        int ScrollDelta);
}
