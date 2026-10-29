using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Domain.Releases;

namespace Aide.UnitTests.GitHosts;

/// <summary>FR-138, §4.19: GitLab and Bitbucket merge readiness mapped onto the v2 reason codes.</summary>
public class GitHostMergeReadinessTests
{
    private static readonly GitLabMergeFacts Mergeable = new()
    {
        DetailedMergeStatus = "mergeable",
        Approved = true,
        UserAccessLevel = GitLabMergeReadiness.MaintainerAccess,
        ProtectedBranchMergeAccessLevels = [40],
        HeadPipelineStatus = "success",
        PipelinesMustSucceed = true,
    };

    private static IReadOnlyList<MergeReadinessReasonCode> Codes(HostMergeReadiness readiness) =>
        [.. readiness.Verdict!.Reasons.Select(r => r.Code)];

    [Fact]
    public void FR138_GitLab_not_approved_is_review_required()
    {
        var readiness = GitLabMergeReadiness.Map(Mergeable with { DetailedMergeStatus = "not_approved", Approved = false }, MergeStrategy.AideMerges);

        Assert.False(readiness.RePoll);
        Assert.False(readiness.Verdict!.CanMerge);
        Assert.Equal([MergeReadinessReasonCode.ReviewRequired], Codes(readiness));
    }

    [Fact]
    public void FR138_GitLab_mergeable_can_merge_and_auto_merges_with_aide_merges()
    {
        var readiness = GitLabMergeReadiness.Map(Mergeable, MergeStrategy.AideMerges);

        Assert.True(readiness.Verdict!.CanMerge);
        Assert.True(readiness.Verdict.WillAutoMerge);
        Assert.Empty(readiness.Verdict.Reasons);
    }

    [Fact]
    public void FR138_GitLab_manual_strategy_adds_manual_strategy_without_changing_canMerge()
    {
        var readiness = GitLabMergeReadiness.Map(Mergeable, MergeStrategy.Manual);

        Assert.True(readiness.Verdict!.CanMerge);
        Assert.False(readiness.Verdict.WillAutoMerge);
        Assert.Equal([MergeReadinessReasonCode.ManualStrategy], Codes(readiness));
    }

    [Theory]
    [InlineData("unchecked")]
    [InlineData("checking")]
    [InlineData("preparing")]
    [InlineData("approvals_syncing")]
    public void S4_19_GitLab_transient_merge_statuses_re_poll(string status)
    {
        var readiness = GitLabMergeReadiness.Map(Mergeable with { DetailedMergeStatus = status }, MergeStrategy.AideMerges);

        Assert.True(readiness.RePoll);
        Assert.Null(readiness.Verdict);
        Assert.True(GitLabMergeReadiness.IsTransient(status));
    }

    [Theory]
    [InlineData("conflict", MergeReadinessReasonCode.MergeConflict)]
    [InlineData("need_rebase", MergeReadinessReasonCode.RebaseRequired)]
    [InlineData("draft_status", MergeReadinessReasonCode.Draft)]
    [InlineData("discussions_not_resolved", MergeReadinessReasonCode.DiscussionsUnresolved)]
    [InlineData("requested_changes", MergeReadinessReasonCode.ChangesRequested)]
    [InlineData("ci_still_running", MergeReadinessReasonCode.ChecksPending)]
    [InlineData("status_checks_must_pass", MergeReadinessReasonCode.ChecksFailing)]
    [InlineData("merge_request_blocked", MergeReadinessReasonCode.BranchProtected)]
    [InlineData("locked_paths", MergeReadinessReasonCode.BranchProtected)]
    [InlineData("not_open", MergeReadinessReasonCode.PrNotOpen)]
    public void FR138_GitLab_detailed_merge_status_maps_to_a_v2_reason(string status, MergeReadinessReasonCode expected)
    {
        var readiness = GitLabMergeReadiness.Map(Mergeable with { DetailedMergeStatus = status }, MergeStrategy.AideMerges);

        Assert.False(readiness.Verdict!.CanMerge);
        Assert.Equal([expected], Codes(readiness));
    }

    [Fact]
    public void FR138_GitLab_facts_give_every_reason_in_v2_order_without_duplicates()
    {
        var facts = Mergeable with
        {
            DetailedMergeStatus = "conflict",
            HasConflicts = true,
            Draft = true,
            Approved = false,
            ApprovalsLeft = 2,
            BlockingDiscussionsResolved = false,
            HeadPipelineStatus = "failed",
            UserAccessLevel = GitLabMergeReadiness.DeveloperAccess,
        };

        var readiness = GitLabMergeReadiness.Map(facts, MergeStrategy.AideMerges);

        Assert.Equal(
            [
                MergeReadinessReasonCode.NoPermission, MergeReadinessReasonCode.MergeConflict, MergeReadinessReasonCode.Draft,
                MergeReadinessReasonCode.ReviewRequired, MergeReadinessReasonCode.DiscussionsUnresolved, MergeReadinessReasonCode.ChecksFailing,
            ],
            Codes(readiness));
        Assert.Equal("2 more approvals are required.", readiness.Verdict!.Reasons[3].Message);
    }

