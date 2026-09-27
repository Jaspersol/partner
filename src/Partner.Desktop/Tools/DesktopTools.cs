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
                minimized = entity.IsMinimized ? true : (bool?)null,
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
        + "keyPress / typeText / scroll / drag。给了 elementId 就用该控件的中心点，不必再给坐标；否则用 x/y——"
        + "**屏幕坐标**（desktop_observe 的 clickAt 就是屏幕坐标，可直接用）。"
        + "x/y 落在哪个已观察窗口里，就作用于那个窗口；跨多个窗口有歧义时会拒绝并列出候选，此时要显式传 entityId。"
        + "typeText 需要 text，keyPress 需要 virtualKey（该窗口须已在前台，否则先 focus），scroll 需要 scrollDelta，drag 需要 x/y + toX/toY。"
        + "执行前会重新观察并核对目标是否仍是同一个窗口/控件。",
        """
        {"type":"object","properties":{
          "entityId":{"type":"string","description":"窗口 id；给了 elementId 或 x/y 时可省略"},
          "elementId":{"type":"string","description":"控件 id，来自 desktop_observe"},
          "kind":{"type":"string","enum":["Focus","MovePointer","LeftClick","RightClick","KeyPress","TypeText","Scroll","Drag"]},
          "x":{"type":"integer","description":"屏幕坐标 x"},
          "y":{"type":"integer","description":"屏幕坐标 y"},
          "toX":{"type":"integer","description":"拖拽终点屏幕坐标 x"},
          "toY":{"type":"integer","description":"拖拽终点屏幕坐标 y"},
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

        var point = arguments.X is { } x && arguments.Y is { } y ? (x, y) : ((int X, int Y)?)null;
        var entityId = seenElement?.WindowId ?? arguments.EntityId;

        if (string.IsNullOrWhiteSpace(entityId))
        {
            // No window named, but a point is: the point itself says where the click lands, so derive the window
            // from it. Models do this — they look at the picture, then click the coordinates they worked out —
            // and refusing that on a technicality is how "operate the app" turns into a loop of refusals.
            entityId = ResolveByPoint(before, point, automation.LastShownEntityId)?.EntityId;
            if (entityId is null)
            {
                return new ToolResult(false, DescribeRefusal(before, point));
            }
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
        // is inside the window by construction. Otherwise the point is the model's own screen coordinate —
        // absent when it did not give one, which the guard treats as a malformed call rather than (0,0).
        var resolved = element?.Centre ?? point;

        if (Guard(kind, target, resolved, arguments) is { } blocked)
        {
            return new ToolResult(false, blocked);
        }

        var parameters = new ActionParams
        {
            X = resolved?.X ?? 0,
            Y = resolved?.Y ?? 0,
            VirtualKey = arguments.VirtualKey ?? 0,
            Text = arguments.Text,
            ScrollDelta = arguments.ScrollDelta ?? 0,
            ToX = arguments.ToX ?? 0,
            ToY = arguments.ToY ?? 0,
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
    /// <remarks>
    /// A missing point is refused rather than defaulted: the fields arrive from a model, and "no coordinates"
    /// silently becoming (0,0) is a click in the corner of the screen that nobody asked for.
    /// </remarks>
    public static string? Guard(ActionKind kind, UiEntity target, (int X, int Y)? point, DesktopActArguments arguments) => kind switch
    {
        // A minimized window's rect is a 160×28 stub at -32000,-32000, so a point "inside" it passes every
        // containment check and still lands in the void. Focus first — that also restores it.
        ActionKind.MovePointer or ActionKind.LeftClick or ActionKind.RightClick or ActionKind.Scroll or ActionKind.Drag
            when target.IsMinimized =>
            $"'{target.Label}' 已最小化（矩形是 -32000 处的占位值）：先对它执行 focus 还原，再操作",

        ActionKind.MovePointer or ActionKind.LeftClick or ActionKind.RightClick or ActionKind.Scroll when point is null =>
            $"{kind} 需要 elementId 或 x/y（屏幕坐标）",

        ActionKind.Drag when point is null || arguments.ToX is null || arguments.ToY is null =>
            "drag 需要 x/y（起点）+ toX/toY（终点），都是屏幕坐标",

        ActionKind.KeyPress or ActionKind.TypeText when !target.IsForeground =>
            $"'{target.Label}' 不在前台，输入会打到别的窗口：先对它执行 focus",

        ActionKind.MovePointer or ActionKind.LeftClick or ActionKind.RightClick or ActionKind.Scroll
            when !target.Rect.Contains(point!.Value.X, point.Value.Y) =>
            $"({point!.Value.X},{point.Value.Y}) 不在 '{target.Label}' 的矩形内：坐标是屏幕坐标，会点到别处",

        ActionKind.Drag when !target.Rect.Contains(point!.Value.X, point.Value.Y)
            || !target.Rect.Contains(arguments.ToX!.Value, arguments.ToY!.Value) =>
            $"拖拽的起点或终点不在 '{target.Label}' 的矩形内",

        ActionKind.KeyPress when arguments.VirtualKey is null or 0 => "keyPress 需要 virtualKey",
        ActionKind.TypeText when string.IsNullOrEmpty(arguments.Text) => "typeText 需要 text",
        ActionKind.Scroll when arguments.ScrollDelta is null or 0 => "scroll 需要 scrollDelta",

        _ => null,
    };

    /// <summary>
    /// Which window a bare point belongs to. An absolute-coordinate click physically goes to whatever is at
    /// that point, so deriving the window from the point is not a guess — but ambiguity is still refused: with
    /// several windows over one point, the model has to say which it meant.
    /// </summary>
    /// <remarks>
    /// The tie-breaker is the window whose picture the model was last shown: that is the one it was reasoning
    /// about when it worked the coordinates out.
    /// </remarks>
    public static UiEntity? ResolveByPoint(DesktopObservation observation, (int X, int Y)? point, string? lastShownEntityId)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (point is not { } at)
        {
            return null;
        }

        var covering = observation.Entities.Where(entity => entity.Rect.Contains(at.X, at.Y)).ToList();
        if (covering.Count == 1)
        {
            return covering[0];
        }

        return covering.FirstOrDefault(entity =>
            string.Equals(entity.EntityId, lastShownEntityId, StringComparison.Ordinal));
    }

    /// <summary>
    /// The refusal, with the observed windows in it. "Call desktop_observe first" is useless to a model that
    /// has just observed — what it needs is the ids it could have named.
    /// </summary>
    public static string DescribeRefusal(DesktopObservation observation, (int X, int Y)? point)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var why = point is { } at
            ? $"屏幕坐标 ({at.X},{at.Y}) 不落在唯一一个已观察窗口内"
            : "缺少 entityId，也没有 x/y 可以判断目标窗口";

        var windows = string.Join("；", observation.Entities.Take(8).Select(entity =>
            $"{entity.EntityId} '{entity.Label}'（{entity.Value ?? "?"}）@{entity.Rect.X},{entity.Rect.Y} "
            + $"{entity.Rect.Width}×{entity.Rect.Height}"));

        var more = observation.Entities.Count > 8 ? $"；…共 {observation.Entities.Count} 个" : string.Empty;

        return $"{why}：把 entityId 一起传过来。本次观察到：{windows}{more}";
    }

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
    /// <remarks>
    /// The coordinates are nullable because "absent" and "zero" are different answers, and the difference is
    /// the whole safety story: a click with no point is a malformed call, not a click at the screen corner.
    /// </remarks>
    public sealed record DesktopActArguments(
        string? EntityId,
        string? ElementId,
        string? Kind,
        int? X,
        int? Y,
        int? ToX,
        int? ToY,
        ushort? VirtualKey,
        string? Text,
        int? ScrollDelta);
}
