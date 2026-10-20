using Partner.Core.Scheduler;

namespace Partner.Api.Endpoints;

/// <summary>
/// The timed-tasks page's API. A task is a message to send and a rule for when: when it comes due the scheduler
/// enqueues the message as <c>InputSource.Schedule</c>, so it reaches the assistant through the same queue, and
/// shows in the session, as anything typed would. The assistant's own tools create tasks through the same
/// <see cref="ScheduleRules"/>.
/// <para>
/// There is no "edit": a task's identity is its rule plus its message, so changing either is a new task. That also
/// keeps the dedup record honest — an occurrence is keyed by the task id and its time, and rewriting a task under
/// the same id would let an old receipt swallow a new occurrence.
/// </para>
/// </summary>
public static class ScheduleEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/schedules", async (ApiContext ctx, CancellationToken ct) =>
        {
            var tasks = await ctx.Scheduler.ListAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { tasks = tasks.Select(ScheduleView.From).ToList() });
        });

        app.MapPost("/api/schedules", async (ScheduleRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            var (created, error) = await ScheduleRules.CreateAsync(ctx.Scheduler, request, ctx.Clock.GetUtcNow(), ct)
                .ConfigureAwait(false);

            return created is null
                ? Results.BadRequest(new { error })
                : Results.Ok(ScheduleView.From(created));
        });

        app.MapPost("/api/schedules/{id}/pause", (string id, ApiContext ctx, CancellationToken ct) =>
            SetStatus(id, ScheduleTaskStatus.Inactive, ctx, ct));

        app.MapPost("/api/schedules/{id}/resume", (string id, ApiContext ctx, CancellationToken ct) =>
            SetStatus(id, ScheduleTaskStatus.Active, ctx, ct));

        app.MapDelete("/api/schedules/{id}", async (string id, ApiContext ctx, CancellationToken ct) =>
            await ctx.Scheduler.RemoveAsync(id, ct).ConfigureAwait(false)
                ? Results.Ok(new { removed = true })
                : Results.NotFound(new { error = $"没有定时任务 {id}" }));
    }

    private static async Task<IResult> SetStatus(string id, ScheduleTaskStatus status, ApiContext ctx, CancellationToken ct)
    {
        try
        {
            return Results.Ok(ScheduleView.From(await ctx.Scheduler.SetStatusAsync(id, status, ct).ConfigureAwait(false)));
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound(new { error = $"没有定时任务 {id}" });
        }
    }
}

/// <summary>What a client sends to create a task. Which fields matter depends on <see cref="Kind"/>.</summary>
public sealed record ScheduleRequest(
    string? Kind,
    string? Cron,
    int? IntervalMinutes,
    DateTimeOffset? At,
    string? Payload,
    string? CatchUp = null);

/// <summary>
/// A task as the page reads it. The trigger is split into the field that made it, so the form can be refilled
/// without parsing the human-readable description.
/// </summary>
public sealed record ScheduleView(
    string Id,
    string Kind,
    string Describe,
    string? Cron,
    int? IntervalMinutes,
    DateTimeOffset? At,
    string Payload,
    string Status,
    DateTimeOffset? NextAt,
    DateTimeOffset? LastAt,
    int Attempt,
    string? LastError,
    string CatchUp = "once",
    int Missed = 0,
    DateTimeOffset? LastMissedAt = null,
    DateTimeOffset? LastRunAt = null,
    bool? LastRunOk = null,
    string? LastRunError = null)
{
    public static ScheduleView From(ScheduleTask task)
    {
        var (kind, cron, minutes, at) = task.Trigger switch
        {
            CronTrigger c => ("cron", c.Expression, (int?)null, (DateTimeOffset?)null),
            IntervalTrigger i => ("interval", null, (int?)Math.Max(1, (int)i.Interval.TotalMinutes), null),
            OnceTrigger o => ("once", null, null, (DateTimeOffset?)o.At),
            _ => ("unknown", null, null, null),
        };

        return new ScheduleView(
            task.TaskId,
            kind,
            task.Trigger.Describe(),
            cron,
            minutes,
            at,
            task.Payload,
            task.Status.ToString().ToLowerInvariant(),

            // An exhausted or paused task has no next time worth showing: its stored one is the last occurrence.
            task.Status == ScheduleTaskStatus.Inactive ? null : task.NextScheduledAt,
            task.LastDeliveryAt,
            task.Attempt,
            task.LastError,
            task.CatchUp.ToString().ToLowerInvariant(),
            task.MissedCount,
            task.LastMissedAt,
            task.LastRunAt,
            task.LastRunOk,
            task.LastRunError);
    }
}
