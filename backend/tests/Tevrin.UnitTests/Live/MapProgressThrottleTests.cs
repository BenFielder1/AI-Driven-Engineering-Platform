using Tevrin.Api.Features.Live;
using Tevrin.Engine.Api;

namespace Tevrin.UnitTests.Live;

/// <summary>FR-363: <c>mapProgress</c> is sent at most once a second per map, with a trailing send for changes in between.</summary>
public sealed class MapProgressThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void FR363_the_first_change_of_a_map_is_sent_at_once() =>
        Assert.Equal((MapProgressSend.Now, TimeSpan.Zero), MapProgressThrottle.Decide(null, trailingScheduled: false, T0, Second));

    [Fact]
    public void FR363_a_change_a_second_or_more_after_the_last_send_is_sent_at_once()
    {
        Assert.Equal(MapProgressSend.Now, MapProgressThrottle.Decide(T0, trailingScheduled: false, T0 + Second, Second).Send);
        Assert.Equal(MapProgressSend.Now, MapProgressThrottle.Decide(T0, trailingScheduled: false, T0 + TimeSpan.FromSeconds(5), Second).Send);
    }

    [Fact]
    public void FR363_a_change_within_the_second_schedules_one_trailing_send_at_its_end() =>
        Assert.Equal(
            (MapProgressSend.Trailing, TimeSpan.FromMilliseconds(700)),
            MapProgressThrottle.Decide(T0, trailingScheduled: false, T0 + TimeSpan.FromMilliseconds(300), Second));

    [Fact]
    public void FR363_changes_while_a_trailing_send_is_scheduled_are_folded_into_it()
    {
        Assert.Equal(MapProgressSend.Skip, MapProgressThrottle.Decide(T0, trailingScheduled: true, T0 + TimeSpan.FromMilliseconds(500), Second).Send);
        Assert.Equal(MapProgressSend.Skip, MapProgressThrottle.Decide(T0, trailingScheduled: true, T0 + TimeSpan.FromSeconds(3), Second).Send);
    }

    [Fact]
    public void FR363_a_thousand_item_ends_in_two_seconds_send_at_most_three_messages()
    {
        DateTimeOffset? last = null;
        DateTimeOffset? trailingAt = null;
        var sent = 0;
        for (var i = 0; i < 1000; i++)
        {
            var now = T0 + TimeSpan.FromMilliseconds(i * 2);
            if (trailingAt is { } due && now >= due)
            {
                (last, trailingAt) = (due, null);
                sent++;
            }

            var (send, delay) = MapProgressThrottle.Decide(last, trailingAt is not null, now, Second);
            if (send == MapProgressSend.Now)
            {
                last = now;
                sent++;
            }
            else if (send == MapProgressSend.Trailing)
            {
                trailingAt = now + delay;
            }
        }

        if (trailingAt is not null)
        {
            sent++;
        }

        Assert.InRange(sent, 2, 3);
    }

    [Theory]
    [InlineData(10, 7, 3, true)]
    [InlineData(10, 7, 2, false)]
    [InlineData(0, 0, 0, false)]
    public void FR363_a_map_is_settled_when_every_item_has_ended(int total, int completed, int failed, bool settled) =>
        Assert.Equal(settled, MapProgressThrottle.Settled(new PipelineMapProgressDto(Guid.CreateVersion7(), total, completed, failed)));
}
