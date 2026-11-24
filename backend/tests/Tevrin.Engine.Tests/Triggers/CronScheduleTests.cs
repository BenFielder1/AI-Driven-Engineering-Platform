using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Triggers;

namespace Tevrin.Engine.Tests.Triggers;

/// <summary>FR-336, §4.71, D-0.8.0-11: the internal 5-field cron parser and its next-occurrence rule, DST included.</summary>
public class CronScheduleTests
{
    /// <summary>Europe/London. CI and the images run on Linux (the tz database); a Windows machine with invariant
    /// globalization can't map IANA ids, so local runs use the same rules by their Windows id.</summary>
    private static readonly TimeZoneInfo London =
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/London", out var iana) ? iana : TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

    private static CronSchedule Parse(string expression)
    {
        Assert.True(CronSchedule.TryParse(expression, out var schedule, out var error), error);
        return schedule;
    }

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("* * * * *")]
    [InlineData("0 6 * * 1")]
    [InlineData("*/15 0-6,22 1,15 JAN-mar MON-FRI")]
    [InlineData("5/10 * * * 7")]
    [InlineData("0 0 1-31/2 * *")]
    [InlineData("  0   6 * * 1  ")]
    public void FR336_valid_cron_expressions_parse(string expression) =>
        Assert.True(CronSchedule.TryParse(expression, out _, out _));

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("5-1 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("1,,2 * * * *")]
    [InlineData("a * * * *")]
    [InlineData("-1 * * * *")]
    [InlineData("@daily")]
    public void FR336_invalid_cron_expressions_are_refused(string expression)
    {
        Assert.False(CronSchedule.TryParse(expression, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void FR336_spec_example_Monday_0600_London_is_0600Z_in_winter_and_0500Z_in_summer()
    {
        var cron = Parse("0 6 * * 1");

        // Monday 7 Dec 2026 (GMT): 06:00 local = 06:00Z.
        Assert.Equal(Utc(2026, 12, 7, 6, 0), cron.NextOccurrence(Utc(2026, 12, 7, 5, 59), London));

        // Monday 6 Jul 2026 (BST): 06:00 local = 05:00Z.
        Assert.Equal(Utc(2026, 7, 6, 5, 0), cron.NextOccurrence(Utc(2026, 7, 6, 4, 59), London));

        // Strictly after: from the fire itself, the next Monday.
        Assert.Equal(Utc(2026, 12, 14, 6, 0), cron.NextOccurrence(Utc(2026, 12, 7, 6, 0), London));
    }

    [Fact]
    public void FR336_a_time_skipped_by_spring_forward_fires_at_the_next_valid_minute()
    {
        // 29 Mar 2026: London clocks go 01:00 GMT → 02:00 BST, so 01:30 doesn't exist.
        var cron = Parse("30 1 * * *");
        var fire = cron.NextOccurrence(Utc(2026, 3, 29, 0, 0), London);
        Assert.Equal(Utc(2026, 3, 29, 1, 0), fire); // 02:00 BST

        // The next day is normal again: 01:30 BST = 00:30Z.
        Assert.Equal(Utc(2026, 3, 30, 0, 30), cron.NextOccurrence(fire!.Value, London));
    }

    [Fact]
    public void FR336_every_quarter_hour_across_spring_forward_fires_once_at_the_gap_end()
    {
        var cron = Parse("*/15 * * * *");
        var fires = Sequence(cron, Utc(2026, 3, 29, 0, 50), 4);
        // 00:45 GMT … then 01:00–01:45 don't exist → 02:00 BST (01:00Z) once, then 02:15 BST.
        Assert.Equal([Utc(2026, 3, 29, 1, 0), Utc(2026, 3, 29, 1, 15), Utc(2026, 3, 29, 1, 30), Utc(2026, 3, 29, 1, 45)], fires);
    }

    [Fact]
    public void FR336_a_time_that_occurs_twice_at_fall_back_fires_once_at_the_first()
    {
        // 25 Oct 2026: London clocks go 02:00 BST → 01:00 GMT, so 01:30 happens at 00:30Z and again at 01:30Z.
        var cron = Parse("30 1 * * *");
        var first = cron.NextOccurrence(Utc(2026, 10, 25, 0, 0), London);
        Assert.Equal(Utc(2026, 10, 25, 0, 30), first);

        // Not again at the second 01:30 (01:30Z): the next fire is the next day's 01:30 GMT.
        Assert.Equal(Utc(2026, 10, 26, 1, 30), cron.NextOccurrence(first!.Value, London));

        // Asked from inside the repeated hour (01:10 GMT), the second 01:30 still doesn't fire.
        Assert.Equal(Utc(2026, 10, 26, 1, 30), cron.NextOccurrence(Utc(2026, 10, 25, 1, 10), London));
    }

    [Fact]
    public void FR336_hourly_across_fall_back_fires_once_per_wall_hour()
    {
        var fires = Sequence(Parse("0 * * * *"), Utc(2026, 10, 24, 23, 30), 3);
        // 00:00 BST (23:00Z) was before; 01:00 BST = 00:00Z; 01:00 GMT (01:00Z) is skipped; 02:00 GMT = 02:00Z.
        Assert.Equal([Utc(2026, 10, 25, 0, 0), Utc(2026, 10, 25, 2, 0), Utc(2026, 10, 25, 3, 0)], fires);
    }

    [Fact]
    public void FR336_both_day_fields_restricted_match_either_vixie_rule()
    {
        // The 1st of the month OR any Friday.
        var cron = Parse("0 0 1 * 5");
        var utc = TimeZoneInfo.Utc;
        Assert.Equal(Utc(2026, 12, 4, 0, 0), cron.NextOccurrence(Utc(2026, 12, 1, 0, 0), utc));  // Fri 4 Dec
        Assert.Equal(Utc(2027, 1, 1, 0, 0), cron.NextOccurrence(Utc(2026, 12, 25, 0, 0), utc));  // Fri 1 Jan (both)
        Assert.Equal(Utc(2026, 12, 1, 0, 0), cron.NextOccurrence(Utc(2026, 11, 27, 0, 0), utc)); // Tue 1 Dec
    }

    [Fact]
    public void FR336_a_starred_day_field_means_both_must_match()
    {
        // Day of month */2 starts with *, so only odd days that are also Mondays.
        var cron = Parse("0 0 */2 * 1");
        Assert.Equal(Utc(2026, 12, 7, 0, 0), cron.NextOccurrence(Utc(2026, 11, 24, 0, 0), TimeZoneInfo.Utc));
    }

    [Fact]
    public void FR336_day_of_week_7_is_Sunday()
    {
        var cron = Parse("0 12 * * 7");
        Assert.Equal(Utc(2026, 11, 29, 12, 0), cron.NextOccurrence(Utc(2026, 11, 24, 0, 0), TimeZoneInfo.Utc));
    }

    [Fact]
    public void FR336_a_cron_that_never_fires_has_no_next_occurrence_and_is_SCHEDULE_INVALID()
    {
        Assert.Null(Parse("0 0 30 2 *").NextOccurrence(Utc(2026, 1, 1, 0, 0), TimeZoneInfo.Utc));
        var parsed = ScheduleRules.Parse("0 0 30 2 *", "UTC", Utc(2026, 1, 1, 0, 0));
        Assert.Equal(EngineErrorCodes.ScheduleInvalid, parsed.Error!.Code);
        Assert.Equal("cron", parsed.Error.Field);
    }

    [Theory]
    [InlineData("Not/AZone")]
    [InlineData("")]
    [InlineData("GMT Standard Time")]
    public void FR336_an_unknown_or_non_IANA_timezone_is_SCHEDULE_INVALID(string timezone)
    {
        var parsed = ScheduleRules.Parse("0 6 * * 1", timezone, Utc(2026, 1, 1, 0, 0));
        Assert.Equal(EngineErrorCodes.ScheduleInvalid, parsed.Error!.Code);
        Assert.Equal("timezone", parsed.Error.Field);
    }

    [Fact]
    public void FR336_a_bad_cron_is_SCHEDULE_INVALID_on_the_cron_field()
    {
        var parsed = ScheduleRules.Parse("61 * * * *", "UTC", Utc(2026, 1, 1, 0, 0));
        Assert.Equal(EngineErrorCodes.ScheduleInvalid, parsed.Error!.Code);
        Assert.Equal("cron", parsed.Error.Field);
    }

    [Fact]
    public void FR336_catch_up_after_downtime_fires_once_then_resumes()
    {
        var spec = ScheduleRules.Parse("0 6 * * 1", "UTC", Utc(2026, 11, 1, 0, 0)).Value;

        // Down from before Mon 7 Dec until Wed 23 Dec: three Mondays missed, one fire now, next is Mon 28 Dec.
        var now = Utc(2026, 12, 23, 10, 0);
        Assert.True(ScheduleRules.IsDue(paused: false, Utc(2026, 12, 7, 6, 0), now));
        Assert.Equal(Utc(2026, 12, 28, 6, 0), ScheduleRules.NextAfterFire(spec, now));
    }

    [Fact]
    public void FR336_a_paused_or_future_schedule_is_not_due()
    {
        var now = Utc(2026, 12, 7, 6, 0);
        Assert.False(ScheduleRules.IsDue(paused: true, now, now));
        Assert.False(ScheduleRules.IsDue(paused: false, now.AddMinutes(1), now));
        Assert.True(ScheduleRules.IsDue(paused: false, now, now));
    }

    /// <summary>America/New_York (Windows id fallback as for <see cref="London"/>).</summary>
    private static readonly TimeZoneInfo NewYork =
        TimeZoneInfo.TryFindSystemTimeZoneById("America/New_York", out var iana) ? iana : TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    [Fact]
    public void FR336_next_occurrences_are_UTC_with_offset_zero_in_London_summer()
    {
        // §4.71: Mon 6 Jul 2026 06:00 BST is 05:00Z, returned as UTC (PostgreSQL timestamptz writes need offset zero).
        var fire = Parse("0 6 * * 1").NextOccurrence(Utc(2026, 7, 6, 4, 59), London)!.Value;
        Assert.Equal(Utc(2026, 7, 6, 5, 0), fire);
        Assert.Equal(TimeSpan.Zero, fire.Offset);
    }

    [Fact]
    public void FR336_next_occurrences_are_UTC_in_New_York_in_summer_and_winter()
    {
        var cron = Parse("30 9 * * *");
        var summer = cron.NextOccurrence(Utc(2026, 7, 1, 0, 0), NewYork)!.Value;
        var winter = cron.NextOccurrence(Utc(2026, 12, 1, 0, 0), NewYork)!.Value;
        Assert.Equal((Utc(2026, 7, 1, 13, 30), TimeSpan.Zero), (summer, summer.Offset)); // EDT, UTC-4
        Assert.Equal((Utc(2026, 12, 1, 14, 30), TimeSpan.Zero), (winter, winter.Offset)); // EST, UTC-5
    }

    [Fact]
    public void FR336_every_returned_occurrence_has_offset_zero_across_DST()
    {
        foreach (var (expression, zone, after) in new[]
        {
            ("*/15 * * * *", London, Utc(2026, 3, 29, 0, 50)),
            ("30 1 * * *", London, Utc(2026, 10, 25, 0, 0)),
            ("0 * * * *", London, Utc(2026, 10, 24, 23, 30)),
            ("0 2 * * *", NewYork, Utc(2026, 3, 7, 0, 0)),
            ("30 1 * * *", NewYork, Utc(2026, 10, 31, 0, 0)),
        })
        {
            var cron = Parse(expression);
            var at = after;
            for (var i = 0; i < 10; i++)
            {
                at = cron.NextOccurrence(at, zone)!.Value;
                Assert.Equal(TimeSpan.Zero, at.Offset);
            }
        }

        Assert.Equal(TimeSpan.Zero, CronSchedule.ToInstant(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Unspecified), London).Offset);
    }

    [Fact]
    public void FR336_a_cron_over_100_characters_is_SCHEDULE_INVALID()
    {
        var expression = "0 6 * * " + string.Join(',', Enumerable.Repeat("1", 50));
        Assert.True(expression.Length > CronSchedule.MaxLength);
        Assert.False(CronSchedule.TryParse(expression, out _, out _));
        Assert.Equal(EngineErrorCodes.ScheduleInvalid, ScheduleRules.Parse(expression, "UTC", Utc(2026, 1, 1, 0, 0)).Error!.Code);
    }

    private static List<DateTimeOffset> Sequence(CronSchedule cron, DateTimeOffset after, int count)
    {
        var fires = new List<DateTimeOffset>();
        var at = after;
        for (var i = 0; i < count; i++)
        {
            at = cron.NextOccurrence(at, London)!.Value;
            fires.Add(at);
        }

        return fires;
    }
}
