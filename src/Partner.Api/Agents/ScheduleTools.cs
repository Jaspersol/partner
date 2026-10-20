using System.Globalization;
using System.Text;
using System.Text.Json;
using Partner.Api.Endpoints;
using Partner.Core.Agent.Tools;
using Partner.Core.Scheduler;

namespace Partner.Api.Agents;

/// <summary>
/// The assistant's side of the timed-tasks page: it can set up, read and remove the same tasks a person can.
/// <para>
/// Three things shape these tools. The model has no clock, so every result says what time it is now (the machine's
/// own zone, the same one cron is read in) and a one-off can be given as "N minutes from now" instead of a date it
/// would have to work out. The rules are <see cref="ScheduleRules"/> — the page's, not a copy. And the message a
/// task carries comes back later as if the person had typed it, so creating or deleting one asks a human first
/// (see <c>DesktopGovernance</c>): a task is a standing instruction, and a web page the assistant read must not be
/// able to leave one behind.
/// </para>
/// </summary>
internal static class ScheduleToolNames
{
    public const string Create = "schedule_create";
    public const string List = "schedule_list";
    public const string Delete = "schedule_delete";
}

/// <summary>Sets up a timed task.</summary>
public sealed class ScheduleCreateTool(ApiContext context) : ITool
{
    public const string ToolName = ScheduleToolNames.Create;

    public ToolSpec Spec { get; } = new(
        ToolName,
        "创建一个定时任务：到点时，下面的 message 会作为一条新消息发给你自己，你像收到用户的话一样去处理。"
        + "到点时你看不到现在这场对话，所以 message 要写完整、自包含（做什么、对谁、要什么结果）。"
        + "kind 三选一：cron（重复，按“分 时 日 月 星期”五段，本机本地时间，如 0 9 * * 1-5 是工作日 9 点）、"
        + "interval（每隔 intervalMinutes 分钟重复）、once（只一次：用 delayMinutes 表示“从现在起多少分钟后”，"
        + "或用 at 写本地时间 yyyy-MM-dd HH:mm）。不知道现在几点就用 delayMinutes，或先调用 schedule_list 看当前时间。",
        """
        {"type":"object","properties":{
          "message":{"type":"string","description":"到点时发给你自己的那句话，写完整"},
          "kind":{"type":"string","enum":["cron","interval","once"]},
          "cron":{"type":"string","description":"kind=cron 时：五段 cron，本机本地时间"},
          "intervalMinutes":{"type":"integer","minimum":1,"description":"kind=interval 时：间隔分钟数"},
          "delayMinutes":{"type":"integer","minimum":1,"description":"kind=once 时：从现在起多少分钟后执行"},
          "at":{"type":"string","description":"kind=once 时：本地时间 yyyy-MM-dd HH:mm（与 delayMinutes 二选一）"}
        },"required":["message","kind"]}
        """);

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var message = ScheduleArgs.Text(argumentsJson, "message");
        var kind = ScheduleArgs.Text(argumentsJson, "kind");
        if (message is null || kind is null)
        {
            return new ToolResult(false, "invalid_args: 需要 message 和 kind");
        }

        var now = context.Clock.GetUtcNow();
        DateTimeOffset? at = null;

