using Aide.Domain.Common;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;

namespace Aide.UnitTests.Lifecycle;

/// <summary>
/// CLAUDE.md rule 9 / §4.1 / FR-11: every allowed and forbidden phase and status transition, for every
/// transition kind, over the full phase × phase matrix.
/// </summary>
public class LifecycleStateMachineTests
{
    /// <summary>A snapshot in which every entry condition of every phase is met.</summary>
    private static readonly LifecycleSnapshot AllMet = new()
    {
        Mode = ApprovalMode.Strict,
        ProjectRequestLocked = true,
        TechnicalSpecificationLocked = true,
        ImplementationPlanLocked = true,
        CurrentPlanStepCount = 3,
        CurrentPlanCompletedStepCount = 3,
        WorkingCopyPrepared = true,
        LatestReviewDecision = ReviewDecision.ReleaseEligible,
        LatestReviewProjectVersion = 5,
        OptimizationPlanLocked = true,
        OptimizationPlanStepCount = 2,
        EntryGateApproved = true,
    };

    public static TheoryData<Phase, Phase> AllPhasePairs()
    {
        var data = new TheoryData<Phase, Phase>();
        foreach (var from in PhaseOrder.All)
        {
            foreach (var to in PhaseOrder.All)
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    public static TheoryData<Phase, ProjectStatus> AllPhaseStatusPairs()
    {
        var data = new TheoryData<Phase, ProjectStatus>();
        foreach (var phase in PhaseOrder.All)
        {
            foreach (var status in Enum.GetValues<ProjectStatus>())
            {
                data.Add(phase, status);
            }
        }

        return data;
    }

    private static LifecycleState At(Phase phase, int version = 5) => new(phase, PhaseOrder.StatusFor(phase), version);

    // ---- Advance: only to the next phase ----

    [Theory]
    [MemberData(nameof(AllPhasePairs))]
    public void FR11_Advance_matrix_allows_only_the_next_phase(Phase from, Phase to)
    {
        var result = LifecycleStateMachine.Apply(At(from), TransitionRequest.AdvanceTo(to), AllMet);

        if (to == from + 1)
        {
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(to, result.Value.To);
            Assert.Equal(PhaseOrder.StatusFor(to), result.Value.Status);
            Assert.Equal(6, result.Value.Version);
        }
        else
        {
            Assert.Equal(ErrorCodes.WrongPhase, result.Error!.Code);
        }
    }

    [Theory]
    [MemberData(nameof(AllPhaseStatusPairs))]
    public void FR11_Advance_status_follows_phase_from_any_status_and_clears_rolled_back(Phase from, ProjectStatus status)
    {
        var result = LifecycleStateMachine.Apply(new LifecycleState(from, status, 2), TransitionRequest.Advance(), AllMet with { LatestReviewProjectVersion = 2 });

        if (from == Phase.Release)
        {
            Assert.Equal(ErrorCodes.WrongPhase, result.Error!.Code);
            return;
        }

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(PhaseOrder.StatusFor(from + 1), result.Value.Status);
        Assert.NotEqual(ProjectStatus.RolledBack, result.Value.Status);
        Assert.NotEqual(ProjectStatus.Released, result.Value.Status);
        Assert.Equal(3, result.Value.Version);
    }

    [Theory]
    [InlineData(Phase.Refinement, ProjectStatus.Active)]
    [InlineData(Phase.Specification, ProjectStatus.Active)]
    [InlineData(Phase.Planning, ProjectStatus.Active)]
    [InlineData(Phase.Execution, ProjectStatus.Building)]
    [InlineData(Phase.Review, ProjectStatus.Reviewing)]
    [InlineData(Phase.Release, ProjectStatus.Releasing)]
    public void Status_follows_phase_table(Phase phase, ProjectStatus expected) =>
        Assert.Equal(expected, PhaseOrder.StatusFor(phase));

    // ---- Review cycle: review → execution only, version unchanged ----

    [Theory]
    [MemberData(nameof(AllPhasePairs))]
    public void FR39_ReviewCycle_matrix_allows_only_review_to_execution(Phase from, Phase to)
    {
        var snapshot = AllMet with { LatestReviewDecision = ReviewDecision.ReviewRequired };
        var result = LifecycleStateMachine.Apply(At(from), new TransitionRequest(TransitionKind.ReviewCycle, to), snapshot);

        if (from == Phase.Review && to == Phase.Execution)
        {
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(ProjectStatus.Building, result.Value.Status);
            Assert.Equal(5, result.Value.Version);
        }
        else
        {
            Assert.Equal(ErrorCodes.WrongPhase, result.Error!.Code);
        }
    }

    [Theory]
    [InlineData(ReviewDecision.ReviewRequired, null, true)]
    [InlineData(ReviewDecision.EscalationRequired, EscalationDecision.Continue, true)]
    [InlineData(ReviewDecision.EscalationRequired, null, false)]
    [InlineData(ReviewDecision.EscalationRequired, EscalationDecision.Accept, false)]
    [InlineData(ReviewDecision.ReleaseEligible, null, false)]
    [InlineData(null, null, false)]
    public void FR14_ReviewCycle_requires_review_required_or_escalation_continue(ReviewDecision? decision, EscalationDecision? escalation, bool allowed)
    {
        var snapshot = AllMet with { LatestReviewDecision = decision, EscalationOutcome = escalation };
        var result = LifecycleStateMachine.Apply(At(Phase.Review), TransitionRequest.ReviewCycle(), snapshot);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal(ErrorCodes.PhasePreconditionFailed, result.Error!.Code);
        }
    }

    public static TheoryData<string, LifecycleSnapshot> MissingReviewCycleConditions() => new()
    {
        { "optimization plan is not approved", AllMet with { LatestReviewDecision = ReviewDecision.ReviewRequired, OptimizationPlanLocked = false } },
        { "optimization plan has no steps", AllMet with { LatestReviewDecision = ReviewDecision.ReviewRequired, OptimizationPlanStepCount = 0 } },
        { "working copy", AllMet with { LatestReviewDecision = ReviewDecision.ReviewRequired, WorkingCopyPrepared = false } },
        { "review_required", AllMet with { LatestReviewDecision = ReviewDecision.ReviewRequired, LatestReviewProjectVersion = 4 } },
    };

    [Theory]
    [MemberData(nameof(MissingReviewCycleConditions))]
    public void FR40_ReviewCycle_needs_an_approved_optimisation_plan_with_steps_and_a_working_copy(string reasonFragment, LifecycleSnapshot snapshot)
    {
        var result = LifecycleStateMachine.Apply(At(Phase.Review), TransitionRequest.ReviewCycle(), snapshot);

        Assert.Equal(ErrorCodes.PhasePreconditionFailed, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.Contains(reasonFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FR39_Release_entry_ignores_a_report_from_another_project_version()
    {
        var result = LifecycleStateMachine.Apply(At(Phase.Review), TransitionRequest.Advance(), AllMet with { LatestReviewProjectVersion = 4 });

        Assert.Equal(ErrorCodes.PhasePreconditionFailed, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.Contains("release_eligible", StringComparison.Ordinal));
    }

    [Fact]
    public void FR14_Accepted_escalation_from_a_stale_report_does_not_allow_release()
    {
        var snapshot = AllMet with
        {
            LatestReviewDecision = ReviewDecision.EscalationRequired,
            EscalationOutcome = EscalationDecision.Accept,
            LatestReviewProjectVersion = 3,
        };

        Assert.Equal(ErrorCodes.PhasePreconditionFailed, LifecycleStateMachine.Apply(At(Phase.Review), TransitionRequest.Advance(), snapshot).Error!.Code);
    }

    // ---- Rollback: to the same or an earlier phase, at an earlier version ----

    [Theory]
    [MemberData(nameof(AllPhasePairs))]
    public void FR44_Rollback_matrix_allows_same_or_earlier_phase(Phase from, Phase to)
    {
        var result = LifecycleStateMachine.Apply(At(from, version: 7), TransitionRequest.Rollback(to, 4), LifecycleSnapshot.Empty);

        if (to <= from)
        {
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(to, result.Value.To);
            Assert.Equal(ProjectStatus.RolledBack, result.Value.Status);
            Assert.Equal(4, result.Value.Version);
        }
        else
        {
            Assert.Equal(ErrorCodes.WrongPhase, result.Error!.Code);
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(0)]
    [InlineData(-1)]
    public void FR44_Rollback_to_a_version_not_below_current_is_invalid(int target)
    {
        var result = LifecycleStateMachine.Apply(At(Phase.Review, 7), TransitionRequest.Rollback(Phase.Planning, target), LifecycleSnapshot.Empty);

        Assert.Equal(ErrorCodes.InvalidTargetVersion, result.Error!.Code);
    }

    [Fact]
    public void FR44_Rolled_back_status_is_cleared_by_the_next_forward_transition()
    {
        var rolledBack = LifecycleStateMachine.Apply(At(Phase.Review, 7), TransitionRequest.Rollback(Phase.Planning, 4), LifecycleSnapshot.Empty).Value;
        var next = LifecycleStateMachine.Apply(rolledBack.NewState, TransitionRequest.Advance(), AllMet).Value;

        Assert.Equal(ProjectStatus.RolledBack, rolledBack.Status);
        Assert.Equal(Phase.Execution, next.To);
        Assert.Equal(ProjectStatus.Building, next.Status);
        Assert.Equal(5, next.Version);
    }

    // ---- Invalidation fallback: strictly earlier phase, version unchanged ----

    [Theory]
    [MemberData(nameof(AllPhasePairs))]
    public void FR19_Fallback_matrix_allows_only_earlier_phases(Phase from, Phase to)
    {
        var result = LifecycleStateMachine.Apply(At(from), TransitionRequest.Fallback(to), LifecycleSnapshot.Empty);

        if (to < from)
        {
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(PhaseOrder.StatusFor(to), result.Value.Status);
            Assert.Equal(5, result.Value.Version);
        }
        else
        {
            Assert.Equal(ErrorCodes.WrongPhase, result.Error!.Code);
        }
    }

    // ---- release → release no-op ----

    [Theory]
    [MemberData(nameof(AllPhasePairs))]
    public void FR42_ReleaseNoop_matrix_allows_only_release_to_release(Phase from, Phase to)
    {
        var state = At(from);
        var result = LifecycleStateMachine.Apply(state, new TransitionRequest(TransitionKind.ReleaseNoop, to), LifecycleSnapshot.Empty);

        if (from == Phase.Release && to == Phase.Release)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(state, result.Value.NewState);
        }
        else
        {
            Assert.Equal(ErrorCodes.WrongPhase, result.Error!.Code);
        }
    }

    // ---- Release completed: only from release/releasing; released + version + 1 (D4) ----

    [Theory]
    [MemberData(nameof(AllPhaseStatusPairs))]
    public void FR42_ReleaseCompleted_only_from_release_releasing(Phase phase, ProjectStatus status)
    {
        var result = LifecycleStateMachine.Apply(new LifecycleState(phase, status, 7), TransitionRequest.ReleaseCompleted(), LifecycleSnapshot.Empty);

        if (phase == Phase.Release && status == ProjectStatus.Releasing)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(ProjectStatus.Released, result.Value.Status);
            Assert.Equal(8, result.Value.Version);
        }
        else
        {
            Assert.Equal(ErrorCodes.NotReleasing, result.Error!.Code);
        }
    }

    [Fact]
    public void D4_Entering_release_sets_releasing_not_released()
    {
        var result = LifecycleStateMachine.Apply(At(Phase.Review), TransitionRequest.Advance(), AllMet);

        Assert.Equal(ProjectStatus.Releasing, result.Value.Status);
    }

    [Fact]
    public void Released_is_never_produced_by_any_kind_other_than_release_completed()
    {
        foreach (var from in PhaseOrder.All)
        {
            foreach (var to in PhaseOrder.All)
            {
                foreach (var kind in Enum.GetValues<TransitionKind>().Where(k => k != TransitionKind.ReleaseCompleted && k != TransitionKind.ReleaseNoop))
                {
                    var result = LifecycleStateMachine.Apply(At(from, 7), new TransitionRequest(kind, to, 4), AllMet with { LatestReviewDecision = ReviewDecision.ReviewRequired });
                    if (result.IsSuccess)
                    {
                        Assert.NotEqual(ProjectStatus.Released, result.Value.Status);
                    }
                }
            }
        }
    }

    // ---- Entry conditions (§4.1 table) ----

    [Fact]
    public void FR11_Refinement_without_approved_project_request_cannot_advance()
    {
        var result = LifecycleStateMachine.Apply(LifecycleState.Initial, TransitionRequest.Advance(), LifecycleSnapshot.Empty);

        Assert.Equal(ErrorCodes.PhasePreconditionFailed, result.Error!.Code);
        Assert.Contains("project request", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<Phase, string, LifecycleSnapshot> MissingConditions() => new()
    {
        { Phase.Refinement, "project request", AllMet with { ProjectRequestLocked = false } },
        { Phase.Specification, "technical specification", AllMet with { TechnicalSpecificationLocked = false } },
        { Phase.Specification, "spec_approval", AllMet with { EntryGateApproved = false } },
        { Phase.Planning, "technical specification", AllMet with { TechnicalSpecificationLocked = false } },
        { Phase.Planning, "implementation plan is not approved", AllMet with { ImplementationPlanLocked = false } },
        { Phase.Planning, "no steps", AllMet with { CurrentPlanStepCount = 0, CurrentPlanCompletedStepCount = 0 } },
        { Phase.Planning, "working copy", AllMet with { WorkingCopyPrepared = false } },
        { Phase.Planning, "plan_approval", AllMet with { EntryGateApproved = false } },
        { Phase.Execution, "2 of 3", AllMet with { CurrentPlanCompletedStepCount = 2 } },
        { Phase.Execution, "0 of 0", AllMet with { CurrentPlanStepCount = 0, CurrentPlanCompletedStepCount = 0 } },
        { Phase.Review, "release_eligible", AllMet with { LatestReviewDecision = ReviewDecision.ReviewRequired } },
        { Phase.Review, "pre_release", AllMet with { EntryGateApproved = false } },
        { Phase.Review, "escalation", AllMet with { PendingGates = [GateType.Escalation] } },
    };

    [Theory]
    [MemberData(nameof(MissingConditions))]
    public void FR11_Each_unmet_entry_condition_fails_with_its_reason(Phase from, string reasonFragment, LifecycleSnapshot snapshot)
    {
        var result = LifecycleStateMachine.Apply(At(from), TransitionRequest.Advance(), snapshot);

        Assert.Equal(ErrorCodes.PhasePreconditionFailed, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.Contains(reasonFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FR14_Release_entry_accepts_an_accepted_escalation()
    {
        var snapshot = AllMet with { LatestReviewDecision = ReviewDecision.EscalationRequired, EscalationOutcome = EscalationDecision.Accept };

        var result = LifecycleStateMachine.Apply(At(Phase.Review), TransitionRequest.Advance(), snapshot);

        Assert.True(result.IsSuccess);
        Assert.Equal(Phase.Release, result.Value.To);
    }

    [Fact]
    public void Release_entry_ignores_the_pre_release_gate_itself_in_pending_gates()
    {
        var result = LifecycleStateMachine.Apply(At(Phase.Review), TransitionRequest.Advance(), AllMet with { PendingGates = [GateType.PreRelease] });

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(ApprovalMode.Autonomous, Phase.Specification)]
    [InlineData(ApprovalMode.Autonomous, Phase.Planning)]
    [InlineData(ApprovalMode.Advisory, Phase.Planning)]
    public void Ungated_entries_do_not_need_gate_approval(ApprovalMode mode, Phase from)
    {
        var result = LifecycleStateMachine.Apply(At(from), TransitionRequest.Advance(), AllMet with { Mode = mode, EntryGateApproved = false });

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    // ---- RequestAdvance (FR-12) ----

    [Fact]
    public void FR12_Advisory_specification_with_approved_spec_creates_spec_approval_gate()
    {
        var snapshot = new LifecycleSnapshot { Mode = ApprovalMode.Advisory, ProjectRequestLocked = true, TechnicalSpecificationLocked = true };

        var result = LifecycleStateMachine.RequestAdvance(At(Phase.Specification), snapshot);

        Assert.True(result.IsSuccess);
        Assert.Equal(GateType.SpecApproval, result.Value.GateRequired);
        Assert.Null(result.Value.Transition);
    }

    [Fact]
    public void FR12_Advance_with_the_required_gate_already_pending_is_gate_pending()
    {
        var snapshot = new LifecycleSnapshot { Mode = ApprovalMode.Advisory, TechnicalSpecificationLocked = true, PendingGates = [GateType.SpecApproval] };

        var result = LifecycleStateMachine.RequestAdvance(At(Phase.Specification), snapshot);

        Assert.Equal(ErrorCodes.GatePending, result.Error!.Code);
    }

    [Fact]
    public void FR12_Autonomous_mode_applies_the_transition_directly()
    {
        var snapshot = new LifecycleSnapshot { Mode = ApprovalMode.Autonomous, TechnicalSpecificationLocked = true };

        var result = LifecycleStateMachine.RequestAdvance(At(Phase.Specification, 2), snapshot, "ready");

        Assert.Null(result.Value.GateRequired);
        Assert.Equal(Phase.Planning, result.Value.Transition!.To);
        Assert.Equal(3, result.Value.Transition.Version);
        Assert.Equal("ready", result.Value.Transition.Reason);
    }

    [Fact]
    public void FR12_Strict_planning_needs_plan_approval_gate_advisory_does_not()
    {
        var snapshot = AllMet with { EntryGateApproved = false };

        Assert.Equal(GateType.PlanApproval, LifecycleStateMachine.RequestAdvance(At(Phase.Planning), snapshot with { Mode = ApprovalMode.Strict }).Value.GateRequired);
        Assert.Equal(Phase.Execution, LifecycleStateMachine.RequestAdvance(At(Phase.Planning), snapshot with { Mode = ApprovalMode.Advisory }).Value.Transition!.To);
    }

    [Fact]
    public void FR39_Review_release_eligible_requests_a_pre_release_gate_in_every_mode()
    {
        foreach (var mode in Enum.GetValues<ApprovalMode>())
        {
            var result = LifecycleStateMachine.RequestAdvance(At(Phase.Review), AllMet with { Mode = mode, EntryGateApproved = false });

            Assert.Equal(GateType.PreRelease, result.Value.GateRequired);
        }
    }

    [Fact]
    public void FR11_RequestAdvance_preconditions_fail_before_any_gate_is_created()
    {
        var result = LifecycleStateMachine.RequestAdvance(At(Phase.Specification), new LifecycleSnapshot { Mode = ApprovalMode.Strict });

        Assert.Equal(ErrorCodes.PhasePreconditionFailed, result.Error!.Code);
    }

    [Fact]
    public void RequestAdvance_from_release_is_wrong_phase()
    {
        var result = LifecycleStateMachine.RequestAdvance(At(Phase.Release), AllMet);

        Assert.Equal(ErrorCodes.WrongPhase, result.Error!.Code);
    }

    // ---- Version rules ----

    [Fact]
    public void Version_increments_on_forward_and_release_only()
    {
        var state = LifecycleState.Initial;
        foreach (var _ in Enumerable.Range(0, 5))
        {
            state = LifecycleStateMachine.Apply(state, TransitionRequest.Advance(), AllMet).Value.NewState;
        }

        Assert.Equal(new LifecycleState(Phase.Release, ProjectStatus.Releasing, 6), state);
        Assert.Equal(6, LifecycleStateMachine.Apply(state, TransitionRequest.ReleaseNoop(), AllMet).Value.Version);
        Assert.Equal(7, LifecycleStateMachine.Apply(state, TransitionRequest.ReleaseCompleted(), AllMet).Value.Version);
    }

    [Fact]
    public void Reason_defaults_when_none_is_given()
    {
        var result = LifecycleStateMachine.Apply(At(Phase.Review), TransitionRequest.Fallback(Phase.Planning), LifecycleSnapshot.Empty);

        Assert.False(string.IsNullOrWhiteSpace(result.Value.Reason));
        Assert.Equal(TransitionKind.InvalidationFallback, result.Value.Kind);
    }

    [Fact]
    public void S4_8_Rollback_and_cascade_fallback_clear_the_review_state_other_transitions_keep_it()
    {
        var reviewing = At(Phase.Review, 7);
        var cycleMet = AllMet with { LatestReviewDecision = ReviewDecision.ReviewRequired, LatestReviewProjectVersion = 7 };

        Assert.True(LifecycleStateMachine.Apply(reviewing, TransitionRequest.Rollback(Phase.Planning, 4), LifecycleSnapshot.Empty).Value.ClearsReviewState);
        Assert.True(LifecycleStateMachine.Apply(reviewing, TransitionRequest.Fallback(Phase.Planning), LifecycleSnapshot.Empty).Value.ClearsReviewState);
        Assert.False(LifecycleStateMachine.Apply(reviewing, TransitionRequest.ReviewCycle(), cycleMet).Value.ClearsReviewState);
        Assert.False(LifecycleStateMachine.Apply(At(Phase.Planning, 3), TransitionRequest.Advance(), AllMet).Value.ClearsReviewState);
    }
}
