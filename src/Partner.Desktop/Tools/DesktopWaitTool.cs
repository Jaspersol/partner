using System.Text.Json;
using Partner.Core.Agent.Tools;
using Partner.Desktop.Interop;
using Partner.Desktop.Perception;
using Partner.Desktop.Windows;

namespace Partner.Desktop.Tools;

/// <summary>
/// Waits, with a deadline, instead of observing on a loop.
/// <para>
/// Three conditions a GUI places in the model's way, each with one way to wait for it: a control that has
/// not rendered yet (its UIA tree: poll a fresh walk of that one window); a window that has not opened yet
/// (the window list, which is a few milliseconds per ask); a pane that has not finished repainting (two
/// hash-identical frames in a row — <see cref="StabilitySensor"/>). Read-only, so it never needs approval;
/// and bounded by a hard timeout, so a slow page spends seconds, not steps.
/// </para>
/// <para>
/// Deliberately bounded at <see cref="TimeoutCap"/> rather than configurable without limit: this is a
/// budget, not a promise — the turn is single-reader and the queue behind it does not notice waiting that
/// exceeds the cap. A page that needs longer is a desktop activity the model should look at again, not
/// one the tool parks forever.
/// </para>
/// </summary>
public sealed class DesktopWaitTool(DesktopAutomation automation) : ITool
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The cap a wait can request: budget, never a guarantee.</summary>
    internal static readonly TimeSpan TimeoutCap = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ElementPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WindowPollInterval = TimeSpan.FromMilliseconds(100);

    public ToolSpec Spec { get; } = new(
        "desktop_wait",
        "等待桌面上的一个条件，最多 5 秒：控件出现（kind=element，entityId + elementId）、窗口出现或消失在列表里"
        + "（kind=window，titleContains）、画面停止变化（kind=stillFrame，entityId；两帧哈希相同才算稳）。"
        + "只读，永不审批；超时就报「未达成」并给出当前状态——慢页面不该烧掉观察步数。",
        """
        {"type":"object","properties":{
          "kind":{"type":"string","enum":["element","window","stillFrame"]},
          "entityId":{"type":"string","description":"窗口 id（element/stillFrame 必需；来自 desktop_observe）"},
          "elementId":{"type":"string","description":"要等的控件 id（element 时必需）"},
          "titleContains":{"type":"string","description":"要等的窗口标题包含的词（window 时必需）"},
          "timeoutMs":{"type":"integer","description":"最长等待毫秒数，默认 3000，硬顶 5000"},
          "stableRounds":{"type":"integer","enum":[1,2],"description":"stillFrame 需要连续多少份相同帧才算稳定，默认 2；1 = 只要一帧（更快但更容易误判）"}
        },"required":["kind"],"additionalProperties":false}
        """);

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var arguments = JsonSerializer.Deserialize<WaitArguments>(argumentsJson, Json);
        if (arguments is null)
        {
            return new ToolResult(false, "参数不是合法 JSON");
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(arguments.TimeoutMs ?? 3000, 50, TimeoutCap.TotalMilliseconds));

        return arguments.Kind switch
        {
            "element" => await WaitForElement(arguments, timeout, ct).ConfigureAwait(false),
            "window" => await WaitForWindowAsync(arguments, timeout, ct).ConfigureAwait(false),
            "stillFrame" => await WaitForStill(arguments, arguments.StableRounds is 1 ? 1 : 2, timeout, ct).ConfigureAwait(false),
            _ => new ToolResult(false, $"未知 kind '{arguments.Kind}'；可用：element / window / stillFrame"),
        };
    }

    /// <summary>
    /// A control in a window that has not painted it yet. Re-walks <b>only that window's</b> tree on the
    /// poll — the same aimed walk the act path uses — and the successful answer carries a fresh
    /// observation, so the elementIds the model holds next are current, not the stale batch it waited on.
    /// </summary>
    private async Task<ToolResult> WaitForElement(WaitArguments arguments, TimeSpan timeout, CancellationToken ct)
    {
        if (arguments.EntityId is not { Length: > 0 } entityId)
        {
            return new ToolResult(false, "element 需要 entityId（等哪个窗口里的控件）");
        }

        if (arguments.ElementId is not { Length: > 0 } elementId)
        {
            return new ToolResult(false, "element 需要 elementId（等哪个控件：来自 desktop_observe）");
        }

        var seenEntity = automation.LastObservation?.Find(entityId);
        if (seenEntity is null)
        {
            return new ToolResult(false, $"未知窗口 '{entityId}'：先调用 desktop_observe");
        }

        var startActivity = Environment.TickCount64;
        var deadline = startActivity + (long)timeout.TotalMilliseconds;
        var pause = ElementPollInterval;
        while (true)
        {
            // Polling only reads the one window's tree. The full observation (window list, a new generation, leases)
            // is made once, when the control has shown up or the time is up.
            bool appeared;
            try
            {
                appeared = automation.ProbeElements(entityId).Any(candidate => candidate.ElementId == elementId);
            }
            catch (Exception error)
            {
                return new ToolResult(false, $"重新观察失败：{error.Message}");
            }

            var due = Environment.TickCount64 >= deadline;
            if (!appeared && !due)
            {
                await Task.Delay(pause, ct).ConfigureAwait(false);

                // Backing off: a control that has not appeared after a second is not about to appear in the next
                // 250 ms, and each probe is a walk of a window's whole tree.
                pause = TimeSpan.FromMilliseconds(Math.Min(pause.TotalMilliseconds * 2, 1000));
                continue;
            }

            DesktopObservation fresh;
            try
            {
                fresh = automation.Observe(elementsFor: entityId);
            }
            catch (Exception error)
            {
                return new ToolResult(false, $"重新观察失败：{error.Message}");
            }

            if (fresh.FindElement(elementId) is { } found)
            {
                return new ToolResult(
                    true,
                    $"控件已出现：'{found.Name}' [{found.Role}] elementId {found.ElementId}，"
                    + $"点击点屏幕 ({found.Centre.X},{found.Centre.Y})，等待 {Environment.TickCount64 - startActivity}ms。"
                    + (found.IsOffscreen ? "它在视野外：先 scrollIntoView 再点名。" : string.Empty)
                    + "已收敛出新的观察，desktop_act 可直接用新 elementId。");
            }

            if (Environment.TickCount64 >= deadline)
            {
                var there = fresh.Elements.Where(candidate => candidate.WindowId == entityId);
                return new ToolResult(false,
                    $"等待 {timeout.TotalMilliseconds:0}ms 后 '{elementId}' 仍不在这个窗口的控件里（现在有 {there.Count()} 个）。"
                    + "不是还没渲染，就是要等的 id 属于上一轮观察（id 是每次观察的新编号，重新 desktop_observe 拿新的）。");
            }

            // It showed up in the probe but not in the observation (the window changed in between): look again.
            await Task.Delay(pause, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A window by title fragment. Answered with the window's own id, so a wait for the launch of a slow
    /// app flows straight into acting on that app instead of a re-observe to get ids.
    /// </summary>
    private async Task<ToolResult> WaitForWindowAsync(WaitArguments arguments, TimeSpan timeout, CancellationToken ct)
    {
        if (arguments.TitleContains is not { Length: > 0 } fragment)
        {
            return new ToolResult(false, "window 需要 titleContains（等哪个标题）");
        }

        var startActivity = Environment.TickCount64;
        var deadline = startActivity + (long)timeout.TotalMilliseconds;

        while (true)
        {
            var windows = WindowEnumerator.List();
            var match = windows.FirstOrDefault(window =>
                (window.Title ?? string.Empty).Contains(fragment, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return new ToolResult(true,
                    $"窗口已出现：'{match.Title}'（{match.ProcessName}）entityId {match.Handle:x} @ "
                    + $"{match.X},{match.Y} {match.Width}×{match.Height}，等待 {Environment.TickCount64 - startActivity}ms。"
                    + "desktop_act 可直接用这个 entityId。");
            }

            if (Environment.TickCount64 >= deadline)
            {
                var sample = string.Join("；", windows.Take(6)
                    .Select(window => $"'{window.Title}'（{window.ProcessName}）"));
                return new ToolResult(false,
                    $"等待 {timeout.TotalMilliseconds:0}ms 后没有标题含 '{fragment}' 的窗口。现在开着：{sample}"
                    + (windows.Count > 6 ? $"；…共 {windows.Count} 个" : string.Empty));
            }

            await Task.Delay(WindowPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pixels that stopped moving. <paramref name="requiredRounds"/> of 2 is the default because one
    /// identical frame hash is what a mid-repaint sample looks like; 1 is offered for fast change with a
    /// tolerant caller.
    /// </summary>
    private async Task<ToolResult> WaitForStill(WaitArguments arguments, int requiredRounds, TimeSpan timeout, CancellationToken ct)
    {
        if (arguments.EntityId is not { Length: > 0 } entityId)
        {
            return new ToolResult(false, "stillFrame 需要 entityId（等哪个窗口的画面）");
        }

        var entity = automation.LastObservation?.Find(entityId);
        if (entity is null)
        {
            return new ToolResult(false, $"未知窗口 '{entityId}'：先调用 desktop_observe");
        }

        var handle = nint.Parse(entityId, System.Globalization.NumberStyles.HexNumber);

        if (NativeMethods.IsIconic(handle))
        {
            return new ToolResult(false, $"'{entity?.Label}' 已最小化（没有像素可测）：先 desktop_act focus 还原它");
        }

        var sensor = new StabilitySensor(automation.Capture);
        var (stable, waitedMs, width, height, rounds) = await sensor
            .WaitStillAsync(handle, timeout, ct).ConfigureAwait(false);

        return stable
            ? new ToolResult(true,
                $"画面已稳定：{rounds} 轮哈希相同 / {waitedMs:0}ms（{width}×{height}）。可以放心读图、读数、点。")
            : new ToolResult(false,
                $"等待 {timeout.TotalMilliseconds:0}ms 后画面仍在变化（{rounds} 轮）。加载中的页面会一直这样；"
                + "延长 timeout（≤5s）或先确认这个窗口上到底发生了什么。");
    }

    /// <summary>What the model asked for, as it arrives from the model.</summary>
    private sealed record WaitArguments(string? Kind, string? EntityId, string? ElementId, string? TitleContains, int? TimeoutMs, int? StableRounds);
}