    [Fact]
    public void FR138_GitLab_running_pipeline_is_pending_only_when_pipelines_must_succeed()
    {
        var running = Mergeable with { HeadPipelineStatus = "running" };

        Assert.Equal([MergeReadinessReasonCode.ChecksPending], Codes(GitLabMergeReadiness.Map(running, MergeStrategy.AideMerges)));
        Assert.True(GitLabMergeReadiness.Map(running with { PipelinesMustSucceed = false }, MergeStrategy.AideMerges).Verdict!.CanMerge);
    }

    [Fact]
    public void FR138_GitLab_ci_must_pass_without_a_pipeline_is_pending()
    {
        var readiness = GitLabMergeReadiness.Map(Mergeable with { DetailedMergeStatus = "ci_must_pass", HeadPipelineStatus = null }, MergeStrategy.AideMerges);

        Assert.Equal([MergeReadinessReasonCode.ChecksPending], Codes(readiness));
    }

    [Theory]
    [InlineData(30, null, true)]
    [InlineData(20, null, false)]
    [InlineData(30, new[] { 40 }, false)]
    [InlineData(40, new[] { 40 }, true)]
    [InlineData(50, new[] { 0 }, false)]
    [InlineData(30, new[] { 0, 30 }, true)]
    public void S4_19_Merging_a_protected_branch_needs_one_of_its_merge_access_levels(int level, int[]? allowed, bool expected) =>
        Assert.Equal(expected, GitLabMergeReadiness.MayMerge(level, allowed));

    [Fact]
    public void FR138_Bitbucket_blocking_failed_required_builds_check_is_checks_failing()
    {
        MergeabilityCheck[] checks =
        [
            new("standard_merge_check", "require_passing_builds_to_merge", "FAILED", Required: true, Blocking: true),
            new("standard_merge_check", "require_approvals_to_merge", "PASSED", Required: true, Blocking: false),
        ];

        var readiness = MergeabilityChecks.Map(checks, MergeStrategy.AideMerges);

        Assert.False(readiness.Verdict!.CanMerge);
        Assert.Equal([MergeReadinessReasonCode.ChecksFailing], Codes(readiness));
        Assert.True(MergeabilityChecks.RequiresPassingBuilds(checks));
    }

    [Fact]
    public void FR138_Bitbucket_is_ready_when_no_check_is_blocking()
    {
        MergeabilityCheck[] checks =
        [
            new("standard_merge_check", "require_passing_builds_to_merge", "PENDING", Required: false, Blocking: false),
            new("standard_merge_check", "require_approvals_to_merge", "FAILED", Required: false, Blocking: false),
        ];

        var readiness = MergeabilityChecks.Map(checks, MergeStrategy.AideMerges);

        Assert.True(readiness.Verdict!.CanMerge);
        Assert.True(readiness.Verdict.WillAutoMerge);
        Assert.False(MergeabilityChecks.RequiresPassingBuilds(checks));
    }

    [Theory]
    [InlineData("require_passing_builds_to_merge", "PENDING", MergeReadinessReasonCode.ChecksPending)]
    [InlineData("require_passing_builds_to_merge", "UNKNOWN", MergeReadinessReasonCode.ChecksPending)]
    [InlineData("require_approvals_to_merge", "FAILED", MergeReadinessReasonCode.ReviewRequired)]
    [InlineData("require_default_reviewer_approvals_to_merge", "FAILED", MergeReadinessReasonCode.ReviewRequired)]
    [InlineData("require_no_changes_requested", "FAILED", MergeReadinessReasonCode.ChangesRequested)]
    [InlineData("require_tasks_to_be_completed", "FAILED", MergeReadinessReasonCode.DiscussionsUnresolved)]
    [InlineData("merge_conflicts", "FAILED", MergeReadinessReasonCode.MergeConflict)]
    [InlineData("restrict_merge_access", "FAILED", MergeReadinessReasonCode.NoPermission)]
    [InlineData("require_all_dependencies_merged", "FAILED", MergeReadinessReasonCode.BranchProtected)]
    public void FR138_Bitbucket_blocking_checks_map_to_v2_reasons(string key, string status, MergeReadinessReasonCode expected)
    {
        var readiness = MergeabilityChecks.Map([new("standard_merge_check", key, status, true, true)], MergeStrategy.AideMerges);

        Assert.Equal([expected], Codes(readiness));
    }

    [Fact]
    public void FR138_Bitbucket_draft_and_closed_pull_requests()
    {
        Assert.Equal([MergeReadinessReasonCode.Draft], Codes(MergeabilityChecks.Map([], MergeStrategy.AideMerges, draft: true)));
        Assert.Equal(
            [MergeReadinessReasonCode.PrNotOpen, MergeReadinessReasonCode.ManualStrategy],
            Codes(MergeabilityChecks.Map([], MergeStrategy.Manual, pullRequestOpen: false)));
    }
}
