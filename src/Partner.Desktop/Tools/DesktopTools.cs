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
        + "动作只能针对这里返回的 entityId / elementId。若你要操作的程序不在列表里，它就没在运行：先用 desktop_launch 打开它，不要对别的窗口动手。"
        + "只想看一个窗口时传 entityId 或 titleContains——不传就是整桌面，每个窗口的每个控件都在里面，又慢又贵。",
        """
        {"type":"object","properties":{
          "entityId":{"type":"string","description":"只看这一个窗口（来自上一次观察）"},
          "titleContains":{"type":"string","description":"只看标题包含这个文字的窗口"}
        },"additionalProperties":false}
        """);

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        DesktopObservation observation;
        try
        {
            observation = automation.Observe();
        }
        catch (Exception error)
        {
            return new ToolResult(false, $"观察失败：{error.Message}");
        }

        var filter = DesktopObserveFilter.Parse(argumentsJson);

        // A browser page's own controls, for the windows that have one. Read after the window walk because it
        // is a network round trip on top of it — and its absence changes nothing, since those windows were
        // already reachable by coordinate.
        var domElements = 0;

        try
        {
            domElements = await automation.AddDomElementsAsync(filter.EntityId, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            domElements = 0;
        }

        // Narrowed before serializing, not after: the point of the filter is that a nine-window desktop does
        // not have to arrive as nine windows' worth of controls in every turn.
        var visible = observation.Entities
            .Where(entity => policy.Allows(entity.Label))
            .Where(filter.Matches)
            .ToList();

        if (visible.Count == 0 && !filter.IsEmpty)
        {
            return new ToolResult(
                false,
                $"没有窗口匹配这个筛选（{filter.Describe()}）：本次观察到 "
                + $"{string.Join("；", observation.Entities.Take(6).Select(entity => $"'{entity.Label}'"))}"
                + (observation.Entities.Count > 6 ? $"；…共 {observation.Entities.Count} 个" : string.Empty));
        }

        var entities = visible
            .Select(entity => new
            {
                entityId = entity.EntityId,
                title = entity.Label,
                process = entity.Value,
                foreground = entity.IsForeground,
                minimized = entity.IsMinimized ? true : (bool?)null,
                // A window cut off mid-walk has not been fully seen, and saying so is the difference between
                // "there is no such control" and "we stopped looking".
                elementsIncomplete = entity.ElementsTruncated ? true : (bool?)null,
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
                        // Its click point is outside the window, so it is not somewhere to click yet — it is
                        // somewhere to scroll to first.
                        offscreen = element.IsOffscreen ? true : (bool?)null,
                        clickAt = new { x = element.Centre.X, y = element.Centre.Y },
                    })
                    .ToList(),
            })
            .ToList();

        return new ToolResult(
            true,
            JsonSerializer.Serialize(new { obsId = observation.ObsId, count = entities.Count, entities }, Json)
            + (domElements > 0
                ? $"\n（其中 {domElements} 个控件来自浏览器页面的 DOM：直接用 elementId 按名字点就行，"
                    + "不需要窗口在前台，也不受窗口被别的程序挡住影响。）"
                : string.Empty));
    }

    /// <summary>
    /// Which windows this observation is about.
    /// <para>
    /// A filter is a statement about what the model is looking for, so one that matches nothing is answered
    /// with what <i>is</i> on screen rather than an empty list — an empty list reads as "the desktop is
    /// empty", which sends the model off launching programs that are already running.
    /// </para>
    /// </summary>
    public sealed record DesktopObserveFilter(string? EntityId, string? TitleContains)
    {
        public bool IsEmpty => EntityId is not { Length: > 0 } && TitleContains is not { Length: > 0 };

        /// <summary>What was asked for, for the refusal that says nothing matched it.</summary>
        public string Describe() =>
            (EntityId is { Length: > 0 } ? $"entityId={EntityId}" : string.Empty) +
            (TitleContains is { Length: > 0 } ? $"标题包含'{TitleContains}'" : string.Empty);

        public bool Matches(UiEntity entity) =>
            (EntityId is not { Length: > 0 } || string.Equals(EntityId, entity.EntityId, StringComparison.Ordinal))
            && (TitleContains is not { Length: > 0 }
                || (entity.Label ?? string.Empty).Contains(TitleContains, StringComparison.OrdinalIgnoreCase));

        /// <summary>A malformed call widens rather than narrows: guessing a filter would hide windows.</summary>
        /// <remarks>
        /// Parsed with the same options the answer is written with, so <c>entityId</c> here means the same
        /// <c>entityId</c> that comes back out — a filter silently matching nothing is worse than no filter.
        /// </remarks>
        public static DesktopObserveFilter Parse(string argumentsJson)
        {
            try
            {
                var arguments = JsonSerializer.Deserialize<ObserveArguments>(argumentsJson, Json);
                return new DesktopObserveFilter(arguments?.EntityId, arguments?.TitleContains);
            }
            catch (JsonException)
            {
                return new DesktopObserveFilter(null, null);
            }
        }
    }

    private sealed record ObserveArguments(string? EntityId, string? TitleContains);
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
        + "doubleClick / keyPress / typeText / scroll / scrollIntoView / drag。"
        + "给了 elementId 就按控件操作（leftClick / focus / scrollIntoView 优先走 UI Automation 的 "
        + "Invoke / Select / Toggle / SetFocus / ScrollItem，**不需要该窗口在前台**；控件没有对应 pattern 时才点它的中心点），不必再给坐标；"
        + "否则用 x/y——"
        + "**屏幕坐标**（desktop_observe 的 clickAt 就是屏幕坐标，可直接用），且必须落在虚拟桌面范围内。"
        + "x/y 落在哪个已观察窗口里，就作用于那个窗口；跨多个窗口有歧义时会拒绝并列出候选，此时要显式传 entityId。"
        + "点还必须真的能落到目标窗口上：若该点被别的窗口挡住（包括本应用自己的窗口），会拒绝并报出挡住的窗口——"
        + "先对目标 focus 把它带到前台，再点。"
        + "offset 是控件内部的相对位置（0..1，左上角为 0,0）：拆分按钮的箭头这类「一个控件里两个作用」才需要它，"
        + "不传就是控件中心。"
        + "typeText 需要 text；keyPress 需要 virtualKey，可选 modifiers（[\"ctrl\"]、[\"ctrl\",\"shift\"] 这样的组合键）；"
        + "scroll 需要 scrollDelta（**格数**：正数向上滚，负数向下滚，一次 3 格比较合适）；"
        + "scrollIntoView 只需 elementId（由控件自己报距离，不用猜滚多少）；drag 需要 x/y + toX/toY。"
        + "执行前会重新观察并核对目标是否仍是同一个窗口/控件。",
        """
        {"type":"object","properties":{
          "entityId":{"type":"string","description":"窗口 id；给了 elementId 或 x/y 时可省略"},
          "elementId":{"type":"string","description":"控件 id，来自 desktop_observe"},
          "kind":{"type":"string","enum":["Focus","MovePointer","LeftClick","RightClick","DoubleClick","KeyPress","TypeText","Scroll","ScrollIntoView","Drag"]},
          "x":{"type":"integer","description":"屏幕坐标 x"},
          "y":{"type":"integer","description":"屏幕坐标 y"},
          "toX":{"type":"integer","description":"拖拽终点屏幕坐标 x"},
          "toY":{"type":"integer","description":"拖拽终点屏幕坐标 y"},
          "virtualKey":{"type":"integer"},
          "text":{"type":"string"},
          "modifiers":{"type":"array","items":{"type":"string","enum":["shift","ctrl","alt","win"]},"description":"按住不放的修饰键，如 [\"ctrl\"]"},
          "scrollDelta":{"type":"integer","description":"滚轮格数：正数向上，负数向下，一次 3 格比较合适"},
          "offset":{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"}},"description":"控件内部的相对位置 0..1，不传就是控件中心"},
          "clickCount":{"type":"integer","enum":[1,2],"description":"1 = 单击，2 = 双击（仅 leftClick）"}
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
        // act only on what was seen — without making a slow human answer a failure. Only this window's UI
        // Automation tree is re-read: it is the one being acted on, and walking every window costs seconds.
        DesktopObservation fresh;
        try
        {
            fresh = automation.Observe(elementsFor: entityId);
        }
        catch (Exception error)
        {
            return new ToolResult(false, $"重新观察失败：{error.Message}");
        }

        if (Revalidate(seenWindow, fresh) is { } stale)
        {
            return new ToolResult(false, stale);
        }

        // The page's own controls, re-read for the same reason the UI Automation tree was: the element the
        // model named is a marker in a document that may have re-rendered, and a stale marker is reported by
        // the act rather than resolved to whatever now sits at that index.
        try
        {
            await automation.AddDomElementsAsync(entityId, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A browser that stopped answering leaves the previous answer in place, which is what the re-check
            // above will then refuse against — a stale control, refused rather than clicked.
        }

        var target = fresh.Find(entityId)!;

        if (seenElement is not null && RevalidateElement(seenElement, fresh) is { } staleElement)
        {
            return new ToolResult(false, staleElement);
        }

        var element = seenElement is null ? null : fresh.FindElement(seenElement.ElementId);

        // Not a refusal: the window is the same window, it just moved or retitled. Reported so the model knows
        // its picture is stale — without spending a step re-observing to find out.
        var drift = DescribeDrift(seenWindow, target);

        // A named control carries its own click point: the model should not have to do geometry, and the point
        // is inside the window by construction. An offset moves it within the control — the only case where
        // "inside" is not enough, because a split button's arrow and its text are one element.
        var resolved = element is null
            ? point
            : Within(element, arguments.Offset);

        if (Guard(kind, target, resolved, arguments) is { } blocked)
        {
            return new ToolResult(false, blocked);
        }

        // A control that is below the fold has a click point outside the window, so every containment check
        // would either refuse it for the wrong reason or, worse, pass once the window is tall enough. Scrolling
        // it into view is the action that means "make this reachable", and the control reports the distance.
        if (element is { IsOffscreen: true }
            && kind is not (ActionKind.ScrollIntoView or ActionKind.Focus))
        {
            return new ToolResult(
                false,
                $"'{element.Name}' [{element.Role}] 不在可见区域（点击点 ({element.Centre.X},{element.Centre.Y}) 在窗口外）："
                + $"先 desktop_act kind=scrollIntoView elementId={element.ElementId}，滚进视野后再操作");
        }

        var parameters = new ActionParams
        {
            X = resolved?.X ?? 0,
            Y = resolved?.Y ?? 0,
            Within = arguments.Offset is { } offset && element is not null ? offset : null,
            ClickCount = arguments.ClickCount ?? 1,
            ElementId = seenElement?.ElementId,
            VirtualKey = arguments.VirtualKey ?? 0,
            Text = arguments.Text,
            Modifiers = Modifiers(arguments.Modifiers),
            ScrollDelta = arguments.ScrollDelta ?? 0,
            ToX = arguments.ToX ?? 0,
            ToY = arguments.ToY ?? 0,
        };

        var action = await automation.ActAsync(entityId, kind, parameters, ct).ConfigureAwait(false);

        var described = Describe(action, kind, target, element);

        return new ToolResult(action.GuardPassed, drift is null ? described : $"{described} · {drift}");
    }

    /// <summary>
    /// Whether a fresh observation still shows the same window the decision was made on. Returns the reason it
    /// does not, or null when it is safe to act.
    /// <para>
    /// The bar is identity, not immobility. The window must still be the same window — same handle, same
    /// process, same process start time — so a recycled handle (the old window closed and Windows handed the
    /// handle to a new one) is caught. It is <b>not</b> required to have kept its title or its rectangle: a
    /// music player retitles itself on every track, a browser on every tab change, an editor on its dirty
    /// marker, and restoring a minimized window moves it by definition. Treating those as a mismatch is how
    /// "act only on what you saw" degenerates into "you may never act" on exactly the applications the model
    /// is pointed at. What did change is reported by <see cref="DescribeDrift"/>; the coordinates are
    /// recomputed from the fresh rectangle wherever that matters.
    /// </para>
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

        return now.IdentityDigest == seen.IdentityDigest
            ? null
            : $"句柄 {seen.EntityId} 现在属于别的窗口了（观察时是 {seen.Value ?? "?"}，现在是 {now.Value ?? "?"}）："
                + "重新 desktop_observe 再决定";
    }

    /// <summary>
    /// What moved or retitled since the decision, as a note to keep in the result.
    /// <para>
    /// Deliberately not a refusal. A screen coordinate stays valid as long as it still lands inside the window
    /// it names — which the guard checks against the <i>fresh</i> rectangle — and an element-addressed action
    /// recomputes its click point from the fresh rectangle as well. Saying it out loud is what lets the model
    /// notice that its picture is stale, without costing it a step.
    /// </para>
    /// </summary>
    public static string? DescribeDrift(UiEntity seen, UiEntity now)
    {
        ArgumentNullException.ThrowIfNull(seen);
        ArgumentNullException.ThrowIfNull(now);

        var notes = new List<string>(2);

        if (!string.Equals(seen.Label, now.Label, StringComparison.Ordinal))
        {
            notes.Add($"标题从 '{seen.Label}' 变成 '{now.Label}'");
        }

        if (seen.LayoutDigest != now.LayoutDigest)
        {
            notes.Add($"矩形从 ({seen.Rect.X},{seen.Rect.Y}) {seen.Rect.Width}×{seen.Rect.Height} "
                + $"变成 ({now.Rect.X},{now.Rect.Y}) {now.Rect.Width}×{now.Rect.Height}");
        }

        return notes.Count == 0 ? null : $"注意：窗口已变化（{string.Join("；", notes)}）";
    }

    /// <summary>
    /// The same check for a named control. Identity is name + role, not the rectangle: a layout shift moves a
    /// control without replacing it, and the click point is recomputed from the fresh rectangle anyway.
    /// </summary>
    public static string? RevalidateElement(UiElement seen, DesktopObservation fresh)
    {
        ArgumentNullException.ThrowIfNull(seen);
        ArgumentNullException.ThrowIfNull(fresh);

        var now = fresh.FindElement(seen.ElementId);
        if (now is null)
        {
            return $"控件 '{seen.Name}' 已经不在了：重新 desktop_observe 再决定";
        }

        return now.IdentityDigest == seen.IdentityDigest
            ? null
            : $"控件 '{seen.Name}' 与观察时不同（名称或类型变了，现在是 '{now.Name}' [{now.Role}]）："
                + "重新 desktop_observe 再决定";
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
        ActionKind.MovePointer or ActionKind.LeftClick or ActionKind.RightClick or ActionKind.Scroll
            or ActionKind.Drag or ActionKind.DoubleClick
            when target.IsMinimized =>
            $"'{target.Label}' 已最小化（矩形是 -32000 处的占位值）：先对它执行 focus 还原，再操作",

        ActionKind.MovePointer or ActionKind.LeftClick or ActionKind.RightClick or ActionKind.Scroll
            or ActionKind.DoubleClick when point is null =>
            $"{kind} 需要 elementId 或 x/y（屏幕坐标）",

        ActionKind.ScrollIntoView when arguments.ElementId is not { Length: > 0 } =>
            "scrollIntoView 需要 elementId：滚动到某个控件，由控件自己报距离，不需要猜滚多少",

        ActionKind.Drag when point is null || arguments.ToX is null || arguments.ToY is null =>
            "drag 需要 x/y（起点）+ toX/toY（终点），都是屏幕坐标",

        ActionKind.KeyPress or ActionKind.TypeText when !target.IsForeground =>
            $"'{target.Label}' 不在前台，输入会打到别的窗口：先对它执行 focus",

        ActionKind.MovePointer or ActionKind.LeftClick or ActionKind.RightClick or ActionKind.Scroll
            or ActionKind.DoubleClick
            when !target.Rect.Contains(point!.Value.X, point.Value.Y) =>
            $"({point!.Value.X},{point.Value.Y}) 不在 '{target.Label}' 的矩形内：坐标是屏幕坐标，会点到别处",

        ActionKind.Drag when !target.Rect.Contains(point!.Value.X, point.Value.Y)
            || !target.Rect.Contains(arguments.ToX!.Value, arguments.ToY!.Value) =>
            $"拖拽的起点或终点不在 '{target.Label}' 的矩形内",

        ActionKind.KeyPress when arguments.VirtualKey is null or 0 => "keyPress 需要 virtualKey",
        ActionKind.TypeText when string.IsNullOrEmpty(arguments.Text) => "typeText 需要 text",
        ActionKind.Scroll when arguments.ScrollDelta is null or 0 => "scroll 需要 scrollDelta",

        ActionKind.LeftClick or ActionKind.DoubleClick
            when arguments.ClickCount is { } count && count is not (1 or 2) =>
            $"clickCount 只能是 1（单击）或 2（双击），收到 {count}",

        _ when arguments.Offset is { } offset && !offset.IsInside =>
            $"offset ({offset.X:0.###},{offset.Y:0.###}) 不在 0..1 之间：它是控件内部的相对位置，超出就不是这个控件了",

        _ when UnknownModifiers(arguments.Modifiers) is { Count: > 0 } unknown =>
            $"修饰键只能是 {string.Join("/", InputInjector.ModifierKeys.Keys)}，不认识：{string.Join("、", unknown)}",

        _ => null,
    };

    /// <summary>
    /// Where inside a control to click, given as a fraction of its size.
    /// <para>
    /// Both ends are inside the control by construction — a fraction below 0 or above 1 is not clamped but
    /// refused by the guard, because a clamped offset is an offset the caller did not ask for, landing
    /// somewhere they did not name. And the fallback is the centre, never a corner of the screen: "this
    /// control" with no position in it means the middle of the control.
    /// </para>
    /// </summary>
    public static (int X, int Y) Within(UiElement element, RelativePoint? offset)
    {
        var at = offset ?? RelativePoint.Centre;
        return (
            element.Rect.X + (int)Math.Round(element.Rect.Width * at.X),
            element.Rect.Y + (int)Math.Round(element.Rect.Height * at.Y));
    }

    /// <summary>
    /// The modifier names a caller wrote, as virtual keys — and the ones that are not modifiers are refused
    /// rather than passed on.
    /// <para>
    /// Silently dropping a misspelt modifier would send a plain "C" and report a Ctrl+C; and a name that is a
    /// real key ("a") is not a modifier at all, so it must not become "Ctrl held down, then nothing".
    /// </para>
    /// </summary>
    public static IReadOnlyList<ushort> Modifiers(IReadOnlyList<string>? names)
    {
        if (names is not { Count: > 0 })
        {
            return Array.Empty<ushort>();
        }

        return names
            .Where(name => InputInjector.ModifierKeys.ContainsKey(name ?? string.Empty))
            .Select(name => InputInjector.ModifierKeys[name])
            .ToList();
    }

    /// <summary>Which of the modifier names a caller wrote are not modifiers.</summary>
    public static IReadOnlyList<string> UnknownModifiers(IReadOnlyList<string>? names) =>
        names is not { Count: > 0 }
            ? Array.Empty<string>()
            : names.Where(name => !InputInjector.ModifierKeys.ContainsKey(name ?? string.Empty)).ToList();

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

        var viaPattern = action.Injection?.Detail.StartsWith("uia.", StringComparison.Ordinal) == true;
        var injected = action.Injection?.Ok == true
            ? viaPattern ? "已通过界面自动化执行" : "注入成功"
            : $"{(viaPattern ? "自动化调用失败" : "注入失败")}（{action.Injection?.Detail}）";

        // The user's foreground window moved, so it is said out loud rather than left for them to notice.
        var raised = action.Raised ? " · 目标原来被挡住，已把它带到前台" : string.Empty;

        var verified = action.PostVerify?.Decision switch
        {
            VerifyDecision.AnyChange => "校验：画面已变化",

            // The one sentence an agent cannot act on, so it carries what to do about it. "Nothing changed" is
            // three different problems — the click missed, the control needs a hover first, or the effect is
            // somewhere this measurement does not look — and the result says which to try rather than leaving
            // the next turn to guess.
            VerifyDecision.NoChange => action.PostVerify?.IntersectedAreaRatio > 0
                ? "校验：画面有变化但不在目标控件上（点偏了或效果在别处）"
                : "校验：画面无变化（效果未被确认）· 可以试：先 movePointer 到它再点；或用 scrollIntoView 先滚进视野再点；或重新 desktop_observe 拿新的点击点",

            VerifyDecision.Indeterminate => $"校验：无法判断（{action.PostVerify?.Reason}）",
            _ => "校验：未执行",
        };

        return $"{kind} · {injected}{raised} · {verified} · 目标 {who}";
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
        int? ScrollDelta,
        IReadOnlyList<string>? Modifiers = null,
        RelativePoint? Offset = null,
        int? ClickCount = null);
}
