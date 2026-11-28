using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Tevrin.Engine.Domain.Triggers;

/// <summary>
/// FR-336, §4.71, D-0.8.0-11: a 5-field cron expression (minute, hour, day of month, month, day of week), parsed by this
/// small internal parser (no package). Each field takes <c>*</c>, a value, a range <c>a-b</c>, a step <c>*/n</c>,
/// <c>a-b/n</c> or <c>a/n</c> (from a to the field's end), and comma lists of these. Months take <c>JAN</c>–<c>DEC</c> and
/// days of week <c>SUN</c>–<c>SAT</c>; day of week 0 and 7 are both Sunday. When both day fields are restricted (neither
/// starts with <c>*</c>) a day matches if either matches (the Vixie cron rule); otherwise both must.
/// </summary>
public sealed class CronSchedule
{
    /// <summary>How far ahead <see cref="NextOccurrence"/> looks: a schedule with no time in this window never fires
    /// (e.g. <c>0 0 30 2 *</c>) and is invalid.</summary>
    public const int SearchYears = 8;

    /// <summary>The longest expression accepted (the <c>pipeline_schedules.cron</c> column's length).</summary>
    public const int MaxLength = 100;

    private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _months;
    private readonly bool[] _daysOfWeek;
    private readonly bool _dayOfMonthStar;
    private readonly bool _dayOfWeekStar;
    private readonly int[] _minuteList;
    private readonly int[] _hourList;

    private CronSchedule(string expression, bool[][] fields, bool dayOfMonthStar, bool dayOfWeekStar)
    {
        Expression = expression;
        (_minutes, _hours, _daysOfMonth, _months, _daysOfWeek) = (fields[0], fields[1], fields[2], fields[3], fields[4]);
        _dayOfMonthStar = dayOfMonthStar;
        _dayOfWeekStar = dayOfWeekStar;
        _minuteList = [.. Enumerable.Range(0, 60).Where(m => _minutes[m])];
        _hourList = [.. Enumerable.Range(0, 24).Where(h => _hours[h])];
    }

    /// <summary>The expression as given (trimmed).</summary>
    public string Expression { get; }

