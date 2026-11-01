using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Domain.Releases;

namespace Aide.UnitTests.Releases;

/// <summary>FR-153, §4.21: production migrations relative to merge readiness, on every Git host's reason codes.</summary>
public class ProductionMigrationRulesTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MergeReadinessReasonCode.ReviewRequired)]
    [InlineData(MergeReadinessReasonCode.ChangesRequested)]
    [InlineData(MergeReadinessReasonCode.ChecksFailing)]
    [InlineData(MergeReadinessReasonCode.ChecksPending)]
    [InlineData(MergeReadinessReasonCode.BranchProtected)]
    [InlineData(MergeReadinessReasonCode.MergeConflict)]
    [InlineData(MergeReadinessReasonCode.PrNotOpen)]
    [InlineData(MergeReadinessReasonCode.Draft)]
    [InlineData(MergeReadinessReasonCode.DiscussionsUnresolved)]
    [InlineData(MergeReadinessReasonCode.RebaseRequired)]
    public void FR153_A_reason_the_PR_cant_merge_holds_the_migrations_back(MergeReadinessReasonCode code) =>
        Assert.Equal(code, ProductionMigrationRules.BlockingReason([MergeReadinessReasonCode.ManualStrategy, code]));

    [Theory]
    [InlineData(MergeReadinessReasonCode.ManualStrategy)]
    [InlineData(MergeReadinessReasonCode.NoPermission)]
    [InlineData(MergeReadinessReasonCode.MergeMethodNotAllowed)]
    public void FR153_A_reason_about_AIDEs_own_merge_doesnt_hold_the_migrations_back(MergeReadinessReasonCode code) =>
        Assert.Null(ProductionMigrationRules.BlockingReason([code]));

    [Fact]
    public void FR153_No_reasons_is_readiness_passed() =>
        Assert.Null(ProductionMigrationRules.BlockingReason([]));

    [Fact]
    public void FR153_FR43_A_conflict_comes_first_then_a_PR_that_isnt_open_then_the_first_other_reason()
    {
        Assert.Equal(
            MergeReadinessReasonCode.MergeConflict,
            ProductionMigrationRules.BlockingReason([MergeReadinessReasonCode.ReviewRequired, MergeReadinessReasonCode.PrNotOpen, MergeReadinessReasonCode.MergeConflict]));
        Assert.Equal(
            MergeReadinessReasonCode.PrNotOpen,
            ProductionMigrationRules.BlockingReason([MergeReadinessReasonCode.ReviewRequired, MergeReadinessReasonCode.PrNotOpen]));
        Assert.Equal(
            MergeReadinessReasonCode.ChecksFailing,
            ProductionMigrationRules.BlockingReason([MergeReadinessReasonCode.NoPermission, MergeReadinessReasonCode.ChecksFailing, MergeReadinessReasonCode.ChecksPending]));
    }

    [Fact]
    public void FR153_GitHub_review_required_and_checks_failing_verdicts_hold_the_migrations_back()
    {
        var verdict = MergeReadinessRules.Evaluate(
            new MergeReadinessFacts
            {
                ReviewDecision = "REVIEW_REQUIRED",
                Checks = [new CheckObservation("ci", CheckRunStatus.Completed, CheckRunConclusion.Failure, true, null)],
            },
            MergeStrategy.AideMerges);

        Assert.Equal(MergeReadinessReasonCode.ReviewRequired, Blocking(verdict));
    }

    [Fact]
    public void FR153_GitLab_not_approved_and_a_failed_pipeline_hold_the_migrations_back()
    {
        var approval = GitLabMergeReadiness.Map(new GitLabMergeFacts { DetailedMergeStatus = "not_approved" }, MergeStrategy.Manual);
        var pipeline = GitLabMergeReadiness.Map(new GitLabMergeFacts { DetailedMergeStatus = "ci_must_pass", HeadPipelineStatus = "failed" }, MergeStrategy.AideMerges);

        Assert.Equal(MergeReadinessReasonCode.ReviewRequired, Blocking(approval.Verdict!));
        Assert.Equal(MergeReadinessReasonCode.ChecksFailing, Blocking(pipeline.Verdict!));
        Assert.Equal(MergeReadinessReasonCode.ChecksPending, Blocking(GitLabMergeReadiness.StillChecking(MergeStrategy.Manual)));
    }

    [Fact]
    public void FR153_Bitbucket_blocking_approvals_and_builds_hold_the_migrations_back()
    {
        var approval = MergeabilityChecks.Map([new MergeabilityCheck("standard_merge_check", "require_approvals_to_merge", "FAILED", true, true)], MergeStrategy.Manual);
        var builds = MergeabilityChecks.Map([new MergeabilityCheck("standard_merge_check", "require_passing_builds_to_merge", "FAILED", true, true)], MergeStrategy.AideMerges);

        Assert.Equal(MergeReadinessReasonCode.ReviewRequired, Blocking(approval.Verdict!));
        Assert.Equal(MergeReadinessReasonCode.ChecksFailing, Blocking(builds.Verdict!));
    }

    [Theory]
    [InlineData(GitProvider.GithubPat, null, null, true)]
    [InlineData(GitProvider.GithubApp, null, null, true)]
    [InlineData(GitProvider.GithubPat, "BLOCKED", "REVIEW_REQUIRED", false)]
    [InlineData(GitProvider.GithubPat, "BLOCKED", null, false)]
    [InlineData(GitProvider.Gitlab, null, null, false)]
    [InlineData(GitProvider.Bitbucket, null, null, false)]
    [InlineData(GitProvider.Local, null, null, false)]
    public void FR153_FR84_Only_GitHub_without_its_GraphQL_answers_has_an_unknown_review(GitProvider provider, string? state, string? decision, bool expected) =>
        Assert.Equal(expected, ProductionMigrationRules.ReviewUnknown(provider, state, decision));

    [Theory]
    [InlineData(GitProvider.Gitlab, "checking", true)]
    [InlineData(GitProvider.Gitlab, "unchecked", true)]
    [InlineData(GitProvider.Gitlab, "preparing", true)]
    [InlineData(GitProvider.Gitlab, "approvals_syncing", true)]
    [InlineData(GitProvider.Gitlab, "mergeable", false)]
    [InlineData(GitProvider.Gitlab, "not_approved", false)]
    [InlineData(GitProvider.Gitlab, null, false)]
    [InlineData(GitProvider.GithubApp, "checking", false)]
    [InlineData(GitProvider.Bitbucket, "checking", false)]
    public void FR138_S4_21_GitLab_still_computing_its_status_is_unknown_readiness(GitProvider provider, string? status, bool expected) =>
        Assert.Equal(expected, ProductionMigrationRules.ReadinessUnknown(provider, status));

    [Fact]
    public void FR153_An_unknown_review_doesnt_hold_the_migrations_back_but_a_failing_check_still_does()
    {
        Assert.Null(ProductionMigrationRules.BlockingReason([MergeReadinessReasonCode.ReviewRequired], reviewUnknown: true));
        Assert.Equal(
            MergeReadinessReasonCode.ChecksFailing,
            ProductionMigrationRules.BlockingReason([MergeReadinessReasonCode.ReviewRequired, MergeReadinessReasonCode.ChecksFailing], reviewUnknown: true));
    }

    [Fact]
    public void FR153_The_poll_runs_when_nothing_ran_yet() =>
        Assert.Equal(PreMergeMigrationStep.Run, ProductionMigrationRules.NextStep(null, Head, false, Now));

    [Theory]
    [InlineData(MigrationRunStatus.Succeeded)]
    [InlineData(MigrationRunStatus.Failed)]
    [InlineData(MigrationRunStatus.Running)]
    public void FR153_The_poll_runs_again_on_a_moved_head_whatever_the_last_run_did(MigrationRunStatus status) =>
        Assert.Equal(PreMergeMigrationStep.Run, ProductionMigrationRules.NextStep(new PreMergeRun(Other, status, Now, null), Head, false, Now));

    [Fact]
    public void FR153_A_head_already_migrated_isnt_run_again() =>
        Assert.Equal(PreMergeMigrationStep.None, ProductionMigrationRules.NextStep(new PreMergeRun(Head, MigrationRunStatus.Succeeded, Now, Now), Head, true, Now));

    [Fact]
    public void FR153_A_failed_run_isnt_repeated_on_every_poll_only_once_the_database_is_usable_again()
    {
        var failed = new PreMergeRun(Head, MigrationRunStatus.Failed, Now, Now);

        Assert.Equal(PreMergeMigrationStep.None, ProductionMigrationRules.NextStep(failed, Head, false, Now.AddDays(1)));
        Assert.Equal(PreMergeMigrationStep.Run, ProductionMigrationRules.NextStep(failed, Head, true, Now.AddDays(1)));
    }

    [Fact]
    public void FR153_A_running_run_is_left_alone_until_it_counts_as_interrupted()
    {
        var running = new PreMergeRun(Head, MigrationRunStatus.Running, Now, null);

        Assert.Equal(PreMergeMigrationStep.None, ProductionMigrationRules.NextStep(running, Head, false, Now + ProductionMigrationRules.InterruptedAfter));
        Assert.Equal(PreMergeMigrationStep.RunAfterInterruption, ProductionMigrationRules.NextStep(running, Head, false, Now + ProductionMigrationRules.InterruptedAfter + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void FR153_A_merge_counts_as_migrated_only_on_a_head_a_run_succeeded_on()
    {
        Assert.False(ProductionMigrationRules.MergedUnmigrated(Head, [Other, Head]));
        Assert.True(ProductionMigrationRules.MergedUnmigrated(Head, [Other]));
        Assert.True(ProductionMigrationRules.MergedUnmigrated(Head, []));
    }

    [Fact]
    public void FR153_A_short_Bitbucket_merged_head_matches_its_full_hash() =>
        Assert.False(ProductionMigrationRules.MergedUnmigrated(Head[..12], [Head]));

    [Fact]
    public void FR153_An_unknown_merged_head_counts_only_when_nothing_was_migrated()
    {
        Assert.True(ProductionMigrationRules.MergedUnmigrated(null, []));
        Assert.False(ProductionMigrationRules.MergedUnmigrated(null, [Head]));
    }

    private static MergeReadinessReasonCode? Blocking(MergeReadinessVerdict verdict) =>
        ProductionMigrationRules.BlockingReason(verdict.Reasons.Select(r => r.Code));
}
