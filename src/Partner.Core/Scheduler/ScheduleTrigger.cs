namespace Partner.Core.Scheduler;

/// <summary>
/// When a <see cref="ScheduleTask"/> is due. Implementations are pure: they never read the clock
/// themselves (design doc §2.4 — the scheduler recomputes from persisted state).
/// </summary>
public abstract record ScheduleTrigger
{
    /// <summary>First occurrence strictly after <paramref name="after"/>, or null when the trigger is exhausted.</summary>
    public abstract DateTimeOffset? NextAfter(DateTimeOffset after, TimeZoneInfo tz);

    /// <summary>Short human-readable form, used in logs and manifest evidence.</summary>
    public abstract string Describe();
}

/// <summary>
/// Fires every <paramref name="Interval"/> measured from the previous occurrence. Because the previous
/// occurrence is persisted, the grid cannot drift across restarts.
/// </summary>
public sealed record IntervalTrigger(TimeSpan Interval) : ScheduleTrigger
{
    public override DateTimeOffset? NextAfter(DateTimeOffset after, TimeZoneInfo tz)
    {
        if (Interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Interval must be positive.");
        }

        return after + Interval;
    }

    public override string Describe() => $"every {Interval}";
}

/// <summary>
/// Fires once at <paramref name="At"/>, then the task goes inactive (the scheduler marks a task whose trigger has
/// no further occurrence as exhausted).
/// </summary>
public sealed record OnceTrigger(DateTimeOffset At) : ScheduleTrigger
{
    public override DateTimeOffset? NextAfter(DateTimeOffset after, TimeZoneInfo tz) => At > after ? At : null;

    public override string Describe() => $"once at {At:O}";
}

/// <summary>Standard 5-field cron (<c>m h dom mon dow</c>), evaluated in the scheduler's time zone.</summary>
public sealed record CronTrigger : ScheduleTrigger
{
    private readonly CronExpression _expression;

    public CronTrigger(string expression)
    {
        _expression = CronExpression.Parse(expression);
    }

    public string Expression => _expression.Expression;

    public override DateTimeOffset? NextAfter(DateTimeOffset after, TimeZoneInfo tz) =>
        _expression.NextAfter(after, tz);

    public override string Describe() => $"cron {_expression.Expression}";
}
