using System.Globalization;

namespace Partner.Core.Scheduler;

/// <summary>
/// A parsed five-field cron expression (<c>minute hour day-of-month month day-of-week</c>)
/// that can compute its next occurrence in a given time zone.
/// </summary>
public sealed class CronExpression
{
    /// <summary>
    /// How far ahead <see cref="NextAfter"/> searches. Four years is the minimum horizon that
    /// resolves every valid expression (a leap-day expression can be up to four years away).
    /// </summary>
    private const int SearchHorizonYears = 4;

    private static readonly string[] WeekdayAliases = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    private static readonly string[] MonthAliases = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _daysOfWeek = new bool[7];
    private bool _dayOfMonthRestricted;
    private bool _dayOfWeekRestricted;

    private CronExpression(string expression) => Expression = expression;

    /// <summary>The original expression text, trimmed.</summary>
    public string Expression { get; }

    /// <summary>Parses "m h dom mon dow". Returns false with a human-readable reason instead of throwing.</summary>
    public static bool TryParse(string expression, out CronExpression? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "Cron expression is empty; expected five fields: minute hour day-of-month month day-of-week.";
            return false;
        }

        string[] fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = $"Cron expression '{expression.Trim()}' has {fields.Length} field(s); exactly five are required: minute hour day-of-month month day-of-week.";
            return false;
        }

        var candidate = new CronExpression(expression.Trim());
        if (!TryParseField(fields[0], "minute", 0, 59, null, 0, candidate._minutes, false, out error) ||
            !TryParseField(fields[1], "hour", 0, 23, null, 0, candidate._hours, false, out error) ||
            !TryParseField(fields[2], "day-of-month", 1, 31, null, 0, candidate._daysOfMonth, false, out error) ||
            !TryParseField(fields[3], "month", 1, 12, MonthAliases, 1, candidate._months, false, out error) ||
            !TryParseField(fields[4], "day-of-week", 0, 7, WeekdayAliases, 0, candidate._daysOfWeek, true, out error))
        {
            return false;
        }

        // The dom/dow rule keys off a literal '*', so '*/1' counts as restricted.
        candidate._dayOfMonthRestricted = fields[2] != "*";
        candidate._dayOfWeekRestricted = fields[4] != "*";
        parsed = candidate;
        return true;
    }

    /// <summary>Throws ArgumentException with a human-readable message when the expression is invalid.</summary>
    public static CronExpression Parse(string expression)
    {
        if (!TryParse(expression, out CronExpression? parsed, out string? error))
        {
            throw new ArgumentException(error, nameof(expression));
        }

        return parsed!;
    }

    /// <summary>
    /// First matching instant strictly after <paramref name="after"/>, evaluated in <paramref name="tz"/>,
    /// returned as a UTC DateTimeOffset. Null when no match exists within the search horizon.
    /// </summary>
    public DateTimeOffset? NextAfter(DateTimeOffset after, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(tz);

        // Work on the wall clock of tz and jump by month/day/hour/minute; a minute-by-minute walk
        // would need ~1.5 million steps to reach the next Feb 29.
        DateTime wall = TimeZoneInfo.ConvertTime(after, tz).DateTime;
        DateTime cursor = new(wall.Year, wall.Month, wall.Day, wall.Hour, wall.Minute, 0);
        cursor = cursor.AddMinutes(1); // `after` is exclusive, so a match at `after` itself is skipped.
        DateTime horizon = cursor.AddYears(SearchHorizonYears);

        while (cursor <= horizon)
        {
            if (!_months[cursor.Month])
            {
                cursor = new DateTime(cursor.Year, cursor.Month, 1).AddMonths(1);
                continue;
            }

            if (!DayMatches(cursor))
            {
                cursor = cursor.Date.AddDays(1);
                continue;
            }

            if (!_hours[cursor.Hour])
            {
                int nextHour = NextSetAtOrAfter(_hours, cursor.Hour + 1, 23);
                cursor = nextHour < 0 ? cursor.Date.AddDays(1) : cursor.Date.AddHours(nextHour);
                continue;
            }

            if (!_minutes[cursor.Minute])
            {
                int nextMinute = NextSetAtOrAfter(_minutes, cursor.Minute + 1, 59);
                cursor = nextMinute < 0
                    ? cursor.Date.AddHours(cursor.Hour + 1)
                    : cursor.Date.AddHours(cursor.Hour).AddMinutes(nextMinute);
                continue;
            }

            // A local time inside a DST spring-forward gap does not exist: skip it rather than throw.
            if (tz.IsInvalidTime(cursor))
            {
                cursor = cursor.AddMinutes(1);
                continue;
            }

            DateTimeOffset instant = new DateTimeOffset(cursor, tz.GetUtcOffset(cursor)).ToUniversalTime();
            if (instant > after)
            {
                return instant;
            }

            // Ambiguous/offset-shifted instants can land at or before `after`; keep walking.
            cursor = cursor.AddMinutes(1);
        }

        return null;
    }

    /// <summary>
    /// Standard cron day rule: when both day fields are restricted either may match, when only one is
    /// restricted it must match, and when neither is restricted every day matches.
    /// </summary>
    private bool DayMatches(DateTime wall)
    {
        int weekday = (int)wall.DayOfWeek; // Sunday = 0.
        if (_dayOfMonthRestricted && _dayOfWeekRestricted)
        {
            return _daysOfMonth[wall.Day] || _daysOfWeek[weekday];
        }

        if (_dayOfMonthRestricted)
        {
            return _daysOfMonth[wall.Day];
        }

        if (_dayOfWeekRestricted)
        {
            return _daysOfWeek[weekday];
        }

        return true;
    }

    private static int NextSetAtOrAfter(bool[] set, int from, int max)
    {
        for (int value = from; value <= max; value++)
        {
            if (set[value])
            {
                return value;
            }
        }

        return -1;
    }

    private static bool TryParseField(
        string field,
        string fieldName,
        int min,
        int max,
        string[]? aliases,
        int aliasBase,
        bool[] target,
        bool mapSevenToSunday,
        out string? error)
    {
        foreach (string element in field.Split(','))
        {
            if (element.Length == 0)
            {
                error = $"Invalid {fieldName} field '{field}': empty element in list.";
                return false;
            }

            string body = element;
            int step = 1;
            int slash = element.IndexOf('/');
            if (slash >= 0)
            {
                body = element[..slash];
                string stepText = element[(slash + 1)..];
                if (!int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                {
                    error = $"Invalid {fieldName} field '{field}': '{stepText}' is not a valid step (expected a positive integer).";
                    return false;
                }
            }

            int start;
            int end;
            if (body == "*")
            {
                start = min;
                end = max;
            }
            else if (body.Contains('-'))
            {
                string[] bounds = body.Split('-');
                if (bounds.Length != 2)
                {
                    error = $"Invalid {fieldName} field '{field}': range '{body}' must contain exactly one '-'.";
                    return false;
                }

                if (!TryResolveToken(bounds[0], min, max, aliases, aliasBase, fieldName, field, out start, out error) ||
                    !TryResolveToken(bounds[1], min, max, aliases, aliasBase, fieldName, field, out end, out error))
                {
                    return false;
                }

                if (start > end)
                {
                    error = $"Invalid {fieldName} field '{field}': range '{body}' is reversed.";
                    return false;
                }
            }
            else
            {
                if (!TryResolveToken(body, min, max, aliases, aliasBase, fieldName, field, out start, out error))
                {
                    return false;
                }

                // "5/10" means "from 5, step 10, up to the field maximum".
                end = slash >= 0 ? max : start;
            }

            for (int value = start; value <= end; value += step)
            {
                target[mapSevenToSunday && value == 7 ? 0 : value] = true;
            }
        }

        error = null;
        return true;
    }

    private static bool TryResolveToken(
        string token,
        int min,
        int max,
        string[]? aliases,
        int aliasBase,
        string fieldName,
        string field,
        out int value,
        out string? error)
    {
        value = 0;

        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int numeric))
        {
            if (numeric < min || numeric > max)
            {
                error = $"Invalid {fieldName} field '{field}': {numeric} is out of range {min}-{max}.";
                return false;
            }

            value = numeric;
            error = null;
            return true;
        }

        if (aliases is not null)
        {
            for (int i = 0; i < aliases.Length; i++)
            {
                if (string.Equals(aliases[i], token, StringComparison.OrdinalIgnoreCase))
                {
                    value = aliasBase + i;
                    error = null;
                    return true;
                }
            }
        }

        error = aliases is null
            ? $"Invalid {fieldName} field '{field}': '{token}' is not a number in {min}-{max}."
            : $"Invalid {fieldName} field '{field}': '{token}' is not a number in {min}-{max} or one of {string.Join(", ", aliases)}.";
        return false;
    }
}
