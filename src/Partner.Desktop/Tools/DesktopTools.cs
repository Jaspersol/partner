using System.Text.Json;
using Partner.Core.Agent.Tools;
using Partner.Desktop.Input;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tools;

/// <summary>
/// Which windows the model may act on.
/// <para>
/// An empty list means any window. The gate that actually protects the user is approval on the action itself;
/// a title filter that matched nothing would look like a broken tool rather than a policy, so this is for
/// narrowing an installation, not for being the only guard.
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
/// Lists the windows on the desktop and issues the leases an action must carry.
/// <para>
/// Read-only: it never injects input, which is why it needs no approval. Its result is also the only way to
/// obtain an <c>entityId</c>, so acting is always preceded by looking.
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
        "列出当前桌面的可见窗口：实体 id、标题、进程与矩形。动作只能针对这里返回的 entityId。"
        + "若你要操作的程序不在列表里，它就没在运行：不要对别的窗口动手，先想办法把它打开（例如用键盘启动器）。",
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
            })
            .ToList();

        return Task.FromResult(new ToolResult(
            true,
            JsonSerializer.Serialize(new { obsId = observation.ObsId, count = entities.Count, entities }, Json)));
    }
}

/// <summary>
/// Performs one action against a window observed by <see cref="DesktopObserveTool"/>.
/// <para>
/// Irreversible: it moves the user's pointer or sends keystrokes. Governance puts it in the <c>ask</c> list, so
/// every call reaches a human before it reaches the desktop. Two things follow from that wait:
/// </para>
/// <list type="bullet">
///   <item><b>The target is re-checked, not the lease.</b> A human can take minutes to answer, which outlives
///   any sane lease; refusing then would make approval useless. So the window is observed again and the action
///   proceeds only if it is still the same window (title, process, geometry) — the lease's meaning, without
///   its clock.</item>
///   <item><b>Global inputs are guarded.</b> Keystrokes go to whatever has focus and clicks go to whatever is
///   at those coordinates, so an action that would land outside the window it names is refused before
///   anything is injected.</item>
/// </list>
/// </summary>
public sealed class DesktopActTool(DesktopAutomation automation, DesktopToolPolicy policy) : ITool
{
    public ToolSpec Spec { get; } = new(
        "desktop_act",
        "对最近一次 desktop_observe 返回的某个窗口执行一次动作。kind 取 focus/movePointer/leftClick/rightClick/keyPress；"
        + "坐标动作需要 x/y（必须落在该窗口矩形内），按键需要 virtualKey（要求该窗口已在前台，否则先 focus）。"
        + "执行前会重新观察并核对目标是否仍是同一个窗口；租约过期不会直接失败。"
        + "目标必须就是你要操作的那个窗口：按键只会打到前台窗口，坐标必须落在该窗口矩形内，否则会被拒绝。",
        """
        {"type":"object","properties":{
          "entityId":{"type":"string"},
          "kind":{"type":"string","enum":["Focus","MovePointer","LeftClick","RightClick","KeyPress"]},
          "x":{"type":"integer"},
          "y":{"type":"integer"},
          "virtualKey":{"type":"integer"}
        },"required":["entityId","kind"],"additionalProperties":false}
        """);

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        DesktopActArguments? arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<DesktopActArguments>(argumentsJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException error)
        {
            return new ToolResult(false, $"参数不是合法 JSON：{error.Message}");
        }

        if (arguments is null || string.IsNullOrWhiteSpace(arguments.EntityId))
        {
            return new ToolResult(false, "缺少 entityId：先调用 desktop_observe");
        }

        if (!Enum.TryParse<ActionKind>(arguments.Kind, ignoreCase: true, out var kind))
        {
            return new ToolResult(false, $"未知动作 '{arguments.Kind}'；可用：{string.Join(", ", Enum.GetNames<ActionKind>())}");
        }

        // What the model decided on. Kept so the re-observation below can tell "same window" from "some other
        // window that happens to reuse the id".
        var seen = automation.LastObservation?.Find(arguments.EntityId);
        if (seen is null)
        {
            return new ToolResult(false, $"未知实体 '{arguments.EntityId}'：先调用 desktop_observe");
        }

        if (!policy.Allows(seen.Label))
        {
            return new ToolResult(false, $"窗口 '{seen.Label}' 不在允许列表内（设置里的桌面段）");
        }

        // Approval can outlive the lease by minutes. Re-observing and comparing content keeps the guarantee the
        // lease was for — act only on the window that was seen — without making a slow human answer a failure.
        DesktopObservation fresh;
        try
        {
            fresh = automation.Observe();
        }
        catch (Exception error)
        {
            return new ToolResult(false, $"重新观察失败：{error.Message}");
        }

        if (Revalidate(seen, fresh) is { } stale)
        {
            return new ToolResult(false, stale);
        }

        var target = fresh.Find(arguments.EntityId)!;

        if (Guard(kind, target, arguments) is { } blocked)
        {
            return new ToolResult(false, blocked);
        }

        var action = await automation
            .ActAsync(
                arguments.EntityId,
                kind,
                new ActionParams { X = arguments.X, Y = arguments.Y, VirtualKey = arguments.VirtualKey },
                ct)
            .ConfigureAwait(false);

        return new ToolResult(action.GuardPassed, Describe(action, kind, target));
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

    /// <summary>
    /// What would make this action land somewhere other than the window it names. Pointer and key input are
    /// global — the OS sends them to a coordinate or to whatever has focus — so without these the model can
    /// type into the user's editor while believing it is talking to another window.
    /// </summary>
    public static string? Guard(ActionKind kind, UiEntity target, DesktopActArguments arguments) => kind switch
    {
        ActionKind.KeyPress when !target.IsForeground =>
            $"'{target.Label}' 不在前台，按键会打到别的窗口：先对它执行 focus",

        ActionKind.MovePointer or ActionKind.LeftClick or ActionKind.RightClick
            when !target.Rect.Contains(arguments.X, arguments.Y) =>
            $"({arguments.X},{arguments.Y}) 不在 '{target.Label}' 的矩形内：坐标是屏幕坐标，会点到别处",

        _ => null,
    };

    /// <summary>
    /// The result, with injection and verification kept apart: they answer different questions, and collapsing
    /// them into one word is how "it clicked" and "nothing visibly happened" end up looking identical.
    /// </summary>
    private static string Describe(DesktopAction action, ActionKind kind, UiEntity target)
    {
        var who = $"{target.Label}（{target.Value ?? "?"}）";

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
    public sealed record DesktopActArguments(string? EntityId, string? Kind, int X, int Y, ushort VirtualKey);
}