    /// <summary>Parses <paramref name="expression"/>; <c>false</c> with a reason in <paramref name="error"/> when it isn't a
    /// valid 5-field cron.</summary>
    public static bool TryParse(string? expression, [NotNullWhen(true)] out CronSchedule? schedule, [NotNullWhen(false)] out string? error)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "The cron expression is empty.";
            return false;
        }

        if (expression.Length > MaxLength)
        {
            error = $"A cron expression has at most {MaxLength} characters.";
            return false;
        }

        var parts = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            error = $"A cron expression has 5 fields (minute hour day-of-month month day-of-week); this has {parts.Length}.";
            return false;
        }

        (string Name, int Min, int Max, string[]? Names, int NameBase)[] specs =
        [
            ("minute", 0, 59, null, 0),
            ("hour", 0, 23, null, 0),
            ("day of month", 1, 31, null, 0),
            ("month", 1, 12, MonthNames, 1),
            ("day of week", 0, 7, DayNames, 0),
        ];
        var fields = new bool[5][];
        for (var i = 0; i < 5; i++)
        {
            var spec = specs[i];
            if (!TryParseField(parts[i], spec.Min, spec.Max, spec.Names, spec.NameBase, out var set))
            {
                error = $"The {spec.Name} field '{parts[i]}' is not valid (values {spec.Min}–{spec.Max}).";
                return false;
            }

            fields[i] = set;
        }

        // Day of week 7 is Sunday.
        if (fields[4][7])
        {
            fields[4][0] = true;
        }

        schedule = new CronSchedule(string.Join(' ', parts), fields, parts[2].StartsWith('*'), parts[4].StartsWith('*'));
        error = null;
        return true;
    }

    /// <summary>Whether the cron's day fields match <paramref name="date"/> (with the Vixie OR rule).</summary>
    public bool MatchesDay(DateOnly date)
    {
        if (!_months[date.Month])
        {
            return false;
        }

        var dom = _daysOfMonth[date.Day];
        var dow = _daysOfWeek[(int)date.DayOfWeek];
        return _dayOfMonthStar || _dayOfWeekStar ? dom && dow : dom || dow;
    }

    /// <summary>
    /// §4.71: the first fire strictly after <paramref name="after"/>, evaluated in <paramref name="zone"/>'s wall clock. A
    /// wall time that doesn't exist (spring forward) fires at the next valid minute; a wall time that occurs twice (fall back)
    /// fires once, at the first (earlier) instant. The result is UTC (offset zero). <c>null</c> when nothing matches within
    /// <see cref="SearchYears"/>.
    /// </summary>
    public DateTimeOffset? NextOccurrence(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var localAfter = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var startWall = new DateTime(localAfter.Year, localAfter.Month, localAfter.Day, localAfter.Hour, localAfter.Minute, 0, DateTimeKind.Unspecified)
            .AddMinutes(1);
        var day = DateOnly.FromDateTime(startWall);
        var lastDay = day.AddYears(SearchYears);
        for (; day <= lastDay; day = day.AddDays(1))
        {
            if (!MatchesDay(day))
            {
                continue;
            }

            foreach (var hour in _hourList)
            {
                foreach (var minute in _minuteList)
                {
                    var wall = day.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Unspecified);
                    if (wall < startWall)
                    {
                        continue;
                    }

                    var instant = ToInstant(wall, zone);
                    if (instant > after)
                    {
                        return instant;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The instant of wall time <paramref name="wall"/> in <paramref name="zone"/> (§4.71's DST rules), in UTC
    /// (offset zero: PostgreSQL <c>timestamptz</c> writes only take UTC values).</summary>
    public static DateTimeOffset ToInstant(DateTime wall, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(wall))
        {
            // Occurs twice: the first occurrence has the larger offset (the earlier instant).
            return new DateTimeOffset(wall, zone.GetAmbiguousTimeOffsets(wall).Max()).ToUniversalTime();
        }

        while (zone.IsInvalidTime(wall))
        {
            // Skipped by a spring-forward: the next valid minute.
            wall = wall.AddMinutes(1);
        }

        return new DateTimeOffset(wall, zone.GetUtcOffset(wall)).ToUniversalTime();
    }

    private static bool TryParseField(string text, int min, int max, string[]? names, int nameBase, out bool[] set)
    {
        set = new bool[max + 1];
        foreach (var item in text.Split(','))
        {
            if (item.Length == 0)
            {
                return false;
            }

            var slash = item.IndexOf('/', StringComparison.Ordinal);
            var rangeText = slash < 0 ? item : item[..slash];
            var step = 1;
            if (slash >= 0 && (!TryNumber(item[(slash + 1)..], out step) || step < 1))
            {
                return false;
            }

            int from, to;
            if (rangeText == "*")
            {
                (from, to) = (min, max);
            }
            else
            {
                var dash = rangeText.IndexOf('-', StringComparison.Ordinal);
                if (dash < 0)
                {
                    if (!TryValue(rangeText, names, nameBase, out from))
                    {
                        return false;
                    }

                    // a/n runs from a to the field's end; a single value is just a.
                    to = slash >= 0 ? max : from;
                }
                else if (!TryValue(rangeText[..dash], names, nameBase, out from) || !TryValue(rangeText[(dash + 1)..], names, nameBase, out to))
                {
                    return false;
                }
            }

            if (from < min || to > max || from > to)
            {
                return false;
            }

            for (var v = from; v <= to; v += step)
            {
                set[v] = true;
            }
        }

        return true;
    }

    private static bool TryValue(string text, string[]? names, int nameBase, out int value)
    {
        if (TryNumber(text, out value))
        {
            return true;
        }

        if (names is not null)
        {
            var index = Array.FindIndex(names, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                value = index + nameBase;
                return true;
            }
        }

        return false;
    }

    private static bool TryNumber(string text, out int value)
    {
        value = 0;
        return text.Length is > 0 and <= 2 && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