        if (kind.Equals("once", StringComparison.OrdinalIgnoreCase))
        {
            var delay = ScheduleArgs.Number(argumentsJson, "delayMinutes");
            var atText = ScheduleArgs.Text(argumentsJson, "at");

            if (delay is not null && atText is not null)
            {
                return new ToolResult(false, "invalid_args: delayMinutes 和 at 只能给一个");
            }

            if (delay is { } minutes)
            {
                if (minutes < 1 || minutes > ScheduleRules.MaxIntervalMinutes)
                {
                    return new ToolResult(false, $"invalid_args: delayMinutes 要在 1 到 {ScheduleRules.MaxIntervalMinutes} 之间");
                }

                at = now.AddMinutes(minutes);
            }
            else if (atText is not null)
            {
                // Read in the machine's zone unless the text carries its own offset: "9:00" from the model means
                // 9:00 on the wall clock of the person it is working for.
                if (!DateTimeOffset.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                {
                    return new ToolResult(false, $"invalid_args: 无法理解时间“{atText}”，请写成 yyyy-MM-dd HH:mm（本地时间）");
                }

                at = parsed;
            }
            else
            {
                return new ToolResult(false, "invalid_args: kind=once 需要 delayMinutes 或 at");
            }
        }

        var request = new ScheduleRequest(
            kind,
            ScheduleArgs.Text(argumentsJson, "cron"),
            ScheduleArgs.Number(argumentsJson, "intervalMinutes"),
            at,
            message);

        var (task, error) = await ScheduleRules.CreateAsync(context.Scheduler, request, now, ct).ConfigureAwait(false);
        if (task is null)
        {
            // The current time rides along on a refusal about time, because that is what the model needs to try again.
            return new ToolResult(false, $"refused: {error}（现在是 {ScheduleRules.Local(now)}，本机时间）");
        }

        return new ToolResult(
            true,
            $"已创建定时任务 {task.TaskId}：{ScheduleRules.Describe(task)}，下次执行 {ScheduleRules.Local(task.NextScheduledAt)}"
            + $"（本机时间；现在是 {ScheduleRules.Local(now)}）。到点时会收到这条消息：{task.Payload}");
    }
}

/// <summary>Lists the timed tasks, and says what time it is.</summary>
public sealed class ScheduleListTool(ApiContext context) : ITool
{
    public const string ToolName = ScheduleToolNames.List;

    private const int PreviewLength = 60;

    public ToolSpec Spec { get; } = new(
        ToolName,
        "列出所有定时任务（编号、规则、状态、下次执行时间、内容），并告诉你现在的本机时间。创建或删除之前先看一眼，避免重复。",
        """{"type":"object","properties":{}}""");

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var now = context.Clock.GetUtcNow();
        var tasks = await context.Scheduler.ListAsync(ct).ConfigureAwait(false);
        var header = $"现在是 {ScheduleRules.Local(now)}（本机时间）。";

        if (tasks.Count == 0)
        {
            return new ToolResult(true, header + "没有定时任务。");
        }

        var text = new StringBuilder(header).Append($"共 {tasks.Count} 个定时任务：");
        foreach (var task in tasks)
        {
            text.Append("\n- ").Append(task.TaskId).Append(" · ").Append(ScheduleRules.Describe(task)).Append(" · ").Append(State(task));
            text.Append(" · ").Append(Preview(task.Payload));
        }

        return new ToolResult(true, text.ToString());
    }

    private static string State(ScheduleTask task) => task.Status switch
    {
        ScheduleTaskStatus.Inactive => task.Trigger is OnceTrigger && task.LastDeliveryAt is not null ? "已执行" : "已暂停",
        ScheduleTaskStatus.Failed => $"投递失败，会重试（{task.LastError}）",
        _ => $"下次 {ScheduleRules.Local(task.NextScheduledAt)}",
    };

    private static string Preview(string payload)
    {
        var line = payload.ReplaceLineEndings(" ");
        return line.Length <= PreviewLength ? line : line[..PreviewLength] + "…";
    }
}

/// <summary>Removes a timed task.</summary>
public sealed class ScheduleDeleteTool(ApiContext context) : ITool
{
    public const string ToolName = ScheduleToolNames.Delete;

    public ToolSpec Spec { get; } = new(
        ToolName,
        "删除一个定时任务，不可恢复。编号用 schedule_list 查。",
        """{"type":"object","properties":{"id":{"type":"string","description":"定时任务编号，如 sch-1a2b3c4d"}},"required":["id"]}""");

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var id = ScheduleArgs.Text(argumentsJson, "id");
        if (id is null)
        {
            return new ToolResult(false, "invalid_args: 需要 id");
        }

        return await context.Scheduler.RemoveAsync(id, ct).ConfigureAwait(false)
            ? new ToolResult(true, $"已删除定时任务 {id}。")
            : new ToolResult(false, $"not_found: 没有定时任务 {id}");
    }
}

internal static class ScheduleArgs
{
    public static string? Text(string json, string name) => Delegation.Text(json, name);

    /// <summary>A whole number, tolerating the model writing it as a string ("30") — which they do.</summary>
    public static int? Number(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var whole) => whole,
                JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
