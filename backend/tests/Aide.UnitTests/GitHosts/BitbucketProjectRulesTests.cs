using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Domain.Releases;
using Aide.Domain.Review;
using Aide.Domain.Stack;

namespace Aide.UnitTests.GitHosts;

/// <summary>
/// FR-135–FR-141 (Bitbucket parts): project binding fields, merge strategies, mergeability → reasons, commit statuses,
/// merge tasks, pull request states and commit hash comparison.
/// </summary>
public class BitbucketProjectRulesTests
{
    private const string Uuid = "{1c3f5a7e-0000-4000-8000-000000000001}";

    // ---------- FR-135, FR-136: create fields ----------

    [Fact]
    public void FR135_FR136_A_bitbucket_create_needs_the_connection_repo_uuid_and_workspace_slug()
    {
        Assert.True(BitbucketProjectRules.ValidateCreate(null, Guid.NewGuid(), Uuid, "acme/todo-app", null, null, null, "git.").IsSuccess);

        var missing = BitbucketProjectRules.ValidateCreate(Guid.NewGuid(), null, "42", "acme", "https://bitbucket.org/acme/x", "t", 5, "git.");
        Assert.Equal(ErrorCodes.Validation, missing.Error!.Code);
        Assert.Equal(
            ["git.instanceId", "git.connectionId", "git.repoId", "git.repoFullName", "git.repoUrl", "git.token", "git.installationId"],
            missing.Error.Details.Select(d => d[..d.IndexOf(':', StringComparison.Ordinal)]));
    }

    [Fact]
    public void FR136_Connection_and_repo_id_are_refused_for_GitHub_and_Local_but_not_for_the_hosts()
    {
        Assert.True(BitbucketProjectRules.ValidateNotSent(GitProvider.Bitbucket, null, Guid.NewGuid(), Uuid).IsSuccess);
        Assert.True(BitbucketProjectRules.ValidateNotSent(GitProvider.Gitlab, Guid.NewGuid(), Guid.NewGuid(), "7").IsSuccess);
        var local = BitbucketProjectRules.ValidateNotSent(GitProvider.Local, Guid.NewGuid(), Guid.NewGuid(), Uuid, "git.");
        Assert.Equal(3, local.Error!.Details.Count);
    }

    [Theory]
    [InlineData("{1c3f5a7e-0000-4000-8000-000000000001}", true)]
    [InlineData("{r-acme-todo}", true)]
    [InlineData("1c3f5a7e-0000-4000-8000-000000000001", false)]
    [InlineData("{}", false)]
    [InlineData("{a b}", false)]
    public void FR136_Repo_uuid_form(string id, bool valid) => Assert.Equal(valid, BitbucketProjectRules.IsValidRepoId(id));

    [Theory]
    [InlineData("acme/todo-app", true)]
    [InlineData("acme/Todo", false)]
    [InlineData("acme/sub/todo", false)]
    [InlineData("acme", false)]
    public void FR136_Workspace_slug_form(string name, bool valid) => Assert.Equal(valid, BitbucketProjectRules.IsValidFullName(name));

    [Fact]
    public void FR136_Permission_and_source_branch_queries_escape_quotes() =>
        Assert.Equal(("repository.uuid=\"{a\\\"b}\"", "source.branch.name=\"aide/x/v0.1.0\""), (BitbucketProjectRules.PermissionQuery("{a\"b}"), BitbucketProjectRules.SourceBranchQuery("aide/x/v0.1.0")));

    [Fact]
    public void FR136_R5_A_Bitbucket_repository_fixes_the_VCS_to_vcs_bitbucket() =>
        Assert.Equal("vcs-bitbucket", StackPinning.RepoVcs(GitProvider.Bitbucket));

    // ---------- FR-140: merge strategy ----------

    [Theory]
    [InlineData(MergeStrategy.AideMerges, "merge_commit")]
    [InlineData(MergeStrategy.Squash, "squash")]
    [InlineData(MergeStrategy.FastForward, "fast_forward")]
    [InlineData(MergeStrategy.Manual, "merge_commit")]
    public void FR140_Bitbucket_merge_strategy_maps_from_the_project_setting(MergeStrategy strategy, string expected) =>
        Assert.Equal(expected, BitbucketProjectRules.MergeStrategyParameter(strategy));

