using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;

namespace Aide.UnitTests.Releases;

/// <summary>FR-42, FR-43, D4.</summary>
public class ReleaseRulesTests
{
    private static readonly LifecycleState Releasing = new(Phase.Release, ProjectStatus.Releasing, 7);

    [Fact]
    public void FR42_The_tag_is_v_version_aide_and_only_such_names_are_release_tags()
    {
        Assert.Equal("v7-aide", ReleaseRules.Tag(7));
        Assert.Equal("v123-aide", ReleaseRules.Tag(123));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReleaseRules.Tag(0));

        Assert.True(ReleaseRules.IsReleaseTag("v7-aide"));
        Assert.False(ReleaseRules.IsReleaseTag("v07-aide"));
        Assert.False(ReleaseRules.IsReleaseTag("-v7-aide"));
        Assert.False(ReleaseRules.IsReleaseTag("v7-aide\n"));
        Assert.False(ReleaseRules.IsReleaseTag("refs/tags/v7-aide"));
        Assert.False(ReleaseRules.IsReleaseTag(null));
    }

    [Fact]
    public void FR42_Reused_versions_get_suffixed_tags_and_only_injection_safe_names_match()
    {
        Assert.Equal("v7-aide", ReleaseRules.Tag(7, 1));
        Assert.Equal("v7-aide.2", ReleaseRules.Tag(7, 2));
        Assert.Equal("v7-aide.9999", ReleaseRules.Tag(7, ReleaseRules.MaxTagSuffix));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReleaseRules.Tag(7, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReleaseRules.Tag(7, 10000));

        Assert.True(ReleaseRules.IsReleaseTag("v7-aide.2"));
        Assert.True(ReleaseRules.IsReleaseTag("v7-aide.10"));
        Assert.False(ReleaseRules.IsReleaseTag("v7-aide.1"));
        Assert.False(ReleaseRules.IsReleaseTag("v7-aide.02"));
        Assert.False(ReleaseRules.IsReleaseTag("v7-aide.10000"));
        Assert.False(ReleaseRules.IsReleaseTag("v7-aide."));
        Assert.False(ReleaseRules.IsReleaseTag("v7-aide.2.3"));
        Assert.False(ReleaseRules.IsReleaseTag("v7-aide.2 --force"));
        Assert.False(ReleaseRules.IsReleaseTag("v7-aide.2\n"));
        Assert.False(ReleaseRules.IsReleaseTag("v7-aide.2^{}"));

        Assert.True(ReleaseRules.TryParseTag("v12-aide.3", out var version, out var suffix));
        Assert.Equal((12, 3), (version, suffix));
        Assert.True(ReleaseRules.IsReleaseTagOf("v12-aide", 12));
        Assert.False(ReleaseRules.IsReleaseTagOf("v12-aide.2", 1));
    }

    [Fact]
    public void FR42_The_first_free_tag_is_used_and_a_tag_holding_the_release_is_reused()
    {
        // Nothing yet: the base tag.
        Assert.Equal(new ReleaseTagChoice("v6-aide", false), ReleaseRules.ChooseTag(6, []));

        // Re-release with new commits after a rollback: v6-aide is on another commit, never moved → v6-aide.2, then .3.
        Assert.Equal(new ReleaseTagChoice("v6-aide.2", false), ReleaseRules.ChooseTag(6, [new("v6-aide", false)]));
        Assert.Equal(new ReleaseTagChoice("v6-aide.3", false), ReleaseRules.ChooseTag(6, [new("v6-aide.2", false), new("v6-aide", false)]));

        // First free, gaps included; other versions' tags and foreign names are ignored.
        Assert.Equal(new ReleaseTagChoice("v6-aide.2", false), ReleaseRules.ChooseTag(6, [new("v6-aide", false), new("v6-aide.3", false), new("v7-aide.2", false), new("v6-aide-rc", false)]));
        Assert.Equal(new ReleaseTagChoice("v6-aide", false), ReleaseRules.ChooseTag(6, [new("v6-aide.2", false)]));

        // Re-release without new commits (or a redelivered job): the existing tag holding the head is the release (no-op).
        Assert.Equal(new ReleaseTagChoice("v6-aide", true), ReleaseRules.ChooseTag(6, [new("v6-aide", true)]));
        Assert.Equal(new ReleaseTagChoice("v6-aide.2", true), ReleaseRules.ChooseTag(6, [new("v6-aide.3", true), new("v6-aide", false), new("v6-aide.2", true)]));
        Assert.Equal(new ReleaseTagChoice("v6-aide.2", false), ReleaseRules.ChooseTag(6, [new("v7-aide", true), new("v6-aide", false)]));
    }

    [Fact]
    public void FR43_Attempts_count_per_version()
    {
        Assert.Equal(1, ReleaseRules.NextAttempt([]));
        Assert.Equal(3, ReleaseRules.NextAttempt([1, 2]));
        Assert.Equal(5, ReleaseRules.NextAttempt([4, 1]));
    }

    [Fact]
    public void FR42_The_job_runs_only_while_releasing_at_the_version_it_was_queued_for()
    {
        Assert.True(ReleaseRules.ShouldRun(Releasing, 7));
        Assert.False(ReleaseRules.ShouldRun(Releasing, 6));
        Assert.False(ReleaseRules.ShouldRun(new LifecycleState(Phase.Release, ProjectStatus.Released, 8), 7));
        Assert.False(ReleaseRules.ShouldRun(new LifecycleState(Phase.Review, ProjectStatus.RolledBack, 7), 7));
    }

    [Fact]
    public void FR43_A_retry_needs_release_releasing_and_a_failed_latest_attempt()
    {
        Assert.True(ReleaseRules.CanRetry(Releasing, ReleaseStatus.Failed, releaseJobActive: false).IsSuccess);
        Assert.True(ReleaseRules.CanRetry(Releasing, null, releaseJobActive: false).IsSuccess); // the job was lost: start one

        Assert.Equal(ErrorCodes.NotReleasing, ReleaseRules.CanRetry(new LifecycleState(Phase.Release, ProjectStatus.Released, 8), ReleaseStatus.Failed, false).Error!.Code);
        Assert.Equal(ErrorCodes.NotReleasing, ReleaseRules.CanRetry(new LifecycleState(Phase.Review, ProjectStatus.Reviewing, 6), null, false).Error!.Code);
        Assert.Equal(ErrorCodes.NotReleasing, ReleaseRules.CanRetry(Releasing, ReleaseStatus.Succeeded, false).Error!.Code);
        Assert.Equal(ErrorCodes.ReleaseInProgress, ReleaseRules.CanRetry(Releasing, ReleaseStatus.InProgress, false).Error!.Code);
        Assert.Equal(ErrorCodes.ReleaseInProgress, ReleaseRules.CanRetry(Releasing, ReleaseStatus.Failed, releaseJobActive: true).Error!.Code);
    }

    [Fact]
    public void FR42_Tag_message_and_merge_title_are_single_line()
    {
        Assert.Equal("[AIDE] Release v7-aide: Todo API", ReleaseRules.MergeTitle("Todo\r\nAPI", 7));
        Assert.Equal("AIDE release v7-aide: Todo API (project version 7)", ReleaseRules.TagMessage("Todo API", 7));
        Assert.Equal("[AIDE] Release v7-aide.2: Todo API", ReleaseRules.MergeTitle("Todo API", 7, "v7-aide.2"));
        Assert.Equal("AIDE release v7-aide.2: Todo API (project version 7)", ReleaseRules.TagMessage("Todo API", 7, "v7-aide.2"));
        Assert.Throws<ArgumentException>(() => ReleaseRules.TagMessage("Todo API", 7, "v6-aide"));
    }

    [Fact]
    public void D4_Released_is_set_only_by_release_completion_which_increments_the_version()
    {
        var completed = LifecycleStateMachine.Apply(Releasing, TransitionRequest.ReleaseCompleted(), LifecycleSnapshot.Empty);

        Assert.True(completed.IsSuccess);
        Assert.Equal((Phase.Release, ProjectStatus.Released, 8), (completed.Value.To, completed.Value.Status, completed.Value.Version));
        var again = LifecycleStateMachine.Apply(completed.Value.NewState, TransitionRequest.ReleaseCompleted(), LifecycleSnapshot.Empty);
        Assert.Equal(ErrorCodes.NotReleasing, again.Error!.Code);
    }
}
