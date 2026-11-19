using Aide.Domain.Common;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Lifecycle;

/// <summary>
/// CLAUDE.md rule 8 / §4.1 / FR-11, FR-293: every allowed and forbidden phase and status transition, for every
/// transition kind, over the full phase × phase matrix, on the engine with the SDLC definition (<see cref="Sdlc"/>; 0.6.1's
/// cases and expected results, rewritten from the removed lifecycle machine).
/// </summary>
public class SdlcLifecycleTransitionTests
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
        StackProfileLocked = true,
        StackCompositionPinned = true,
    };

    public static TheoryData<Phase, Phase> AllPhasePairs()
    {
        var data = new TheoryData<Phase, Phase>();
        foreach (var from in Sdlc.All)
        {
            foreach (var to in Sdlc.All)
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    public static TheoryData<Phase, ProjectStatus> AllPhaseStatusPairs()
    {
        var data = new TheoryData<Phase, ProjectStatus>();
        foreach (var phase in Sdlc.All)
        {
            foreach (var status in Enum.GetValues<ProjectStatus>())
            {
                data.Add(phase, status);
            }
        }

        return data;
    }

    private static LifecycleState At(Phase phase, int version = 5) => new(phase, Sdlc.StatusFor(phase), version);

    // ---- Advance: only to the next phase ----

    [Theory]
    [MemberData(nameof(AllPhasePairs))]
    public void FR11_Advance_matrix_allows_only_the_next_phase(Phase from, Phase to)
    {
        var result = Sdlc.Apply(At(from), SdlcTransitions.AdvanceTo(to), AllMet);

        if (to == from + 1)
        {
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(to, result.Value.To);
            Assert.Equal(Sdlc.StatusFor(to), result.Value.Status);
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
        var result = Sdlc.Apply(new LifecycleState(from, status, 2), SdlcTransitions.Advance(), AllMet with { LatestReviewProjectVersion = 2 });

        if (from == Phase.Release)
        {
            Assert.Equal(ErrorCodes.WrongPhase, result.Error!.Code);
            return;
        }

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(Sdlc.StatusFor(from + 1), result.Value.Status);
        Assert.NotEqual(ProjectStatus.RolledBack, result.Value.Status);
        Assert.NotEqual(ProjectStatus.Released, result.Value.Status);
        Assert.Equal(3, result.Value.Version);
    }

    [Theory]
    [InlineData(Phase.Analysis, ProjectStatus.Active)]
    [InlineData(Phase.Refinement, ProjectStatus.Active)]
    [InlineData(Phase.Specification, ProjectStatus.Active)]
    [InlineData(Phase.Planning, ProjectStatus.Active)]
    [InlineData(Phase.Execution, ProjectStatus.Building)]
    [InlineData(Phase.Review, ProjectStatus.Reviewing)]
    [InlineData(Phase.Release, ProjectStatus.Releasing)]
    public void Status_follows_phase_table(Phase phase, ProjectStatus expected) =>
        Assert.Equal(expected, Sdlc.StatusFor(phase));

    // ---- Review cycle: review → execution only, version unchanged ----

    [Theory]
    [MemberData(nameof(AllPhasePairs))]
    public void FR39_ReviewCycle_matrix_allows_only_review_to_execution(Phase from, Phase to)
    {
        var snapshot = AllMet with { LatestReviewDecision = ReviewDecision.ReviewRequired };
        var result = Sdlc.Apply(At(from), SdlcTransitions.ReviewCycle(target: to), snapshot);

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
        var result = Sdlc.Apply(At(Phase.Review), SdlcTransitions.ReviewCycle(), snapshot);

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
        var result = Sdlc.Apply(At(Phase.Review), SdlcTransitions.ReviewCycle(), snapshot);

        Assert.Equal(ErrorCodes.PhasePreconditionFailed, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.Contains(reasonFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FR39_Release_entry_ignores_a_report_from_another_project_version()
    {
        var result = Sdlc.Apply(At(Phase.Review), SdlcTransitions.Advance(), AllMet with { LatestReviewProjectVersion = 4 });

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

        Assert.Equal(ErrorCodes.PhasePreconditionFailed, Sdlc.Apply(At(Phase.Review), SdlcTransitions.Advance(), snapshot).Error!.Code);
    }

    // ---- Rollback: to the same or an earlier phase, at an earlier version ----

    [Theory]
    [MemberData(nameof(AllPhasePairs))]
    public void FR44_Rollback_matrix_allows_same_or_earlier_phase(Phase from, Phase to)
    {
        var result = Sdlc.Apply(At(from, version: 7), SdlcTransitions.Rollback(to, 4), LifecycleSnapshot.Empty);

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
        var result = Sdlc.Apply(At(Phase.Review, 7), SdlcTransitions.Rollback(Phase.Planning, target), LifecycleSnapshot.Empty);

        Assert.Equal(ErrorCodes.InvalidTargetVersion, result.Error!.Code);
    }

    [Fact]
    public void FR44_Rolled_back_status_is_cleared_by_the_next_forward_transition()
    {
        var rolledBack = Sdlc.Apply(At(Phase.Review, 7), SdlcTransitions.Rollback(Phase.Planning, 4), LifecycleSnapshot.Empty).Value;
        var next = Sdlc.Apply(rolledBack.NewState, SdlcTransitions.Advance(), AllMet).Value;

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
        var result = Sdlc.Apply(At(from), SdlcTransitions.Fallback(to), LifecycleSnapshot.Empty);

        if (to < from)
        {
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(Sdlc.StatusFor(to), result.Value.Status);
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
        var result = Sdlc.Apply(state, SdlcTransitions.ReleaseNoop(target: to), LifecycleSnapshot.Empty);

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

    // ---- Release completed: only from release/releasing; released, checkpoint unchanged (D4 amended by §4.10) ----

    [Theory]
    [MemberData(nameof(AllPhaseStatusPairs))]
    public void FR42_amended_by_S4_10_FR65_ReleaseCompleted_only_from_release_releasing_or_awaiting_merge_and_keeps_the_checkpoint(Phase phase, ProjectStatus status)
    {
        var result = Sdlc.Apply(new LifecycleState(phase, status, 7), SdlcTransitions.ReleaseCompleted(), LifecycleSnapshot.Empty);

        if (phase == Phase.Release && status is ProjectStatus.Releasing or ProjectStatus.AwaitingMerge)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(ProjectStatus.Released, result.Value.Status);
            Assert.Equal(7, result.Value.Version);
        }
        else
        {
            Assert.Equal(ErrorCodes.NotReleasing, result.Error!.Code);
        }
    }

    // ---- FR-65: awaiting merge (status-only moves within release, checkpoint unchanged) ----

    [Theory]
    [MemberData(nameof(AllPhaseStatusPairs))]
    public void FR65_ReleaseAwaitingMerge_only_from_release_releasing(Phase phase, ProjectStatus status)
    {
        var result = Sdlc.Apply(new LifecycleState(phase, status, 7), SdlcTransitions.ReleaseAwaitingMerge(), LifecycleSnapshot.Empty);

        if (phase == Phase.Release && status == ProjectStatus.Releasing)
        {
            Assert.Equal(new LifecycleState(Phase.Release, ProjectStatus.AwaitingMerge, 7), result.Value.NewState);
            Assert.Equal(SdlcStatusChanges.ReleaseAwaitingMerge, result.Value.EventKind);
        }
        else
        {
            Assert.Equal(ErrorCodes.NotReleasing, result.Error!.Code);
        }
    }

    [Theory]
    [MemberData(nameof(AllPhaseStatusPairs))]
    public void FR65_ReleaseMergeAbandoned_only_from_release_awaiting_merge_back_to_releasing(Phase phase, ProjectStatus status)
    {
        var result = Sdlc.Apply(new LifecycleState(phase, status, 7), SdlcTransitions.ReleaseMergeAbandoned(), LifecycleSnapshot.Empty);

        if (phase == Phase.Release && status == ProjectStatus.AwaitingMerge)
        {
            Assert.Equal(new LifecycleState(Phase.Release, ProjectStatus.Releasing, 7), result.Value.NewState);
        }
        else
        {
            Assert.Equal(ErrorCodes.NotReleasing, result.Error!.Code);
        }
    }

    [Theory]
    [InlineData(SdlcStatusChanges.ReleaseAwaitingMerge, ProjectStatus.Releasing)]
    [InlineData(SdlcStatusChanges.ReleaseMergeAbandoned, ProjectStatus.AwaitingMerge)]
    public void FR65_Awaiting_merge_moves_never_leave_the_release_phase(string change, ProjectStatus status)
    {
        foreach (var target in Sdlc.All.Where(p => p != Phase.Release))
        {
            var result = Sdlc.Apply(
                new LifecycleState(Phase.Release, status, 7),
                new Transition(RunTransitionKind.StatusChange, target.ToWire(), StatusChange: change),
                LifecycleSnapshot.Empty);
            Assert.Equal(ErrorCodes.WrongPhase, result.Error!.Code);
            Assert.Equal("Awaiting a release merge stays in the release phase.", result.Error.Message);
        }
    }

    [Fact]
    public void FR65_A_project_awaiting_merge_can_not_advance_review_cycle_or_start_a_new_version()
    {
        var state = new LifecycleState(Phase.Release, ProjectStatus.AwaitingMerge, 7);

        Assert.Equal(ErrorCodes.WrongPhase, Sdlc.Apply(state, SdlcTransitions.Advance(), AllMet).Error!.Code);
        Assert.Equal(ErrorCodes.WrongPhase, Sdlc.Apply(state, SdlcTransitions.ReviewCycle(), AllMet).Error!.Code);
        Assert.Equal(ErrorCodes.NotReleased, Sdlc.NewVersion(state, LifecycleSnapshot.Empty).Error!.Code);
    }

    [Fact]
    public void D4_Entering_release_sets_releasing_not_released()
    {
        var result = Sdlc.Apply(At(Phase.Review), SdlcTransitions.Advance(), AllMet);

        Assert.Equal(ProjectStatus.Releasing, result.Value.Status);
    }

    [Fact]
    public void Released_is_never_produced_by_any_kind_other_than_release_completed()
    {
        foreach (var from in Sdlc.All)
        {
            foreach (var to in Sdlc.All)
            {
                var snapshot = AllMet with { LatestReviewDecision = ReviewDecision.ReviewRequired };
                Transition[] moves =
                [
                    SdlcTransitions.AdvanceTo(to),
                    SdlcTransitions.ReviewCycle(target: to),
                    SdlcTransitions.Rollback(to, 4),
                    SdlcTransitions.Fallback(to),
                    new Transition(RunTransitionKind.StatusChange, to.ToWire(), StatusChange: SdlcStatusChanges.ReleaseAwaitingMerge),
                    new Transition(RunTransitionKind.StatusChange, to.ToWire(), StatusChange: SdlcStatusChanges.ReleaseMergeAbandoned),
                ];
                var results = moves.Select(m => Sdlc.Apply(At(from, 7), m, snapshot)).Append(Sdlc.NewVersion(At(from, 7), snapshot, to));
                foreach (var result in results)
                {
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
        var result = Sdlc.Apply(LifecycleState.Initial, SdlcTransitions.Advance(), LifecycleSnapshot.Empty);

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
        { Phase.Review, "escalation", AllMet with { PendingGates = [EngineGates.Escalation] } },
    };

    [Theory]
    [MemberData(nameof(MissingConditions))]
    public void FR11_Each_unmet_entry_condition_fails_with_its_reason(Phase from, string reasonFragment, LifecycleSnapshot snapshot)
    {
        var result = Sdlc.Apply(At(from), SdlcTransitions.Advance(), snapshot);

        Assert.Equal(ErrorCodes.PhasePreconditionFailed, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.Contains(reasonFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FR14_Release_entry_accepts_an_accepted_escalation()
    {
        var snapshot = AllMet with { LatestReviewDecision = ReviewDecision.EscalationRequired, EscalationOutcome = EscalationDecision.Accept };

        var result = Sdlc.Apply(At(Phase.Review), SdlcTransitions.Advance(), snapshot);

        Assert.True(result.IsSuccess);
        Assert.Equal(Phase.Release, result.Value.To);
    }

    [Fact]
    public void Release_entry_ignores_the_pre_release_gate_itself_in_pending_gates()
    {
        var result = Sdlc.Apply(At(Phase.Review), SdlcTransitions.Advance(), AllMet with { PendingGates = [SdlcGates.PreRelease] });

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(ApprovalMode.Autonomous, Phase.Specification)]
    [InlineData(ApprovalMode.Autonomous, Phase.Planning)]
    [InlineData(ApprovalMode.Advisory, Phase.Planning)]
    public void Ungated_entries_do_not_need_gate_approval(ApprovalMode mode, Phase from)
    {
        var result = Sdlc.Apply(At(from), SdlcTransitions.Advance(), AllMet with { Mode = mode, EntryGateApproved = false });

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    // ---- RequestAdvance (FR-12) ----

    [Fact]
    public void FR12_Advisory_specification_with_approved_spec_creates_spec_approval_gate()
    {
        var snapshot = new LifecycleSnapshot { Mode = ApprovalMode.Advisory, ProjectRequestLocked = true, TechnicalSpecificationLocked = true };

        var result = Sdlc.RequestAdvance(At(Phase.Specification), snapshot);

        Assert.True(result.IsSuccess);
        Assert.Equal(SdlcGates.SpecApproval, result.Value.GateRequired);
        Assert.Null(result.Value.Transition);
    }

    [Fact]
    public void FR12_Advance_with_the_required_gate_already_pending_is_gate_pending()
    {
        var snapshot = new LifecycleSnapshot { Mode = ApprovalMode.Advisory, TechnicalSpecificationLocked = true, PendingGates = [SdlcGates.SpecApproval] };

        var result = Sdlc.RequestAdvance(At(Phase.Specification), snapshot);

        Assert.Equal(ErrorCodes.GatePending, result.Error!.Code);
    }

    [Fact]
    public void FR12_Autonomous_mode_applies_the_transition_directly()
    {
        var snapshot = new LifecycleSnapshot { Mode = ApprovalMode.Autonomous, TechnicalSpecificationLocked = true };

        var result = Sdlc.RequestAdvance(At(Phase.Specification, 2), snapshot, "ready");

        Assert.Null(result.Value.GateRequired);
        Assert.Equal(Phase.Planning, result.Value.Transition!.To);
        Assert.Equal(3, result.Value.Transition.Version);
        Assert.Equal("ready", result.Value.Transition.Reason);
    }

    [Fact]
    public void FR12_Strict_planning_needs_plan_approval_gate_advisory_does_not()
    {
        var snapshot = AllMet with { EntryGateApproved = false };

        Assert.Equal(SdlcGates.PlanApproval, Sdlc.RequestAdvance(At(Phase.Planning), snapshot with { Mode = ApprovalMode.Strict }).Value.GateRequired);
        Assert.Equal(Phase.Execution, Sdlc.RequestAdvance(At(Phase.Planning), snapshot with { Mode = ApprovalMode.Advisory }).Value.Transition!.To);
    }

    [Fact]
    public void FR39_Review_release_eligible_requests_a_pre_release_gate_in_every_mode()
    {
        foreach (var mode in Enum.GetValues<ApprovalMode>())
        {
            var result = Sdlc.RequestAdvance(At(Phase.Review), AllMet with { Mode = mode, EntryGateApproved = false });

            Assert.Equal(SdlcGates.PreRelease, result.Value.GateRequired);
        }
    }

    [Fact]
    public void FR11_RequestAdvance_preconditions_fail_before_any_gate_is_created()
    {
        var result = Sdlc.RequestAdvance(At(Phase.Specification), new LifecycleSnapshot { Mode = ApprovalMode.Strict });

        Assert.Equal(ErrorCodes.PhasePreconditionFailed, result.Error!.Code);
    }

    [Fact]
    public void RequestAdvance_from_release_is_wrong_phase()
    {
        var result = Sdlc.RequestAdvance(At(Phase.Release), AllMet);

        Assert.Equal(ErrorCodes.WrongPhase, result.Error!.Code);
    }

    // ---- Checkpoint rules (§4.10) ----

    [Fact]
    public void S4_10_Checkpoint_increments_on_forward_transitions_only_not_on_release_completion()
    {
        var state = LifecycleState.Initial;
        foreach (var _ in Enumerable.Range(0, 5))
        {
            state = Sdlc.Apply(state, SdlcTransitions.Advance(), AllMet).Value.NewState;
        }

        Assert.Equal(new LifecycleState(Phase.Release, ProjectStatus.Releasing, 6), state);
        Assert.Equal(6, Sdlc.Apply(state, SdlcTransitions.ReleaseNoop(), AllMet).Value.Version);
        var released = Sdlc.Apply(state, SdlcTransitions.ReleaseCompleted(), AllMet).Value;
        Assert.Equal(6, released.Version);

        // A new version starts again at checkpoint 1; the next forward transition is checkpoint 2.
        var started = Sdlc.NewVersion(released.NewState, LifecycleSnapshot.Empty).Value;
        Assert.Equal(new LifecycleState(Phase.Refinement, ProjectStatus.Active, 1), started.NewState);
        Assert.Equal(2, Sdlc.Apply(started.NewState, SdlcTransitions.Advance(), AllMet).Value.Version);
    }

    [Fact]
    public void S4_10_Checkpoint_does_not_change_on_review_cycle_fallback_or_noop()
    {
        var reviewing = At(Phase.Review, 5);
        var cycleMet = AllMet with { LatestReviewDecision = ReviewDecision.ReviewRequired, LatestReviewProjectVersion = 5 };

        Assert.Equal(5, Sdlc.Apply(reviewing, SdlcTransitions.ReviewCycle(), cycleMet).Value.Version);
        Assert.Equal(5, Sdlc.Apply(reviewing, SdlcTransitions.Fallback(Phase.Planning), LifecycleSnapshot.Empty).Value.Version);
        Assert.Equal(5, Sdlc.Apply(At(Phase.Release, 5), SdlcTransitions.ReleaseNoop(), LifecycleSnapshot.Empty).Value.Version);
        Assert.Equal(6, Sdlc.Apply(reviewing, SdlcTransitions.Advance(), AllMet).Value.Version);
    }

    // ---- New version: release → refinement, only from released with nothing in development (§4.10, FR-60) ----

    [Theory]
    [MemberData(nameof(AllPhaseStatusPairs))]
    public void S4_10_NewVersion_only_from_release_released(Phase phase, ProjectStatus status)
    {
        var result = Sdlc.NewVersion(new LifecycleState(phase, status, 6), LifecycleSnapshot.Empty);

        if (phase == Phase.Release && status == ProjectStatus.Released)
        {
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal((Phase.Release, Phase.Refinement, ProjectStatus.Active, 1, PhaseTransition.NewVersionEvent),
                (result.Value.From, result.Value.To, result.Value.Status, result.Value.Version, result.Value.EventKind));
            Assert.True(result.Value.ClearsReviewState);
            Assert.True(result.Value.ClearsExecutionPause);
        }
        else
        {
            Assert.Equal(ErrorCodes.NotReleased, result.Error!.Code);
        }
    }

    [Theory]
    [MemberData(nameof(AllPhaseStatusPairs))]
    public void S4_10_NewVersion_is_refused_from_every_phase_and_status_while_a_version_is_in_development(Phase phase, ProjectStatus status)
    {
        var result = Sdlc.NewVersion(new LifecycleState(phase, status, 6), LifecycleSnapshot.Empty with { DevelopmentVersionExists = true });

        Assert.Equal(
            phase == Phase.Release && status == ProjectStatus.Released ? ErrorCodes.VersionInProgress : ErrorCodes.NotReleased,
            result.Error!.Code);
    }

    [Theory]
    [MemberData(nameof(AllPhasePairs))]
    public void S4_10_NewVersion_matrix_allows_only_release_to_refinement(Phase from, Phase to)
    {
        var status = from == Phase.Release ? ProjectStatus.Released : Sdlc.StatusFor(from);
        var result = Sdlc.NewVersion(new LifecycleState(from, status, 6), LifecycleSnapshot.Empty, to);

        if (from == Phase.Release && to == Phase.Refinement)
        {
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        else
        {
            Assert.Contains(result.Error!.Code, new[] { ErrorCodes.WrongPhase, ErrorCodes.NotReleased });
        }
    }

    [Fact]
    public void S4_10_Advancing_from_release_is_still_wrong_phase_only_new_version_leaves_it()
    {
        var released = new LifecycleState(Phase.Release, ProjectStatus.Released, 6);

        Assert.Equal(ErrorCodes.WrongPhase, Sdlc.Apply(released, SdlcTransitions.Advance(), AllMet).Error!.Code);
        Assert.Equal(ErrorCodes.WrongPhase, Sdlc.Apply(released, SdlcTransitions.AdvanceTo(Phase.Refinement), AllMet).Error!.Code);
        Assert.True(Sdlc.NewVersion(released, LifecycleSnapshot.Empty).IsSuccess);
    }

    [Fact]
    public void Reason_defaults_when_none_is_given()
    {
        var result = Sdlc.Apply(At(Phase.Review), SdlcTransitions.Fallback(Phase.Planning), LifecycleSnapshot.Empty);

        Assert.False(string.IsNullOrWhiteSpace(result.Value.Reason));
        Assert.Equal(PhaseTransition.FallbackEvent, result.Value.EventKind);
    }

    [Fact]
    public void S4_8_Rollback_and_cascade_fallback_clear_the_review_state_other_transitions_keep_it()
    {
        var reviewing = At(Phase.Review, 7);
        var cycleMet = AllMet with { LatestReviewDecision = ReviewDecision.ReviewRequired, LatestReviewProjectVersion = 7 };

        Assert.True(Sdlc.Apply(reviewing, SdlcTransitions.Rollback(Phase.Planning, 4), LifecycleSnapshot.Empty).Value.ClearsReviewState);
        Assert.True(Sdlc.Apply(reviewing, SdlcTransitions.Fallback(Phase.Planning), LifecycleSnapshot.Empty).Value.ClearsReviewState);
        Assert.False(Sdlc.Apply(reviewing, SdlcTransitions.ReviewCycle(), cycleMet).Value.ClearsReviewState);
        Assert.False(Sdlc.Apply(At(Phase.Planning, 3), SdlcTransitions.Advance(), AllMet).Value.ClearsReviewState);
        Assert.True(Sdlc.NewVersion(new LifecycleState(Phase.Release, ProjectStatus.Released, 7), LifecycleSnapshot.Empty).Value.ClearsReviewState);
        Assert.False(Sdlc.Apply(reviewing, SdlcTransitions.Rollback(Phase.Planning, 4), LifecycleSnapshot.Empty).Value.ClearsExecutionPause);
    }
}
