using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Releases;
using Aide.Domain.Review;

namespace Aide.UnitTests.Execution;

/// <summary>§4.47, FR-254, FR-255, FR-258: the build check's state rules (every table row) and attempt limits.</summary>
public sealed class BuildCheckRulesTests
{
    private const string Head = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private const string Older = "0f0e0d0c0b0a09080706050403020100ffeeddcc";

    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly BuildCheckPreviewComponent Vercel = new(DeploymentComponent.Frontend, DeploymentProvider.Vercel, DeploymentStatus.Building, "https://p.vercel.app");

    private static BuildCheckPreview Rollout(string? sha, RolloutState? state, DeploymentStatus? status = null) =>
        new(sha, state, [Vercel with { Status = status ?? Vercel.Status }]);

    private static CheckObservation Check(string name, CheckRunStatus status, CheckRunConclusion? conclusion = null, bool required = true) =>
        new(name, status, conclusion, required, $"https://ci/{name}");

    private static BuildCheckChecks Checks(string? sha, params CheckObservation[] checks) => new(sha, checks);

    private static BuildCheckDecision Decide(BuildCheckObservation observation, int attempt = 0, int max = 5, DateTimeOffset? now = null) =>
        BuildCheckRules.Decide(observation, attempt, max, now ?? T0);

    // ---------- §4.47 table: — → waiting (FR-254, FR-258) ----------

