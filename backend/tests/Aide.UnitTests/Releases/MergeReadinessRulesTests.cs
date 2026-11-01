using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Domain.Projects;
using Aide.Domain.Releases;

namespace Aide.UnitTests.Releases;

/// <summary>FR-64, FR-65, FR-84 (§4.10 tag prefix): merge readiness, auto-merge and the release settings rules.</summary>
public class MergeReadinessRulesTests
{
    public static TheoryData<Phase> AllPhases() => [.. PhaseOrder.All];

    [Theory]
    [MemberData(nameof(AllPhases))]
    public void FR84_Merge_readiness_is_checked_only_in_review_and_release(Phase phase)
    {
        var result = MergeReadinessRules.CanCheck(phase);

        if (phase is Phase.Review or Phase.Release)
        {
            Assert.True(result.IsSuccess);
        }
        else
        {
            Assert.Equal(ErrorCodes.WrongPhase, result.Error!.Code);
        }
    }

    [Theory]
    [InlineData(MergeStrategy.AideMerges, true, true)]
    [InlineData(MergeStrategy.AideMerges, false, false)]
    [InlineData(MergeStrategy.Manual, true, false)]
    [InlineData(MergeStrategy.Manual, false, false)]
    public void FR65_AIDE_merges_automatically_only_under_aide_merges_when_the_PR_can_merge(MergeStrategy strategy, bool canMerge, bool expected) =>
        Assert.Equal(expected, MergeReadinessRules.WillAutoMerge(strategy, canMerge));

    [Fact]
    public void FR84_FR138_Reason_check_and_conclusion_wire_values_match_the_spec()
    {
        Assert.Equal(
            ["review_required", "changes_requested", "checks_failing", "checks_pending", "branch_protected", "merge_conflict", "no_permission", "merge_method_not_allowed", "pr_not_open", "manual_strategy", "draft", "discussions_unresolved", "rebase_required"],
            EnumStrings.Values<MergeReadinessReasonCode>());
        Assert.Equal(["queued", "in_progress", "completed"], EnumStrings.Values<CheckRunStatus>());
        Assert.Equal(["success", "failure", "neutral", "cancelled", "skipped", "timed_out", "action_required"], EnumStrings.Values<CheckRunConclusion>());
        Assert.Equal("awaiting_merge", ProjectStatus.AwaitingMerge.ToWire());
        Assert.Equal("awaiting_merge", ReleaseStatus.AwaitingMerge.ToWire());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("release/")]
    [InlineData("rel-1.x_")]
    [InlineData("abcdefghijklmnopqrst")] // 20
    public void FR64_Valid_tag_prefixes_are_accepted(string? prefix) =>
        Assert.True(ProjectSettingsRules.ValidateTagPrefix(prefix).IsSuccess);

    [Theory]
    [InlineData("abcdefghijklmnopqrstu")] // 21
    [InlineData("-v")]
    [InlineData(".v")]
    [InlineData("/v")]
    [InlineData("v 1")]
    [InlineData("a..b")]
    [InlineData("a//b")]
    [InlineData("v@")]
    public void FR64_Invalid_tag_prefixes_are_VALIDATION(string prefix)
    {
        var result = ProjectSettingsRules.ValidateTagPrefix(prefix);

        Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        Assert.StartsWith("tagPrefix:", Assert.Single(result.Error.Details), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(ReleaseVersionStatus.InDevelopment, false, true)]
    [InlineData(ReleaseVersionStatus.InDevelopment, true, false)]
    [InlineData(ReleaseVersionStatus.AwaitingMerge, false, false)]
    [InlineData(null, true, false)]
    public void FR65_The_tag_prefix_is_fixed_while_a_release_is_in_progress_or_awaiting_merge(
        ReleaseVersionStatus? development, bool releaseInProgress, bool allowed)
    {
        var result = ProjectSettingsRules.CanChangeTagPrefix(development, releaseInProgress);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal(ErrorCodes.Conflict, result.Error!.Code);
        }
    }
}
