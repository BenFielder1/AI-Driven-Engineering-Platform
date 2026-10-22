using Aide.Domain.Entities;
using Aide.Domain.Releases;

namespace Aide.UnitTests.Releases;

/// <summary>FR-84: merge readiness evaluated from what GitHub reports.</summary>
public class MergeReadinessEvaluateTests
{
    private static readonly MergeReadinessFacts Clean = new()
    {
        CanWrite = true,
        AllowedMergeMethods = ["merge", "squash"],
        MergeStateStatus = "CLEAN",
        ReviewDecision = "APPROVED",
    };

    private static CheckObservation Check(string name, CheckRunStatus status, CheckRunConclusion? conclusion, bool required = true) =>
        new(name, status, conclusion, required, $"https://github.com/acme/widgets/runs/{name.Length}");

    [Fact]
    public void FR84_Given_a_ruleset_requiring_1_approval_and_none_yet_then_canMerge_false_with_review_required()
    {
        var verdict = MergeReadinessRules.Evaluate(
            Clean with { RequiredApprovingReviews = 1, ReviewDecision = "REVIEW_REQUIRED", MergeStateStatus = "BLOCKED" },
            MergeStrategy.AideMerges);

        Assert.False(verdict.CanMerge);
        Assert.False(verdict.WillAutoMerge);
        var reason = Assert.Single(verdict.Reasons);
        Assert.Equal((MergeReadinessReasonCode.ReviewRequired, "1 approving review is required."), (reason.Code, reason.Message));
    }

    [Fact]
    public void FR84_A_clean_PR_with_permission_merges_automatically_under_aide_merges()
    {
        var verdict = MergeReadinessRules.Evaluate(Clean, MergeStrategy.AideMerges);

        Assert.Equal((true, true), (verdict.CanMerge, verdict.WillAutoMerge));
        Assert.Empty(verdict.Reasons);
    }

    [Fact]
    public void FR84_FR65_Manual_strategy_adds_manual_strategy_and_never_auto_merges()
    {
        var verdict = MergeReadinessRules.Evaluate(Clean, MergeStrategy.Manual);

        Assert.Equal((true, false), (verdict.CanMerge, verdict.WillAutoMerge));
        Assert.Equal([MergeReadinessReasonCode.ManualStrategy], verdict.Reasons.Select(r => r.Code));
    }

    [Fact]
    public void FR84_Every_blocking_fact_has_its_reason_in_order()
    {
        var verdict = MergeReadinessRules.Evaluate(
            Clean with
            {
                CanWrite = false,
                AllowedMergeMethods = ["squash"],
                MergeStateStatus = "DIRTY",
                ReviewDecision = "CHANGES_REQUESTED",
                Checks = [Check("ci / test", CheckRunStatus.Completed, CheckRunConclusion.Failure), Check("lint", CheckRunStatus.InProgress, null), Check("docs", CheckRunStatus.Completed, CheckRunConclusion.Failure, required: false)],
            },
            MergeStrategy.AideMerges);

        Assert.False(verdict.CanMerge);
        Assert.Equal(
            [MergeReadinessReasonCode.NoPermission, MergeReadinessReasonCode.MergeMethodNotAllowed, MergeReadinessReasonCode.MergeConflict, MergeReadinessReasonCode.ChangesRequested, MergeReadinessReasonCode.ChecksFailing, MergeReadinessReasonCode.ChecksPending],
            verdict.Reasons.Select(r => r.Code));
        Assert.Contains("ci / test", verdict.Reasons[4].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("docs", verdict.Reasons[4].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR84_No_open_PR_is_pr_not_open_alone()
    {
        var verdict = MergeReadinessRules.Evaluate(Clean with { PullRequestOpen = false, CanWrite = false }, MergeStrategy.AideMerges);

        Assert.Equal([MergeReadinessReasonCode.PrNotOpen], verdict.Reasons.Select(r => r.Code));
    }

    [Theory]
    [InlineData("BLOCKED")]
    [InlineData("BEHIND")]
    public void FR84_Blocked_or_behind_without_another_reason_is_branch_protected(string state)
    {
        var verdict = MergeReadinessRules.Evaluate(Clean with { MergeStateStatus = state }, MergeStrategy.AideMerges);

        Assert.Equal([MergeReadinessReasonCode.BranchProtected], verdict.Reasons.Select(r => r.Code));
    }

    [Fact]
    public void FR84_Without_GraphQL_required_approvals_count_as_missing_and_unknown_facts_do_not_block()
    {
        var degraded = MergeReadinessRules.Evaluate(
            new MergeReadinessFacts { GraphQlAvailable = false, RequiredApprovingReviews = 2 }, MergeStrategy.AideMerges);
        var unknown = MergeReadinessRules.Evaluate(new MergeReadinessFacts { GraphQlAvailable = false }, MergeStrategy.AideMerges);

        Assert.Equal([MergeReadinessReasonCode.ReviewRequired], degraded.Reasons.Select(r => r.Code));
        Assert.Contains("2 approving reviews are required.", degraded.Reasons[0].Message, StringComparison.Ordinal);
        Assert.True(unknown.CanMerge);
    }

    [Fact]
    public void FR84_Required_contexts_mark_checks_and_missing_ones_are_pending_placeholders()
    {
        var marked = MergeReadinessRules.MarkRequired(
            [Check("ci / test", CheckRunStatus.Completed, CheckRunConclusion.Success, false), Check("ci / test", CheckRunStatus.Completed, CheckRunConclusion.Failure, false), Check("other", CheckRunStatus.Queued, null, false)],
            ["ci / test", "deploy"],
            includeMissingRequired: true);

        Assert.Equal(
            [("ci / test", true, (CheckRunConclusion?)CheckRunConclusion.Success), ("other", false, null), ("deploy", true, null)],
            marked.Select(c => (c.Name, c.Required, c.Conclusion)));
        Assert.Equal(2, MergeReadinessRules.MarkRequired(marked.Take(2), ["deploy"]).Count);
    }

    [Theory]
    [InlineData("success", CheckRunConclusion.Success)]
    [InlineData("failure", CheckRunConclusion.Failure)]
    [InlineData("stale", CheckRunConclusion.Failure)]
    [InlineData("startup_failure", CheckRunConclusion.Failure)]
    [InlineData("timed_out", CheckRunConclusion.TimedOut)]
    [InlineData("action_required", CheckRunConclusion.ActionRequired)]
    [InlineData(null, null)]
    public void FR85_Check_run_conclusions_map_from_GitHub(string? wire, CheckRunConclusion? expected) =>
        Assert.Equal(expected, MergeReadinessRules.CheckConclusionOf(wire));

    [Theory]
    [InlineData("success", CheckRunStatus.Completed, CheckRunConclusion.Success)]
    [InlineData("error", CheckRunStatus.Completed, CheckRunConclusion.Failure)]
    [InlineData("failure", CheckRunStatus.Completed, CheckRunConclusion.Failure)]
    [InlineData("pending", CheckRunStatus.InProgress, null)]
    public void FR85_Commit_statuses_map_to_checks(string state, CheckRunStatus status, CheckRunConclusion? conclusion) =>
        Assert.Equal((status, conclusion), MergeReadinessRules.CommitStatusOf(state));
}