    [Theory]
    [InlineData(5, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void FR258_A_build_check_starts_only_when_max_build_fix_attempts_is_above_0(int max, bool starts) =>
        Assert.Equal(starts, BuildCheckRules.ShouldStart(max));

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(-1, false)]
    public void FR258_Max_build_fix_attempts_must_be_0_to_10(int value, bool valid) =>
        Assert.Equal(valid, BuildCheckRules.IsValidMaxAttempts(value));

    [Fact]
    public void FR254_The_first_check_of_a_cycle_is_attempt_0() =>
        Assert.Equal(0, BuildCheckRules.NextAttempt([], 1));

    [Fact]
    public void FR258_The_attempt_count_starts_again_at_0_in_the_next_review_cycle()
    {
        var checks = new[]
        {
            new BuildCheck { ReviewCycle = 1, Attempt = 0 },
            new BuildCheck { ReviewCycle = 1, Attempt = 1 },
            new BuildCheck { ReviewCycle = 1, Attempt = 2 },
        };

        Assert.Equal(3, BuildCheckRules.NextAttempt(checks, 1));
        Assert.Equal(0, BuildCheckRules.NextAttempt(checks, 2));
    }

    // ---------- waiting → skipped ----------

    [Fact]
    public void FR255_No_deploy_target_and_no_checks_host_is_skipped()
    {
        var decision = Decide(new BuildCheckObservation(Head, null, null));

        Assert.Equal(BuildCheckStatus.Skipped, decision.Status);
        Assert.True(decision.ToReview);
        Assert.Empty(decision.Sources);
    }

    [Fact]
    public void FR255_No_deploy_target_and_no_required_checks_on_the_head_is_skipped()
    {
        var decision = Decide(new BuildCheckObservation(Head, null, Checks(Head, Check("lint", CheckRunStatus.InProgress, required: false))));

        Assert.Equal(BuildCheckStatus.Skipped, decision.Status);
    }

    // ---------- waiting → passed ----------

    [Fact]
    public void FR255_The_head_rollout_succeeded_and_required_check_build_passed_so_the_check_passes()
    {
        var observation = new BuildCheckObservation(
            Head,
            Rollout(Head, RolloutState.Succeeded, DeploymentStatus.Ready),
            Checks(Head, Check("build", CheckRunStatus.Completed, CheckRunConclusion.Success), Check("optional", CheckRunStatus.Completed, CheckRunConclusion.Failure, required: false)));

        var decision = Decide(observation);

        Assert.Equal(BuildCheckStatus.Passed, decision.Status);
        Assert.True(decision.ToReview);
        Assert.Empty(decision.TimedOut);
        Assert.Equal(
            [
                new BuildCheckSource("Vercel preview", BuildCheckSourceKind.Preview, "success", "https://p.vercel.app"),
                new BuildCheckSource("build", BuildCheckSourceKind.Check, "success", "https://ci/build"),
            ],
            decision.Sources);
    }

    [Fact]
    public void FR255_A_short_head_sha_from_the_host_is_the_same_commit()
    {
        Assert.True(BuildCheckRules.SameSha(Head, Head[..7]));
        Assert.True(BuildCheckRules.SameSha(Head.ToUpperInvariant(), Head));
        Assert.False(BuildCheckRules.SameSha(Head, Head[..6]));
        Assert.False(BuildCheckRules.SameSha(Head, Older));
        Assert.False(BuildCheckRules.SameSha(null, Head));
    }

    // ---------- older SHA ignored (FR-255) ----------

    [Fact]
    public void FR255_A_rollout_that_finished_for_an_older_sha_keeps_the_check_waiting_for_the_head_rollout()
    {
        var decision = Decide(new BuildCheckObservation(Head, Rollout(Older, RolloutState.Succeeded, DeploymentStatus.Ready), null));

        Assert.Equal(BuildCheckStatus.Waiting, decision.Status);
        Assert.Equal(T0 + PreviewReviewRules.RecheckInterval, decision.RecheckAt);
        Assert.Equal(T0, decision.PreviewWaitSince);
        var source = Assert.Single(decision.Sources);
        Assert.Null(source.Conclusion);
        Assert.Null(source.Url);
    }

    [Fact]
    public void FR255_A_failed_rollout_of_an_older_sha_is_ignored_too()
    {
        var decision = Decide(new BuildCheckObservation(Head, Rollout(Older, RolloutState.Failed, DeploymentStatus.Error), null));

        Assert.Equal(BuildCheckStatus.Waiting, decision.Status);
    }

    [Fact]
    public void FR255_Checks_read_for_an_older_sha_keep_the_check_waiting_every_30_seconds()
    {
        var decision = Decide(new BuildCheckObservation(Head, null, Checks(Older, Check("build", CheckRunStatus.Completed, CheckRunConclusion.Failure))));

        Assert.Equal(BuildCheckStatus.Waiting, decision.Status);
        Assert.Equal(T0 + ExternalCheckRules.RecheckInterval, decision.RecheckAt);
        Assert.Equal(T0, decision.ChecksWaitSince);
        Assert.Empty(decision.Sources);
    }

    [Fact]
    public void S4_47_A_running_rollout_and_a_pending_check_recheck_at_the_earlier_interval_and_keep_both_wait_starts()
    {
        var observation = new BuildCheckObservation(Head, Rollout(Head, RolloutState.Running), Checks(Head, Check("build", CheckRunStatus.InProgress)))
        {
            PreviewWaitSince = T0.AddMinutes(-5),
            ChecksWaitSince = T0.AddMinutes(-2),
        };

        var decision = Decide(observation);

        Assert.True(decision.Wait);
        Assert.Equal(T0 + PreviewReviewRules.RecheckInterval, decision.RecheckAt);
        Assert.Equal((T0.AddMinutes(-5), T0.AddMinutes(-2)), (decision.PreviewWaitSince!.Value, decision.ChecksWaitSince!.Value));
        Assert.Equal([null, null], decision.Sources.Select(s => s.Conclusion));
    }

    [Fact]
    public void S4_47_A_failed_preview_still_waits_for_the_pending_required_check_so_the_fix_sees_every_failure()
    {
        var observation = new BuildCheckObservation(Head, Rollout(Head, RolloutState.Failed, DeploymentStatus.Error), Checks(Head, Check("build", CheckRunStatus.InProgress)));

        var decision = Decide(observation);

        Assert.Equal(BuildCheckStatus.Waiting, decision.Status);
        Assert.Equal(T0 + ExternalCheckRules.RecheckInterval, decision.RecheckAt);
    }

    // ---------- waiting → failed / exhausted ----------

    [Fact]
    public void S4_47_A_failed_head_rollout_with_fixes_left_is_failed_and_plans_the_next_fix()
    {
        var decision = Decide(new BuildCheckObservation(Head, Rollout(Head, RolloutState.Failed, DeploymentStatus.Error), null), attempt: 0, max: 5);

        Assert.Equal(BuildCheckStatus.Failed, decision.Status);
        Assert.True(decision.PlanFix);
        Assert.False(decision.ToReview);
        Assert.Equal(["Vercel preview"], decision.Failing); // FR-259: one component, no suffix
        Assert.Equal("failure", Assert.Single(decision.Sources).Conclusion);
    }

    [Fact]
    public void S4_47_A_failed_required_check_with_fixes_left_is_failed()
    {
        var decision = Decide(
            new BuildCheckObservation(Head, Rollout(Head, RolloutState.Succeeded, DeploymentStatus.Ready), Checks(Head, Check("build", CheckRunStatus.Completed, CheckRunConclusion.Failure))),
            attempt: 2,
            max: 5);

        Assert.Equal(BuildCheckStatus.Failed, decision.Status);
        Assert.Equal(["build"], decision.Failing);
        Assert.Contains(new BuildCheckSource("build", BuildCheckSourceKind.Check, "failure", "https://ci/build"), decision.Sources);
    }

    [Fact]
    public void S4_47_A_failure_when_fixes_used_equals_max_is_exhausted_and_goes_to_review()
    {
        var decision = Decide(new BuildCheckObservation(Head, Rollout(Head, RolloutState.Failed, DeploymentStatus.Error), null), attempt: 5, max: 5);

        Assert.Equal(BuildCheckStatus.Exhausted, decision.Status);
        Assert.True(decision.ToReview);
    }

    [Fact]
    public void FR258_With_max_2_and_three_failing_checks_two_fixes_run_and_the_third_check_is_exhausted()
    {
        var failing = new BuildCheckObservation(Head, Rollout(Head, RolloutState.Failed, DeploymentStatus.Error), null);

        var statuses = Enumerable.Range(0, 3).Select(attempt => Decide(failing, attempt, max: 2).Status).ToList();

        Assert.Equal([BuildCheckStatus.Failed, BuildCheckStatus.Failed, BuildCheckStatus.Exhausted], statuses);
    }

    [Fact]
    public void S4_47_Worked_example_m5_attempt_0_fails_attempt_1_fails_attempt_2_passes_3_checks_2_fixes()
    {
        var failed = new BuildCheckObservation(Head, Rollout(Head, RolloutState.Failed, DeploymentStatus.Error), null);
        var passed = new BuildCheckObservation(Head, Rollout(Head, RolloutState.Succeeded, DeploymentStatus.Ready), null);

        Assert.Equal(
            [BuildCheckStatus.Failed, BuildCheckStatus.Failed, BuildCheckStatus.Passed],
            [Decide(failed, 0).Status, Decide(failed, 1).Status, Decide(passed, 2).Status]);
    }

    [Fact]
    public void S4_47_A_blocked_preview_rollout_counts_as_failed() =>
        Assert.Equal(BuildCheckStatus.Failed, Decide(new BuildCheckObservation(Head, Rollout(Head, RolloutState.Blocked), null)).Status);

    // ---------- timeouts (FR-255) ----------

    [Fact]
    public void FR255_The_rollout_wait_timing_out_after_30_minutes_fails_with_the_failure_preview_platform_finding()
    {
        var observation = new BuildCheckObservation(Head, Rollout(Head, RolloutState.Running), null) { PreviewWaitSince = T0 };

        var waiting = Decide(observation, now: T0 + PreviewReviewRules.WaitLimit - TimeSpan.FromSeconds(1));
        var decision = Decide(observation, now: T0 + PreviewReviewRules.WaitLimit);

        Assert.Equal(BuildCheckStatus.Waiting, waiting.Status);
        Assert.Equal(BuildCheckStatus.Failed, decision.Status);
        var failure = Assert.Single(decision.TimedOut);
        Assert.Equal("failure:preview:vercel", failure.FailureId);
        Assert.Equal("failure:preview:vercel", failure.Fingerprint);
        Assert.Equal("Vercel preview didn't finish within 30 minutes", failure.Message);
        Assert.Equal(FailureIds.Preview, failure.Source);
        Assert.Equal(BuildCheckRules.TimedOutConclusion, Assert.Single(decision.Sources).Conclusion);
    }

    [Fact]
    public void FR255_A_preview_rollout_never_queued_for_the_head_times_out_the_same_way()
    {
        var decision = Decide(new BuildCheckObservation(Head, Rollout(null, null), null) { PreviewWaitSince = T0 }, now: T0.AddMinutes(30));

        Assert.Equal("failure:preview:vercel", Assert.Single(decision.TimedOut).FailureId);
    }

    [Fact]
    public void S4_47_Two_components_on_one_platform_time_out_with_component_qualified_ids()
    {
        var preview = new BuildCheckPreview(Head, RolloutState.Running,
        [
            new BuildCheckPreviewComponent(DeploymentComponent.Backend, DeploymentProvider.Railway, DeploymentStatus.Ready),
            new BuildCheckPreviewComponent(DeploymentComponent.Frontend, DeploymentProvider.Railway, DeploymentStatus.Building),
        ]);

        var decision = Decide(new BuildCheckObservation(Head, preview, null) { PreviewWaitSince = T0 }, now: T0.AddMinutes(31));

        Assert.Equal(["failure:preview:railway-frontend"], decision.TimedOut.Select(f => f.FailureId));
        Assert.Equal(["Railway preview (backend)", "Railway preview (frontend)"], decision.Sources.Select(s => s.Name)); // FR-259: two components keep the suffix
        Assert.Equal(["success", BuildCheckRules.TimedOutConclusion], decision.Sources.Select(s => s.Conclusion));
    }

    [Fact]
    public void FR255_A_required_check_pending_for_15_minutes_fails_with_a_failure_check_finding()
    {
        var observation = new BuildCheckObservation(Head, null, Checks(Head, Check("ci / build", CheckRunStatus.InProgress))) { ChecksWaitSince = T0 };

        var waiting = Decide(observation, now: T0 + ExternalCheckRules.WaitLimit - TimeSpan.FromSeconds(1));
        var decision = Decide(observation, now: T0 + ExternalCheckRules.WaitLimit);

        Assert.True(waiting.Wait);
        Assert.Equal(BuildCheckStatus.Failed, decision.Status);
        var failure = Assert.Single(decision.TimedOut);
        Assert.Equal("failure:check:ci-build", failure.FailureId);
        Assert.Equal("required check ci / build didn't finish within 15 minutes", failure.Message);
        Assert.Equal(BuildCheckRules.TimedOutConclusion, Assert.Single(decision.Sources).Conclusion);
    }

    // ---------- any → exhausted / cancelled ----------

    [Theory]
    [InlineData(BuildCheckStatus.Waiting, BuildCheckStatus.Exhausted)]
    [InlineData(BuildCheckStatus.Failed, BuildCheckStatus.Exhausted)]
    [InlineData(BuildCheckStatus.Passed, BuildCheckStatus.Passed)]
    [InlineData(BuildCheckStatus.Skipped, BuildCheckStatus.Skipped)]
    [InlineData(BuildCheckStatus.Exhausted, BuildCheckStatus.Exhausted)]
    [InlineData(BuildCheckStatus.Cancelled, BuildCheckStatus.Cancelled)]
    public void FR258_Invalid_fix_planning_after_one_retry_or_a_blocked_fix_step_exhausts_an_open_check(BuildCheckStatus from, BuildCheckStatus expected) =>
        Assert.Equal(expected, BuildCheckRules.Exhaust(from));

    [Theory]
    [InlineData(BuildCheckStatus.Waiting, BuildCheckStatus.Cancelled)]
    [InlineData(BuildCheckStatus.Failed, BuildCheckStatus.Cancelled)]
    [InlineData(BuildCheckStatus.Passed, BuildCheckStatus.Passed)]
    [InlineData(BuildCheckStatus.Skipped, BuildCheckStatus.Skipped)]
    [InlineData(BuildCheckStatus.Exhausted, BuildCheckStatus.Exhausted)]
    [InlineData(BuildCheckStatus.Cancelled, BuildCheckStatus.Cancelled)]
    public void S4_47_Invalidation_rollback_or_stopping_execution_cancels_an_open_check(BuildCheckStatus from, BuildCheckStatus expected) =>
        Assert.Equal(expected, BuildCheckRules.Cancel(from));

    [Fact]
    public void FR258_Fix_planning_is_retried_once_then_the_check_is_exhausted()
    {
        Assert.True(BuildCheckRules.RetryFixPlanning(0));
        Assert.False(BuildCheckRules.RetryFixPlanning(1));
    }

    [Fact]
    public void S4_47_Every_transition_from_each_state_is_allowed_or_forbidden_as_the_table_says()
    {
        var all = Enum.GetValues<BuildCheckStatus>();
        foreach (var from in all)
        {
            foreach (var to in all)
            {
                var expected = from switch
                {
                    BuildCheckStatus.Waiting => true,
                    BuildCheckStatus.Failed => to is BuildCheckStatus.Exhausted or BuildCheckStatus.Cancelled,
                    _ => false,
                };
                Assert.True(expected == BuildCheckRules.CanTransition(from, to), $"{from} → {to}");
            }
        }

        Assert.All(all.Where(BuildCheckRules.IsFinal), s => Assert.DoesNotContain(all, to => BuildCheckRules.CanTransition(s, to)));
        Assert.Equal([BuildCheckStatus.Passed, BuildCheckStatus.Skipped, BuildCheckStatus.Exhausted], all.Where(BuildCheckRules.GoesToReview));
    }

    // ---------- checks in force: rollback and invalidation (§4.47) ----------

    [Fact]
    public void FR258_After_a_rollback_supersedes_the_cycles_checks_the_next_attempt_is_0_again()
    {
        var checks = new[]
        {
            new BuildCheck { ReviewCycle = 0, Attempt = 0, Status = BuildCheckStatus.Failed, SupersededAt = T0 },
            new BuildCheck { ReviewCycle = 0, Attempt = 1, Status = BuildCheckStatus.Exhausted, SupersededAt = T0 },
        };

        Assert.Equal(0, BuildCheckRules.NextAttempt(checks, 0));
    }

    [Fact]
    public void FR258_Fixes_used_count_only_checks_in_force_with_fix_steps_and_never_cancelled_ones()
    {
        var current = new BuildCheck { ReviewCycle = 0, Attempt = 3 };
        var fixedInForce = new BuildCheck { ReviewCycle = 0, Attempt = 1, Status = BuildCheckStatus.Failed };
        var superseded = new BuildCheck { ReviewCycle = 0, Attempt = 0, Status = BuildCheckStatus.Failed, SupersededAt = T0 };
        var cancelled = new BuildCheck { ReviewCycle = 0, Attempt = 2, Status = BuildCheckStatus.Cancelled };
        var otherCycle = new BuildCheck { ReviewCycle = 1, Attempt = 0, Status = BuildCheckStatus.Failed };
        var all = new[] { current, fixedInForce, superseded, cancelled, otherCycle };
        var withFixSteps = new HashSet<Guid>(all.Select(c => c.Id));

        Assert.Equal(1, BuildCheckRules.FixesUsed(all, 0, current.Id, withFixSteps));
        Assert.Equal(0, BuildCheckRules.FixesUsed(all, 0, current.Id, new HashSet<Guid>()));
    }

    [Fact]
    public void FR255_Checks_never_reported_for_the_head_within_15_minutes_fail_with_a_check_timeout_failure()
    {
        var observation = new BuildCheckObservation(Head, null, Checks(Older, Check("build", CheckRunStatus.Completed, CheckRunConclusion.Success)))
        {
            ChecksWaitSince = T0 - ExternalCheckRules.WaitLimit - TimeSpan.FromSeconds(1),
        };

        var decision = Decide(observation);

        Assert.Equal(BuildCheckStatus.Failed, decision.Status);
        var failure = Assert.Single(decision.TimedOut);
        Assert.Equal("failure:check:required-checks", failure.FailureId);
        Assert.Equal(BuildCheckRules.HeadChecksTimedOutMessage, failure.Message);
        Assert.Equal(BuildCheckRules.TimedOutConclusion, Assert.Single(decision.Sources).Conclusion);
    }

    [Fact]
    public void FR255_A_required_check_not_yet_reported_for_the_head_keeps_the_check_waiting_and_fails_at_15_minutes()
    {
        // Branch protection requires build; GitHub has reported only lint for the head so far.
        var marked = MergeReadinessRules.MarkRequired(
            [Check("lint", CheckRunStatus.Completed, CheckRunConclusion.Success, required: false)], ["build"], includeMissingRequired: true);

        var waiting = Decide(new BuildCheckObservation(Head, null, new BuildCheckChecks(Head, marked)));
        Assert.Equal(BuildCheckStatus.Waiting, waiting.Status);

        var timedOut = Decide(new BuildCheckObservation(Head, null, new BuildCheckChecks(Head, marked))
        {
            ChecksWaitSince = T0 - ExternalCheckRules.WaitLimit - TimeSpan.FromSeconds(1),
        });
        Assert.Equal(BuildCheckStatus.Failed, timedOut.Status);
        Assert.Equal("failure:check:build", Assert.Single(timedOut.TimedOut).FailureId);
    }
}
