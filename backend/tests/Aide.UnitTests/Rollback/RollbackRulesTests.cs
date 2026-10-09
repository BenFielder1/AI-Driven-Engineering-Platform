using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Lifecycle;
using Aide.Domain.Rollback;

namespace Aide.UnitTests.Rollback;

/// <summary>§4.8, FR-44, FR-45, D5.</summary>
public class RollbackRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A history matching FR-44: v1 refinement … v4 planning … v7 review.</summary>
    private static readonly RecordedTransition[] History =
    [
        new(2, Phase.Specification, T0.AddMinutes(1)),
        new(3, Phase.Planning, T0.AddMinutes(2)),
        new(3, Phase.Specification, T0.AddMinutes(3)),
        new(4, Phase.Planning, T0.AddMinutes(4)),
        new(5, Phase.Execution, T0.AddMinutes(5)),
        new(6, Phase.Review, T0.AddMinutes(6)),
        new(6, Phase.Execution, T0.AddMinutes(7)),
        new(7, Phase.Review, T0.AddMinutes(8)),
    ];

    private static RollbackRequest Request(
        int target = 4,
        IReadOnlyList<ArtifactRef>? artifacts = null,
        IReadOnlyList<StepRef>? steps = null,
        bool runInProgress = false,
        DateTimeOffset? lastRollbackAt = null) =>
        new(
            new LifecycleState(Phase.Review, ProjectStatus.Reviewing, 7),
            target,
            "bad direction",
            History,
            artifacts ?? [],
            steps ?? [],
            runInProgress,
            T0.AddHours(1),
            lastRollbackAt);

    [Fact]
    public void FR44_Version7_in_review_rolled_back_to_version4_restores_planning_and_invalidates_v5_to_v7()
    {
        var request4 = new ArtifactRef(Guid.NewGuid(), ArtifactType.ProjectRequest, ArtifactStatus.Locked, 1);
        var plan4 = new ArtifactRef(Guid.NewGuid(), ArtifactType.ImplementationPlan, ArtifactStatus.Locked, 4);
        var optimisation6 = new ArtifactRef(Guid.NewGuid(), ArtifactType.OptimizationPlan, ArtifactStatus.Locked, 6);
        var spec5 = new ArtifactRef(Guid.NewGuid(), ArtifactType.TechnicalSpecification, ArtifactStatus.Draft, 5);
        var optimisation7 = new ArtifactRef(Guid.NewGuid(), ArtifactType.OptimizationPlan, ArtifactStatus.Draft, 7);
        var rejected7 = new ArtifactRef(Guid.NewGuid(), ArtifactType.OptimizationPlan, ArtifactStatus.Rejected, 7);
        var planStep = new StepRef(Guid.NewGuid(), plan4.Id, StepStatus.Completed);
        var optimisationStep = new StepRef(Guid.NewGuid(), optimisation6.Id, StepStatus.Completed);

        var result = RollbackRules.Plan(Request(
            artifacts: [request4, plan4, optimisation6, spec5, optimisation7, rejected7],
            steps: [planStep, optimisationStep]));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var plan = result.Value;
        Assert.Equal(Phase.Planning, plan.Transition.To);
        Assert.Equal(ProjectStatus.RolledBack, plan.Transition.Status);
        Assert.Equal(4, plan.Transition.Version);
        Assert.Equal((7, 4), (plan.FromVersion, plan.TargetVersion));
        Assert.Equal([optimisation6.Id, spec5.Id, optimisation7.Id], plan.InvalidatedArtifactIds);
        Assert.Equal([optimisationStep.Id], plan.ResetStepIds);
        Assert.Equal("bad direction", plan.Transition.Reason);
        Assert.Equal(0, plan.ReviewCycle);
    }

    [Fact]
    public void S4_8_Rollback_keeping_the_plan_requeues_its_blocked_and_rejected_steps_but_not_completed_ones()
    {
        var plan4 = new ArtifactRef(Guid.NewGuid(), ArtifactType.ImplementationPlan, ArtifactStatus.Locked, 4);
        var completed = new StepRef(Guid.NewGuid(), plan4.Id, StepStatus.Completed);
        var blocked = new StepRef(Guid.NewGuid(), plan4.Id, StepStatus.Blocked);
        var rejected = new StepRef(Guid.NewGuid(), plan4.Id, StepStatus.Rejected);
        var pending = new StepRef(Guid.NewGuid(), plan4.Id, StepStatus.Pending);
        var optimisation6 = new ArtifactRef(Guid.NewGuid(), ArtifactType.OptimizationPlan, ArtifactStatus.Locked, 6);
        var blockedOptimisation = new StepRef(Guid.NewGuid(), optimisation6.Id, StepStatus.Blocked);

        var plan = RollbackRules.Plan(Request(artifacts: [plan4, optimisation6], steps: [completed, blocked, rejected, pending, blockedOptimisation])).Value;

        Assert.Equal([blocked.Id, rejected.Id], plan.RequeuedStepIds);
        Assert.Equal([blockedOptimisation.Id], plan.ResetStepIds);
    }

    [Fact]
    public void FR44_Rollback_into_a_release_is_invalid_target_version()
    {
        RecordedTransition[] history = [.. History, new(7, Phase.Release, T0.AddMinutes(9)), new(8, Phase.Release, T0.AddMinutes(10))];
        var request = Request(7) with { Current = new LifecycleState(Phase.Release, ProjectStatus.Released, 8), Transitions = history };

        var result = RollbackRules.Plan(request);

        Assert.Equal(ErrorCodes.InvalidTargetVersion, result.Error!.Code);
        Assert.Contains("release", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR44_Released_project_can_roll_back_to_review()
    {
        RecordedTransition[] history = [.. History, new(8, Phase.Release, T0.AddMinutes(9)), new(9, Phase.Release, T0.AddMinutes(10))];
        var request = Request(7) with { Current = new LifecycleState(Phase.Release, ProjectStatus.Released, 9), Transitions = history };

        Assert.Equal(Phase.Review, RollbackRules.Plan(request).Value.Transition.To);
    }

    [Fact]
    public void D5_Phase_is_restored_from_the_latest_transition_at_the_target_version()
    {
        Assert.Equal(Phase.Specification, RollbackRules.RestoredPhase(History, 3));
        Assert.Equal(Phase.Execution, RollbackRules.RestoredPhase(History, 6));
    }

    [Fact]
    public void D5_Version1_without_a_transition_is_refinement() =>
        Assert.Equal(Phase.Refinement, RollbackRules.RestoredPhase(History, 1));

    [Fact]
    public void Rollback_within_the_same_phase_is_allowed()
    {
        var current = new LifecycleState(Phase.Execution, ProjectStatus.Building, 6);
        var result = RollbackRules.Plan(Request(5) with { Current = current });

        Assert.Equal(Phase.Execution, result.Value.Transition.To);
        Assert.Equal(5, result.Value.Transition.Version);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(0)]
    public void FR44_Target_not_below_current_is_invalid_target_version(int target) =>
        Assert.Equal(ErrorCodes.InvalidTargetVersion, RollbackRules.Plan(Request(target)).Error!.Code);

    [Fact]
    public void FR44_Target_with_no_recorded_state_is_invalid_target_version()
    {
        var request = Request(2) with { Transitions = [] };

        Assert.Equal(ErrorCodes.InvalidTargetVersion, RollbackRules.Plan(request).Error!.Code);
    }

    [Fact]
    public void FR45_Rollback_30_seconds_after_another_is_rate_limited()
    {
        var result = RollbackRules.Plan(Request(lastRollbackAt: T0.AddHours(1).AddSeconds(-30)));

        Assert.Equal(ErrorCodes.RateLimited, result.Error!.Code);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Error.RetryAfter);
        Assert.Equal(429, ErrorCodes.StatusCodeFor(result.Error.Code));
    }

    [Fact]
    public void FR45_Rollback_a_minute_after_another_is_allowed() =>
        Assert.True(RollbackRules.Plan(Request(lastRollbackAt: T0.AddHours(1).AddMinutes(-1))).IsSuccess);

    [Fact]
    public void FR45_Rollback_is_blocked_while_a_step_is_in_progress()
    {
        var step = new StepRef(Guid.NewGuid(), Guid.NewGuid(), StepStatus.InProgress);

        Assert.Equal(ErrorCodes.RollbackBlocked, RollbackRules.Plan(Request(steps: [step])).Error!.Code);
    }

    [Fact]
    public void FR45_Rollback_is_blocked_while_an_agent_run_is_in_progress() =>
        Assert.Equal(ErrorCodes.RollbackBlocked, RollbackRules.Plan(Request(runInProgress: true)).Error!.Code);

    [Fact]
    public void S4_8_After_rollback_the_next_forward_transition_is_target_plus_one()
    {
        var rolledBack = RollbackRules.Plan(Request()).Value.Transition.NewState;
        var snapshot = new LifecycleSnapshot
        {
            Mode = Aide.Domain.Gates.ApprovalMode.Autonomous,
            TechnicalSpecificationLocked = true,
            ImplementationPlanLocked = true,
            CurrentPlanStepCount = 2,
            WorkingCopyPrepared = true,
        };

        var next = LifecycleStateMachine.Apply(rolledBack, TransitionRequest.Advance(), snapshot);

        Assert.Equal(5, next.Value.Version);
        Assert.Equal(ProjectStatus.Building, next.Value.Status);
    }

    [Fact]
    public void FR45_Rollback_blocked_names_its_reason_in_the_details()
    {
        var running = RollbackRules.Plan(Request() with { ReleaseRunning = true }).Error!;
        Assert.Equal((ErrorCodes.RollbackBlocked, "reason: release_running"), (running.Code, Assert.Single(running.Details)));

        var partial = RollbackRules.Plan(Request() with { ReleasePartiallyApplied = true }).Error!;
        Assert.Equal((ErrorCodes.RollbackBlocked, "reason: release_partially_applied"), (partial.Code, Assert.Single(partial.Details)));

        var agent = RollbackRules.Plan(Request(runInProgress: true)).Error!;
        Assert.Equal("reason: agent_run_in_progress", Assert.Single(agent.Details));

        var step = new StepRef(Guid.CreateVersion7(), Guid.CreateVersion7(), StepStatus.InProgress);
        Assert.Equal("reason: step_in_progress", Assert.Single(RollbackRules.Plan(Request(steps: [step], runInProgress: true)).Error!.Details));
    }

    [Fact]
    public void FR45_Rollback_is_blocked_while_a_release_job_is_running_and_checks_run_in_order()
    {
        Assert.Equal(ErrorCodes.RollbackBlocked, RollbackRules.Plan(Request() with { ReleaseRunning = true }).Error!.Code);

        // INVALID_TARGET_VERSION → ROLLBACK_BLOCKED → RATE_LIMITED.
        var everything = Request(9, runInProgress: true, lastRollbackAt: T0.AddHours(1)) with { ReleaseRunning = true };
        Assert.Equal(ErrorCodes.InvalidTargetVersion, RollbackRules.Plan(everything).Error!.Code);
        Assert.Equal(ErrorCodes.RollbackBlocked, RollbackRules.Plan(everything with { TargetVersion = 4 }).Error!.Code);
        Assert.Equal(ErrorCodes.RateLimited, RollbackRules.Plan(Request(lastRollbackAt: T0.AddHours(1))).Error!.Code);
    }

    [Fact]
    public void FR44_Versions_list_the_restorable_versions_up_to_the_current_one_newest_first()
    {
        RecordedTransition[] history = [.. History, new(8, Phase.Release, T0.AddMinutes(9)), new(9, Phase.Release, T0.AddMinutes(10)), new(10, Phase.Review, T0.AddMinutes(11))];
        var created = T0.AddMinutes(-5);

        // The project is released at version 9; the transition at version 10 belongs to a timeline a rollback left.
        var versions = RollbackRules.Versions(history, 9, Phase.Release, created);

        Assert.Equal([9, 8, 7, 6, 5, 4, 3, 2, 1], versions.Select(v => v.Version));
        Assert.Equal(VersionBlock.CurrentVersion, versions[0].Block);
        Assert.Equal((Phase.Release, VersionBlock.ReleasePhase, false), (versions[1].Phase, versions[1].Block, versions[1].RollbackAllowed));
        Assert.Equal((Phase.Review, (VersionBlock?)null, true, T0.AddMinutes(8)), (versions[2].Phase, versions[2].Block, versions[2].RollbackAllowed, versions[2].EnteredAt));
        Assert.Equal(Phase.Execution, versions[3].Phase); // latest transition at version 6 (a review cycle)
        Assert.Equal((Phase.Refinement, created), (versions[8].Phase, versions[8].EnteredAt));
    }

    [Fact]
    public void FR44_Versions_skip_versions_without_a_recorded_phase_but_always_list_the_current_one()
    {
        var versions = RollbackRules.Versions([new RecordedTransition(3, Phase.Planning, T0)], 4, Phase.Execution, T0.AddMinutes(-1));

        Assert.Equal([(4, Phase.Execution), (3, Phase.Planning), (1, Phase.Refinement)], versions.Select(v => (v.Version, v.Phase)));
    }
}