    [Theory]
    [InlineData(GitProvider.Bitbucket, MergeStrategy.FastForward, true)]
    [InlineData(GitProvider.Bitbucket, MergeStrategy.Squash, true)]
    [InlineData(GitProvider.Gitlab, MergeStrategy.FastForward, false)]
    [InlineData(GitProvider.GithubApp, MergeStrategy.Squash, false)]
    [InlineData(GitProvider.Local, MergeStrategy.Manual, true)]
    [InlineData(GitProvider.Gitlab, MergeStrategy.AideMerges, true)]
    public void FR140_fast_forward_and_squash_are_Bitbucket_only(GitProvider provider, MergeStrategy strategy, bool allowed)
    {
        var result = BitbucketProjectRules.ValidateMergeStrategy(provider, strategy);
        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.StartsWith("mergeStrategy:", Assert.Single(result.Error!.Details), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FR140_AIDE_merges_under_every_strategy_but_manual()
    {
        Assert.True(MergeReadinessRules.WillAutoMerge(MergeStrategy.FastForward, true));
        Assert.True(MergeReadinessRules.WillAutoMerge(MergeStrategy.Squash, true));
        Assert.False(MergeReadinessRules.WillAutoMerge(MergeStrategy.Manual, true));
        Assert.False(MergeReadinessRules.WillAutoMerge(MergeStrategy.AideMerges, false));
    }

    // ---------- FR-138: mergeability → reasons ----------

    [Fact]
    public void FR138_Given_a_blocking_failed_require_passing_builds_check_then_checks_failing()
    {
        var readiness = MergeabilityChecks.Map([new MergeabilityCheck("standard_merge_check", "require_passing_builds_to_merge", "FAILED", true, true)], MergeStrategy.AideMerges);
        Assert.False(readiness.Verdict!.CanMerge);
        Assert.Equal([MergeReadinessReasonCode.ChecksFailing], readiness.Verdict.Reasons.Select(r => r.Code));
    }

    [Theory]
    [InlineData("require_passing_builds_to_merge", "PENDING", MergeReadinessReasonCode.ChecksPending)]
    [InlineData("require_approvals_to_merge", "FAILED", MergeReadinessReasonCode.ReviewRequired)]
    [InlineData("require_default_reviewer_approvals_to_merge", "FAILED", MergeReadinessReasonCode.ReviewRequired)]
    [InlineData("require_tasks_to_be_completed", "FAILED", MergeReadinessReasonCode.DiscussionsUnresolved)]
    [InlineData("merge_conflicts", "FAILED", MergeReadinessReasonCode.MergeConflict)]
    [InlineData("pull_request_draft", "FAILED", MergeReadinessReasonCode.Draft)]
    [InlineData("restrict_merges_write_access", "FAILED", MergeReadinessReasonCode.NoPermission)]
    [InlineData("fast_forward_rebase_needed", "FAILED", MergeReadinessReasonCode.RebaseRequired)]
    [InlineData("something_new", "FAILED", MergeReadinessReasonCode.BranchProtected)]
    public void FR138_Blocking_checks_map_to_the_v2_reason_codes(string key, string status, MergeReadinessReasonCode expected) =>
        Assert.Equal(expected, Assert.Single(MergeabilityChecks.Map([new MergeabilityCheck("standard_merge_check", key, status, true, true)], MergeStrategy.AideMerges).Verdict!.Reasons).Code);

    [Fact]
    public void FR138_Ready_means_no_blocking_check()
    {
        var readiness = MergeabilityChecks.Map(
            [new MergeabilityCheck("standard_merge_check", "require_approvals_to_merge", "FAILED", false, false), new MergeabilityCheck("standard_merge_check", "require_passing_builds_to_merge", "PASSED", true, false)],
            MergeStrategy.AideMerges);
        Assert.Equal((true, true), (readiness.Verdict!.CanMerge, readiness.Verdict.WillAutoMerge));
        Assert.Empty(readiness.Verdict.Reasons);
    }

    [Fact]
    public void FR138_AIDEs_own_draft_is_ignored_until_AIDE_marked_it_ready()
    {
        MergeabilityCheck[] checks = [new("standard_merge_check", "pull_request_draft", "FAILED", true, true)];
        Assert.True(MergeabilityChecks.Map(MergeabilityChecks.IgnoringAideDraft(checks), MergeStrategy.AideMerges).Verdict!.CanMerge);
        Assert.Equal(
            MergeReadinessReasonCode.Draft,
            Assert.Single(MergeabilityChecks.Map(MergeabilityChecks.IgnoringAideDraft(checks, markedReady: true), MergeStrategy.AideMerges).Verdict!.Reasons).Code);
    }

    [Fact]
    public void FR138_Manual_strategy_adds_manual_strategy_naming_Bitbucket()
    {
        var verdict = MergeabilityChecks.Map([], MergeStrategy.Manual).Verdict!;
        Assert.Equal((true, false), (verdict.CanMerge, verdict.WillAutoMerge));
        Assert.Contains("Bitbucket", Assert.Single(verdict.Reasons, r => r.Code == MergeReadinessReasonCode.ManualStrategy).Message, StringComparison.Ordinal);
    }

    // ---------- FR-139: statuses ----------

    [Fact]
    public void FR139_A_failed_build_is_major_when_the_branch_requires_passing_builds_else_minor()
    {
        BitbucketCommitStatus[] statuses = [new("build", "FAILED", "https://ci.example/1"), new("lint", "SUCCESSFUL", null), new("e2e", "INPROGRESS", null), new("old", "STOPPED", null)];
        var required = MergeabilityChecks.RequiresPassingBuilds([new MergeabilityCheck("standard_merge_check", "require_passing_builds_to_merge", "PASSED", true, false)]);
        Assert.True(required);

        var major = CommitStatusFindings.Findings(CommitStatusFindings.FromBitbucket(statuses, required), CommitStatusHost.Bitbucket);
        Assert.Contains(major, f => f.Severity == Severity.Major && f.Message == "required build build failed" && f.Evidence == "build: https://ci.example/1");
        var minor = CommitStatusFindings.Findings(CommitStatusFindings.FromBitbucket(statuses, false), CommitStatusHost.Bitbucket);
        Assert.All(minor, f => Assert.Equal(Severity.Minor, f.Severity));
        Assert.Contains(CommitStatusFindings.FromBitbucket(statuses, required), MergeReadinessRules.IsPending);
    }

    // ---------- FR-140: merge tasks, PR states, hashes, tag URL ----------

    [Theory]
    [InlineData("SUCCESS", BitbucketMergeTaskState.Succeeded)]
    [InlineData("PENDING", BitbucketMergeTaskState.Pending)]
    [InlineData(null, BitbucketMergeTaskState.Pending)]
    [InlineData("FAILED", BitbucketMergeTaskState.Failed)]
    public void FR140_Merge_task_status(string? status, BitbucketMergeTaskState expected) => Assert.Equal(expected, BitbucketProjectRules.TaskStateOf(status));

    [Theory]
    [InlineData("MERGED", MergePollObservation.Merged)]
    [InlineData("DECLINED", MergePollObservation.ClosedUnmerged)]
    [InlineData("SUPERSEDED", MergePollObservation.ClosedUnmerged)]
    [InlineData("OPEN", MergePollObservation.Open)]
    public void FR140_Pull_request_state_for_the_merge_poll(string state, MergePollObservation expected) => Assert.Equal(expected, BitbucketProjectRules.ObservationOf(state));

    [Theory]
    [InlineData(400, HostMergeFailureKind.AwaitingMerge)]
    [InlineData(409, HostMergeFailureKind.HeadMoved)]
    [InlineData(555, HostMergeFailureKind.Retry)]
    public void FR140_Bitbucket_merge_errors(int status, HostMergeFailureKind expected) => Assert.Equal(expected, BitbucketMergeResult.ClassifyMergeError(status).Kind);

    [Fact]
    public void FR140_Short_hashes_match_their_full_commit()
    {
        const string full = "abcdef0123456789abcdef0123456789abcdef01";
        Assert.True(BitbucketProjectRules.SameCommit("abcdef012345", full));
        Assert.True(BitbucketProjectRules.SameCommit(full, full.ToUpperInvariant()));
        Assert.False(BitbucketProjectRules.SameCommit("abc", full));
        Assert.False(BitbucketProjectRules.SameCommit(null, full));
        Assert.True(BitbucketProjectRules.IsFullHash(full));
        Assert.False(BitbucketProjectRules.IsFullHash("abcdef012345"));
    }

    [Fact]
    public void FR140_Tag_url_prefers_Bitbucket_link_else_the_repo_page()
    {
        Assert.Equal("https://bitbucket.org/acme/todo/commits/tag/v0.1.0", BitbucketProjectRules.TagUrl(null, "https://bitbucket.org/acme/todo", "v0.1.0"));
        Assert.Equal("https://bitbucket.org/x", BitbucketProjectRules.TagUrl("https://bitbucket.org/x", null, "v0.1.0"));
        Assert.Null(BitbucketProjectRules.TagUrl("javascript:alert(1)", null, "v0.1.0"));
    }
}
