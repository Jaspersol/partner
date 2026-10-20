using System.Globalization;
using System.Text.RegularExpressions;
using Partner.Core.Scheduler;

namespace Partner.Api.Endpoints;

/// <summary>
/// What a timed task has to satisfy to be stored, and how one reads in words. Shared by the HTTP endpoints and by
/// the assistant's own tools on purpose: two entry points with two copies of "how short may an interval be" is
/// how the assistant ends up able to create what the page would have refused.
/// </summary>
public static class ScheduleRules
{
    /// <summary>A guard against a runaway client, not a product limit anyone should meet.</summary>
    public const int MaxTasks = 100;

    public const int MaxPayloadLength = 2000;

    public const int MaxIntervalMinutes = 60 * 24 * 365;

    /// <summary>The wall-clock format people and the model both read: the machine's own zone, minutes.</summary>
    public static string Local(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Validates the request and, when it passes, stores it. The error is the sentence to show whoever asked —
    /// a person on the page, or the model, which reads it and tries again.
    /// </summary>
    public static async Task<(ScheduleTask? Task, string? Error)> CreateAsync(
        IScheduler scheduler, ScheduleRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var payload = request.Payload?.Trim() ?? string.Empty;
        if (payload.Length == 0)
        {
            return (null, "请填写要发给助手的内容");
        }

        if (payload.Length > MaxPayloadLength)
        {
            return (null, $"内容太长（最多 {MaxPayloadLength} 字）");
        }

        if (!TryBuildTrigger(request, now, out var trigger, out var problem))
        {
            return (null, problem);
        }

        if ((await scheduler.ListAsync(ct).ConfigureAwait(false)).Count >= MaxTasks)
        {
            return (null, $"定时任务最多 {MaxTasks} 个，先删掉不用的");
        }

        try
        {
            var created = await scheduler.ScheduleAsync(new ScheduleTask
            {
                TaskId = "sch-" + Guid.NewGuid().ToString("n")[..8],
                Trigger = trigger!,
                Payload = payload,
            }, ct).ConfigureAwait(false);

            return (created, null);
        }
        catch (InvalidOperationException error)
        {
            // A rule with no future occurrence (a cron date that never comes, say).
            return (null, error.Message);
        }
    }

    /// <summary>
    /// Validation lives here, not in the triggers: those are pure and trust their input, and this is the one
    /// place a stranger's text becomes one.
    /// </summary>
    public static bool TryBuildTrigger(ScheduleRequest request, DateTimeOffset now, out ScheduleTrigger? trigger, out string problem)
    {
        trigger = null;
        problem = string.Empty;

        switch (request.Kind?.Trim().ToLowerInvariant())
        {
            case "cron":
                try
                {
                    trigger = new CronTrigger(request.Cron?.Trim() ?? string.Empty);
                    return true;
                }
                catch (ArgumentException error)
                {
                    // ArgumentException appends "(Parameter 'expression')" to the message; the reason before it
                    // is what the reader needs, the parameter name is ours.
                    var reason = Regex.Replace(error.Message, @"\s*\(Parameter '[^']*'\)\s*$", string.Empty);
                    problem = $"cron 表达式不对：{reason}";
                    return false;
                }

            case "interval":
                if (request.IntervalMinutes is not { } minutes || minutes < 1 || minutes > MaxIntervalMinutes)
                {
                    problem = $"间隔要在 1 到 {MaxIntervalMinutes} 分钟之间";
                    return false;
                }

                trigger = new IntervalTrigger(TimeSpan.FromMinutes(minutes));
                return true;

            case "once":
                if (request.At is not { } at || at <= now)
                {
                    problem = "执行时间要晚于现在";
                    return false;
                }

                trigger = new OnceTrigger(at);
                return true;

            default:
                problem = "类型只能是 cron、interval 或 once";
                return false;
        }
    }

    /// <summary>The rule in words. Cron stays as the expression: translating it would be a second source of truth.</summary>
    public static string Describe(ScheduleTask task) => task.Trigger switch
    {
        IntervalTrigger interval => interval.Interval.TotalMinutes switch
        {
            var m when m % 1440 == 0 => $"每 {m / 1440:0} 天",
            var m when m % 60 == 0 => $"每 {m / 60:0} 小时",
            var m => $"每 {m:0} 分钟",
        },
        OnceTrigger once => $"只一次，{Local(once.At)}",
        CronTrigger cron => $"cron {cron.Expression}",
        _ => task.Trigger.Describe(),
    };
}
