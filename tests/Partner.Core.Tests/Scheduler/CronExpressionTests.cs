using Partner.Core.Scheduler;

namespace Partner.Core.Tests.Scheduler;

public sealed class CronExpressionTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 26, 10, 7, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("* * * * *")]
    [InlineData("*/15 * * * *")]
    [InlineData("0 9-17 * * mon-fri")]
    [InlineData("0 0 1 1 *")]
    [InlineData("30 4 1,15 * 5")]
    [InlineData("0 0 29 2 *")]
    [InlineData("5/10 * * * *")]
    [InlineData("1-30/5 * * * *")]
    [InlineData("0 0 1 JAN *")]
    [InlineData("0 0 * * SUN")]
    [InlineData("  0   0  *  *  *  ")]
    public void TryParse_AcceptsValidExpressions(string expression)
    {
        Assert.True(CronExpression.TryParse(expression, out CronExpression? parsed, out string? error));

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(expression.Trim(), parsed!.Expression);
    }

    [Theory]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * 32 * *")]
    [InlineData("* * * 0 *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("5-1 * * * *")]
    [InlineData("1,,2 * * * *")]
    [InlineData("abc * * * *")]
    public void TryParse_RejectsInvalidExpressions(string expression)
    {
        Assert.False(CronExpression.TryParse(expression, out CronExpression? parsed, out string? error));

        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("@daily")]
    [InlineData("? * * * *")]
    [InlineData("*/ * * * *")]
    [InlineData("-1 * * * *")]
    [InlineData("1/ * * * *")]
    [InlineData("1, * * * *")]
    [InlineData("1--5 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("0 0 L * *")]
    [InlineData("0 0 * * mon-")]
    [InlineData("* * * jan-dec-extra *")]
    [InlineData("0 0 1 1 * trailing")]
    public void TryParse_NeverThrowsForGarbage(string expression)
    {
        bool ok = CronExpression.TryParse(expression, out CronExpression? parsed, out string? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_ErrorMessageNamesTheOffendingFieldAndValue()
    {
        Assert.False(CronExpression.TryParse("60 * * * *", out _, out string? error));

        Assert.Contains("minute", error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("60", error!);
    }

    [Fact]
    public void Parse_ThrowsArgumentExceptionForInvalidExpression()
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(() => CronExpression.Parse("* * 31-1 * *"));

        Assert.Contains("day-of-month", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reversed", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NextAfter_EveryMinute_ReturnsTheFollowingMinute()
    {
        CronExpression cron = CronExpression.Parse("* * * * *");

        Assert.Equal(new DateTimeOffset(2026, 9, 26, 10, 8, 0, TimeSpan.Zero), cron.NextAfter(Base, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_StepExpression_ReturnsNextQuarterHour()
    {
        CronExpression cron = CronExpression.Parse("*/15 * * * *");

        Assert.Equal(new DateTimeOffset(2026, 9, 26, 10, 15, 0, TimeSpan.Zero), cron.NextAfter(Base, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_DailyExpression_RollsToTheNextDay()
    {
        CronExpression cron = CronExpression.Parse("0 9 * * *");

        Assert.Equal(new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero), cron.NextAfter(Base, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_TreatsAfterAsExclusive()
    {
        CronExpression cron = CronExpression.Parse("*/15 * * * *");
        var exact = new DateTimeOffset(2026, 9, 26, 10, 15, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 9, 26, 10, 30, 0, TimeSpan.Zero), cron.NextAfter(exact, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_LeapDayExpression_JumpsTwoYearsInsteadOfMinuteStepping()
    {
        CronExpression cron = CronExpression.Parse("0 0 29 2 *");

        Assert.Equal(
            new DateTimeOffset(2028, 2, 29, 0, 0, 0, TimeSpan.Zero),
            cron.NextAfter(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_WeekdayWindow_FromSaturdayReturnsMondayMorning()
    {
        CronExpression cron = CronExpression.Parse("0 9-17 * * mon-fri");
        var saturday = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero), cron.NextAfter(saturday, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_DayOfMonthOrDayOfWeek_ReturnsTheEarlierMatch()
    {
        CronExpression cron = CronExpression.Parse("0 0 13 * 5");

        // Friday 2026-09-04 comes before Sunday 2026-09-13, so the OR rule must pick the Friday.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero),
            cron.NextAfter(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_DayMatchingBothFields_YieldsOneOccurrenceNotTwo()
    {
        CronExpression cron = CronExpression.Parse("0 0 13 * 5"); // 2026-11-13 is a Friday.

        DateTimeOffset? fridayTheThirteenth = cron.NextAfter(new DateTimeOffset(2026, 11, 12, 23, 59, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero), fridayTheThirteenth);

        // The following occurrence is the next Friday, not the same instant emitted twice.
        Assert.Equal(
            new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero),
            cron.NextAfter(fridayTheThirteenth!.Value, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_DayOfMonthOnly_IgnoresWeekday()
    {
        CronExpression cron = CronExpression.Parse("0 0 13 * *");

        Assert.Equal(
            new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero),
            cron.NextAfter(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_DayOfWeekOnly_IgnoresDayOfMonth()
    {
        CronExpression cron = CronExpression.Parse("0 0 * * 5");

        Assert.Equal(
            new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero),
            cron.NextAfter(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_FixedOffsetZone_ConvertsWallClockToUtc()
    {
        TimeZoneInfo plus8 = TimeZoneInfo.CreateCustomTimeZone("t+8", TimeSpan.FromHours(8), "t+8", "t+8");
        CronExpression cron = CronExpression.Parse("0 9 * * *");
        var after = new DateTimeOffset(2026, 9, 26, 0, 30, 0, TimeSpan.Zero); // 08:30 local

        DateTimeOffset? result = cron.NextAfter(after, plus8);

        Assert.Equal(new DateTimeOffset(2026, 9, 26, 1, 0, 0, TimeSpan.Zero), result);
        Assert.Equal(TimeSpan.Zero, result!.Value.Offset);
    }

    [Fact]
    public void NextAfter_SkipsLocalTimeInsideDstSpringForwardGap()
    {
        // Custom zone mirroring US rules: on 2026-03-08 the clock jumps 02:00 -> 03:00, so 02:30 does not exist.
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone(
            "gap-zone",
            TimeSpan.FromHours(-5),
            "gap-zone",
            "gap-zone",
            "gap-zone-dst",
            [
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    new DateTime(2026, 1, 1),
                    new DateTime(2030, 12, 31),
                    TimeSpan.FromHours(1),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 8),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1)),
            ]);

        CronExpression cron = CronExpression.Parse("30 2 * * *");
        DateTimeOffset? result = cron.NextAfter(new DateTimeOffset(2026, 3, 7, 8, 0, 0, TimeSpan.Zero), zone);

        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 3, 9, 2, 30, 0), TimeZoneInfo.ConvertTime(result!.Value, zone).DateTime);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero), result.Value);
    }

    [Fact]
    public void NextAfter_ValueWithStep_StartsAtValueAndStepsToFieldMaximum()
    {
        CronExpression cron = CronExpression.Parse("5/10 * * * *"); // 5, 15, 25, 35, 45, 55

        Assert.Equal(new DateTimeOffset(2026, 9, 26, 10, 15, 0, TimeSpan.Zero), cron.NextAfter(Base, TimeZoneInfo.Utc));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 26, 10, 55, 0, TimeSpan.Zero),
            cron.NextAfter(new DateTimeOffset(2026, 9, 26, 10, 45, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 26, 11, 5, 0, TimeSpan.Zero),
            cron.NextAfter(new DateTimeOffset(2026, 9, 26, 10, 56, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_RangeWithStep_StepsWithinRangeOnly()
    {
        CronExpression cron = CronExpression.Parse("1-30/5 * * * *"); // 1, 6, 11, 16, 21, 26

        Assert.Equal(new DateTimeOffset(2026, 9, 26, 10, 11, 0, TimeSpan.Zero), cron.NextAfter(Base, TimeZoneInfo.Utc));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 26, 11, 1, 0, TimeSpan.Zero),
            cron.NextAfter(new DateTimeOffset(2026, 9, 26, 10, 26, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_AcceptsMonthAndWeekdayAliases()
    {
        var from = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero); // Monday

        Assert.Equal(
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CronExpression.Parse("0 0 1 jan *").NextAfter(from, TimeZoneInfo.Utc));
        Assert.Equal(
            new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero),
            CronExpression.Parse("0 0 * * fri").NextAfter(from, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NextAfter_TreatsSevenAsSunday()
    {
        CronExpression cron = CronExpression.Parse("0 0 * * 7");

        DateTimeOffset? result = cron.NextAfter(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 6, 7, 0, 0, 0, TimeSpan.Zero), result);
        Assert.Equal(DayOfWeek.Sunday, result!.Value.UtcDateTime.DayOfWeek);
    }

    [Fact]
    public void NextAfter_ImpossibleButValidExpression_ReturnsNullWithinHorizon()
    {
        CronExpression cron = CronExpression.Parse("0 0 30 2 *"); // February never has 30 days.

        Assert.Null(cron.NextAfter(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }
}
