using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;

namespace Aide.UnitTests.Releases;

/// <summary>FR-42 (amended by FR-64), FR-43, D4, D-V2-2.</summary>
public class ReleaseRulesTests
{
    private static readonly LifecycleState Releasing = new(Phase.Release, ProjectStatus.Releasing, 7);

    [Theory]
    [InlineData("v1.2.0")]
    [InlineData("1.2.0")]
    [InlineData("release/1.2.0-rc.1")]
    [InlineData("v2.0.0-beta.1")]
    [InlineData("v7-aide")] // v1 tags stay valid names
    [InlineData("v7-aide.2")]
    public void FR64_Safe_tag_names_are_release_tags(string tag) => Assert.True(ReleaseRules.IsReleaseTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-v1.2.0")]
    [InlineData(".v1.2.0")]
    [InlineData("/v1.2.0")]
    [InlineData("v1.2.0\n")]
    [InlineData("v1.2.0 --force")]
    [InlineData("v1..2.0")]
    [InlineData("a//1.2.0")]
    [InlineData("a/.b1.2.0")]
    [InlineData("v1.2.0^{}")]
    [InlineData("v1.2.0.")]
    [InlineData("v1.2.0/")]
    [InlineData("v1.2.0.lock")]
    [InlineData("v1.2.0@{1}")]
    public void FR64_Unsafe_names_are_not_release_tags(string? tag) => Assert.False(ReleaseRules.IsReleaseTag(tag));

    [Fact]
    public void FR42_amended_by_FR64_A_missing_tag_is_created_a_tag_holding_the_release_is_reused_another_is_TAG_EXISTS()
    {
        Assert.False(ReleaseRules.DecideTag("v1.2.0", null).Value);
        Assert.True(ReleaseRules.DecideTag("v1.2.0", new ExistingReleaseTag("v1.2.0", ContainsRelease: true)).Value);

        // The v1 suffix scheme (v<N>-aide.<k>) is gone: a tag on another commit fails the release; nothing is merged.
        var taken = ReleaseRules.DecideTag("v1.2.0", new ExistingReleaseTag("v1.2.0", ContainsRelease: false));
        Assert.Equal(ErrorCodes.TagExists, taken.Error!.Code);
    }

    [Fact]
    public void FR43_Attempts_count_per_checkpoint()
    {
        Assert.Equal(1, ReleaseRules.NextAttempt([]));
        Assert.Equal(3, ReleaseRules.NextAttempt([1, 2]));
        Assert.Equal(5, ReleaseRules.NextAttempt([4, 1]));
    }

    [Fact]
    public void FR42_The_job_runs_only_while_releasing_at_the_checkpoint_it_was_queued_for()
    {
        Assert.True(ReleaseRules.ShouldRun(Releasing, 7));
        Assert.False(ReleaseRules.ShouldRun(Releasing, 6));
        Assert.False(ReleaseRules.ShouldRun(new LifecycleState(Phase.Release, ProjectStatus.Released, 7), 7));
        Assert.False(ReleaseRules.ShouldRun(new LifecycleState(Phase.Review, ProjectStatus.RolledBack, 7), 7));
    }

    [Fact]
    public void FR43_A_retry_needs_release_releasing_and_a_failed_latest_attempt()
    {
        Assert.True(ReleaseRules.CanRetry(Releasing, ReleaseStatus.Failed, releaseJobActive: false).IsSuccess);
        Assert.True(ReleaseRules.CanRetry(Releasing, null, releaseJobActive: false).IsSuccess); // the job was lost: start one

        Assert.Equal(ErrorCodes.NotReleasing, ReleaseRules.CanRetry(new LifecycleState(Phase.Release, ProjectStatus.Released, 7), ReleaseStatus.Failed, false).Error!.Code);
        Assert.Equal(ErrorCodes.NotReleasing, ReleaseRules.CanRetry(new LifecycleState(Phase.Review, ProjectStatus.Reviewing, 6), null, false).Error!.Code);
        Assert.Equal(ErrorCodes.NotReleasing, ReleaseRules.CanRetry(Releasing, ReleaseStatus.Succeeded, false).Error!.Code);
        Assert.Equal(ErrorCodes.ReleaseInProgress, ReleaseRules.CanRetry(Releasing, ReleaseStatus.InProgress, false).Error!.Code);
        Assert.Equal(ErrorCodes.ReleaseInProgress, ReleaseRules.CanRetry(Releasing, ReleaseStatus.Failed, releaseJobActive: true).Error!.Code);
    }

    [Fact]
    public void FR64_Tag_message_and_merge_title_name_the_semver_tag_on_one_line()
    {
        Assert.Equal("[AIDE] Release v1.2.0: Todo API", ReleaseRules.MergeTitle("Todo\r\nAPI", "v1.2.0"));
        Assert.Equal("AIDE release v1.2.0: Todo API (version 1.2.0)", ReleaseRules.TagMessage("Todo API", "v1.2.0", "1.2.0"));
        Assert.Throws<ArgumentException>(() => ReleaseRules.TagMessage("Todo API", "-x", "1.2.0"));
    }

    [Fact]
    public void D4_amended_by_S4_10_Released_is_set_only_by_release_completion_which_keeps_the_checkpoint()
    {
        var completed = LifecycleStateMachine.Apply(Releasing, TransitionRequest.ReleaseCompleted(), LifecycleSnapshot.Empty);

        Assert.True(completed.IsSuccess);
        Assert.Equal((Phase.Release, ProjectStatus.Released, 7), (completed.Value.To, completed.Value.Status, completed.Value.Version));
        var again = LifecycleStateMachine.Apply(completed.Value.NewState, TransitionRequest.ReleaseCompleted(), LifecycleSnapshot.Empty);
        Assert.Equal(ErrorCodes.NotReleasing, again.Error!.Code);
    }

    [Fact]
    public void FR140_A_submitted_merge_still_pending_is_waited_for_up_to_an_hour_then_handed_to_the_merge_poll()
    {
        var submitted = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
        Assert.True(ReleaseRules.KeepWaitingForSubmittedMerge(null, submitted));
        Assert.True(ReleaseRules.KeepWaitingForSubmittedMerge(submitted, submitted.AddMinutes(59)));
        Assert.False(ReleaseRules.KeepWaitingForSubmittedMerge(submitted, submitted.AddHours(1)));
        Assert.False(ReleaseRules.KeepWaitingForSubmittedMerge(submitted, submitted.AddHours(3)));
    }
}
