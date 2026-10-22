using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Releases;

namespace Aide.UnitTests.Releases;

/// <summary>§4.14, FR-65: the merge poll schedule, its 14-day ceiling and the awaiting → released/failed decisions.</summary>
public class MergePollRulesTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 2)]
    [InlineData(30, 2)]
    [InlineData(59, 2)]
    [InlineData(60, 10)]
    [InlineData(600, 10)]
    public void S4_14_Polls_every_2_minutes_for_the_first_hour_then_every_10(int minutesWaited, int expectedInterval)
    {
        var now = Since.AddMinutes(minutesWaited);

        Assert.Equal(now.AddMinutes(expectedInterval), MergePollRules.NextPollAt(Since, now));
    }

    [Fact]
    public void S4_14_The_last_poll_runs_at_the_14_day_ceiling_never_after_it()
    {
        var deadline = Since + TimeSpan.FromDays(14);

        Assert.Equal(deadline, MergePollRules.NextPollAt(Since, deadline.AddMinutes(-3)));
        Assert.Equal(deadline.AddMinutes(1), MergePollRules.NextPollAt(Since, deadline.AddMinutes(1)));
    }

    [Theory]
    [InlineData(MergePollObservation.Open)]
    [InlineData(MergePollObservation.NotModified)]
    public void S4_14_Open_or_unchanged_waits_for_the_next_poll(MergePollObservation observation)
    {
        var now = Since.AddMinutes(5);
        var decision = MergePollRules.Decide(Since, now, observation);

        Assert.Equal((MergePollDecisionKind.Wait, now.AddMinutes(2)), (decision.Kind, decision.NextPollAt));
    }

    [Theory]
    [InlineData(MergePollObservation.Open)]
    [InlineData(MergePollObservation.NotModified)]
    public void S4_14_FR65_After_14_days_unmerged_fails_with_MERGE_TIMEOUT(MergePollObservation observation)
    {
        var decision = MergePollRules.Decide(Since, Since + TimeSpan.FromDays(14), observation);

        Assert.Equal((MergePollDecisionKind.Fail, ErrorCodes.MergeTimeout), (decision.Kind, decision.ErrorCode));
        Assert.False(MergePollRules.IsTimedOut(Since, Since + TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void S4_14_FR65_Merged_completes_even_at_the_ceiling()
    {
        Assert.Equal(MergePollDecisionKind.Complete, MergePollRules.Decide(Since, Since.AddMinutes(2), MergePollObservation.Merged).Kind);
        Assert.Equal(MergePollDecisionKind.Complete, MergePollRules.Decide(Since, Since + TimeSpan.FromDays(20), MergePollObservation.Merged).Kind);
    }

    [Fact]
    public void S4_14_FR65_Closed_without_merging_fails_with_PR_CLOSED()
    {
        var decision = MergePollRules.Decide(Since, Since.AddHours(3), MergePollObservation.ClosedUnmerged);

        Assert.Equal((MergePollDecisionKind.Fail, ErrorCodes.PrClosed), (decision.Kind, decision.ErrorCode));
        Assert.Contains("Reopen", decision.Message, StringComparison.Ordinal);
    }

    public static TheoryData<ReleaseStatus, ReleaseStatus> AllAttemptPairs()
    {
        var data = new TheoryData<ReleaseStatus, ReleaseStatus>();
        foreach (var from in Enum.GetValues<ReleaseStatus>())
        {
            foreach (var to in Enum.GetValues<ReleaseStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllAttemptPairs))]
    public void S4_14_FR65_Release_attempt_status_matrix(ReleaseStatus from, ReleaseStatus to)
    {
        var allowed = (from, to) is (ReleaseStatus.InProgress, ReleaseStatus.Succeeded)
            or (ReleaseStatus.InProgress, ReleaseStatus.Failed)
            or (ReleaseStatus.InProgress, ReleaseStatus.AwaitingMerge)
            or (ReleaseStatus.AwaitingMerge, ReleaseStatus.Succeeded)
            or (ReleaseStatus.AwaitingMerge, ReleaseStatus.Failed);

        Assert.Equal(allowed, ReleaseAttemptRules.CanTransition(from, to));
    }

    [Fact]
    public void FR65_Attempt_status_wire_value_awaiting_merge() =>
        Assert.Equal("awaiting_merge", ReleaseStatus.AwaitingMerge.ToWire());
}
