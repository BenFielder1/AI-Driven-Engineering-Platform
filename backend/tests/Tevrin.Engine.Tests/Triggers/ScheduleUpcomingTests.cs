using Tevrin.Engine.Domain.Triggers;

namespace Tevrin.Engine.Tests.Triggers;

/// <summary>FR-366: the next-3-fire-times preview of a schedule, from the FR-336 cron rules.</summary>
public class ScheduleUpcomingTests
{
    /// <summary>Europe/London (see <see cref="CronScheduleTests"/> for the Windows fallback).</summary>
    private static readonly TimeZoneInfo London =
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/London", out var iana) ? iana : TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

    private static ScheduleSpec Spec(string cron)
    {
        Assert.True(CronSchedule.TryParse(cron, out var schedule, out var error), error);
        return new ScheduleSpec(schedule, London);
    }

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Fact]
    public void FR366_preview_is_the_next_three_fire_times_strictly_after_now()
    {
        // Every Monday at 06:00 London; 2030-12-02 is a Monday, and London is on GMT in December.
        var times = ScheduleRules.NextFireTimes(Spec("0 6 * * 1"), Utc(2030, 12, 2, 6, 0));

        Assert.Equal([Utc(2030, 12, 9, 6, 0), Utc(2030, 12, 16, 6, 0), Utc(2030, 12, 23, 6, 0)], times);
    }

    [Fact]
    public void FR366_preview_follows_the_timezone_across_a_clock_change()
    {
        // 06:00 London daily around the 2031-03-30 spring-forward: GMT, then BST (05:00 UTC).
        var times = ScheduleRules.NextFireTimes(Spec("0 6 * * *"), Utc(2031, 3, 28, 12, 0));

        Assert.Equal([Utc(2031, 3, 29, 6, 0), Utc(2031, 3, 30, 5, 0), Utc(2031, 3, 31, 5, 0)], times);
    }

    [Fact]
    public void FR366_an_active_schedule_starts_at_its_stored_next_fire_even_when_overdue()
    {
        var spec = Spec("0 6 * * *");
        var stored = Utc(2030, 12, 2, 6, 0);

        var times = ScheduleRules.Upcoming(spec, paused: false, stored, now: Utc(2030, 12, 2, 7, 30));

        Assert.Equal([stored, Utc(2030, 12, 3, 6, 0), Utc(2030, 12, 4, 6, 0)], times);
    }

    [Fact]
    public void FR366_a_paused_schedule_previews_from_now_as_a_resume_would()
    {
        var times = ScheduleRules.Upcoming(Spec("0 6 * * *"), paused: true, Utc(2030, 1, 1, 6, 0), now: Utc(2030, 12, 2, 7, 30));

        Assert.Equal([Utc(2030, 12, 3, 6, 0), Utc(2030, 12, 4, 6, 0), Utc(2030, 12, 5, 6, 0)], times);
    }

    [Fact]
    public void FR366_a_count_of_zero_is_empty()
    {
        Assert.Empty(ScheduleRules.NextFireTimes(Spec("* * * * *"), Utc(2030, 12, 2, 6, 0), 0));
        Assert.Empty(ScheduleRules.Upcoming(Spec("* * * * *"), paused: false, Utc(2030, 12, 2, 6, 0), Utc(2030, 12, 2, 6, 0), 0));
    }
}
