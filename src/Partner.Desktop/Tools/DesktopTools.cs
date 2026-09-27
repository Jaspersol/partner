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
        "列出当前桌面的可见窗口：实体 id、标题、进程与矩形。动作只能针对这里返回的 entityId，且必须用最近一次观察发出的租约。",
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
/// every call reaches a human before it reaches the desktop, and the lease the observation issued bounds how
/// long that decision stays actionable.
/// </para>
/// </summary>
public sealed class DesktopActTool(DesktopAutomation automation, DesktopToolPolicy policy) : ITool
{
    public ToolSpec Spec { get; } = new(
        "desktop_act",
        "对最近一次 desktop_observe 返回的某个窗口执行一次动作。kind 取 focus/movePointer/leftClick/rightClick/keyPress；"
        + "坐标动作需要 x/y，按键需要 virtualKey。租约过期、实体未知或窗口不在允许列表内都会被拒绝（不做任何注入）。",
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

        // The title check happens here rather than in governance: only this layer can look the entity up, and a
        // window that is not allowed must be refused *before* any injection, not after.
        var entity = automation.LastObservation?.Find(arguments.EntityId);
        if (entity is null)
        {
            return new ToolResult(false, $"未知实体 '{arguments.EntityId}'：先调用 desktop_observe（租约随观察更新）");
        }

        if (!policy.Allows(entity.Label))
        {
            return new ToolResult(false, $"窗口 '{entity.Label}' 不在允许列表内（设置里的桌面段）");
        }

        var action = await automation
            .ActAsync(
                arguments.EntityId,
                kind,
                new ActionParams { X = arguments.X, Y = arguments.Y, VirtualKey = arguments.VirtualKey },
                ct)
            .ConfigureAwait(false);

        var detail = action.GuardPassed
            ? $"注入={action.Injection?.Ok} 校验={action.PostVerify?.Decision}"
            : $"被拒绝：{action.GuardReason}";

        return new ToolResult(action.GuardPassed, $"{action.ActionId} {kind} → {detail}");
    }

    private sealed record DesktopActArguments(string? EntityId, string? Kind, int X, int Y, ushort VirtualKey);
}
