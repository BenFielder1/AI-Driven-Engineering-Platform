using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>FR-331 (the one-run rule for subject-less runs, applied in code) and FR-337 (webhook replay window).</summary>
public class RunStartAndDeliveryRuleTests
{
    private static readonly ConcurrencyDefinition Many = ConcurrencyDefinition.None with { Runs = RunConcurrency.Many };

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void FR331_runs_one_allows_a_start_only_without_an_open_run(bool hasOpenRun, bool mayStart) =>
        Assert.Equal(mayStart, PipelineRunRules.MayStart(ConcurrencyDefinition.None, hasOpenRun));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FR331_runs_many_always_allows_a_start(bool hasOpenRun) =>
        Assert.True(PipelineRunRules.MayStart(Many, hasOpenRun));

    private static readonly DateTimeOffset Now = new(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FR337_a_delivery_never_received_is_not_a_replay() =>
        Assert.False(WebhookDeliveryRules.IsReplay(null, Now));

    [Theory]
    [InlineData(0, true)]
    [InlineData(60, true)]
    [InlineData(24 * 60 - 1, true)]
    [InlineData(24 * 60, false)]
    [InlineData(25 * 60, false)]
    public void FR337_the_same_delivery_within_24_hours_is_a_replay(int minutesAgo, bool replay) =>
        Assert.Equal(replay, WebhookDeliveryRules.IsReplay(Now.AddMinutes(-minutesAgo), Now));

    [Fact]
    public void FR337_deliveries_are_kept_30_days_and_replays_checked_over_24_hours() =>
        Assert.Equal((TimeSpan.FromDays(30), TimeSpan.FromHours(24)), (WebhookDeliveryRules.Retention, WebhookDeliveryRules.ReplayWindow));
}
