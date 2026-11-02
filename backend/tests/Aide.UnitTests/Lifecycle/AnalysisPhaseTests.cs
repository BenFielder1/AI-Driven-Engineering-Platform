using Aide.Domain.Common;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;

namespace Aide.UnitTests.Lifecycle;

/// <summary>
/// FR-179, §4.27 (CLAUDE.md rule 8): every allowed and forbidden transition into and out of <c>analysis</c>, verbatim from the
/// §4.27 transition table.
/// </summary>
public class AnalysisPhaseTests
{
    private static readonly LifecycleSnapshot ProfileApproved = new() { StackProfileLocked = true, StackCompositionPinned = true };

    private static LifecycleState InAnalysis(ProjectStatus status = ProjectStatus.Active, int checkpoint = 1) => new(Phase.Analysis, status, checkpoint);

    [Fact]
    public void FR179_S4_27_Phase_order_puts_analysis_first_and_before_refinement()
    {
        Assert.Equal(
            [Phase.Analysis, Phase.Refinement, Phase.Specification, Phase.Planning, Phase.Execution, Phase.Review, Phase.Release],
            PhaseOrder.All);
        Assert.True(Phase.Analysis.IsBefore(Phase.Refinement));
        Assert.False(Phase.Refinement.IsBefore(Phase.Analysis));
        Assert.Equal(Phase.Refinement, PhaseOrder.Next(Phase.Analysis));
        Assert.DoesNotContain((Phase?)Phase.Analysis, PhaseOrder.All.Select(p => PhaseOrder.Next(p)));
    }

    [Fact]
    public void FR179_S4_27_Status_for_analysis_is_active_and_no_gate_guards_it_in_any_mode()
    {
        Assert.Equal(ProjectStatus.Active, PhaseOrder.StatusFor(Phase.Analysis));
        foreach (var mode in Enum.GetValues<ApprovalMode>())
        {
            Assert.Null(GateRules.RequiredGate(mode, Phase.Analysis));
            Assert.Null(GateRules.RequiredGate(mode, Phase.Refinement));
        }
    }

    // ---- project or version created → analysis: stack_source = infer and a base commit with code ----

    [Fact]
    public void FR178_S4_27_A_project_created_with_inference_and_code_starts_in_analysis()
    {
        var start = LifecycleStateMachine.Start(infersStack: true, baseHasCode: true);

        Assert.Equal(new LifecycleState(Phase.Analysis, ProjectStatus.Active, 1), start.Value);
    }

    [Fact]
    public void FR178_S4_27_A_project_created_with_inference_and_no_code_is_STACK_INFER_NO_CODE()
    {
        var start = LifecycleStateMachine.Start(infersStack: true, baseHasCode: false);

        Assert.Equal(ErrorCodes.StackInferNoCode, start.Error!.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FR178_D_V5_2_A_project_created_without_inference_starts_in_refinement_as_today(bool baseHasCode) =>
        Assert.Equal(LifecycleState.Initial, LifecycleStateMachine.Start(infersStack: false, baseHasCode).Value);

    [Fact]
    public void FR186_S4_27_A_new_version_with_inference_and_code_starts_in_analysis_at_checkpoint_1()
    {
        var released = new LifecycleState(Phase.Release, ProjectStatus.Released, 6);

        var started = LifecycleStateMachine.Apply(released, TransitionRequest.NewVersionInto(Phase.Analysis), new LifecycleSnapshot { InfersStack = true, BaseHasCode = true });

        Assert.Equal((Phase.Analysis, ProjectStatus.Active, 1, TransitionKind.NewVersion), (started.Value.To, started.Value.Status, started.Value.Version, started.Value.Kind));
        Assert.True(started.Value.ClearsReviewState);
    }

    [Fact]
    public void FR186_S4_27_A_new_version_with_inference_and_no_base_code_is_STACK_INFER_NO_CODE()
    {
        var released = new LifecycleState(Phase.Release, ProjectStatus.Released, 6);

        var started = LifecycleStateMachine.Apply(released, TransitionRequest.NewVersionInto(Phase.Analysis), new LifecycleSnapshot { InfersStack = true, BaseHasCode = false });

        Assert.Equal(ErrorCodes.StackInferNoCode, started.Error!.Code);
    }

    [Fact]
    public void FR186_S4_27_Only_an_inference_version_starts_in_analysis_and_it_never_starts_in_refinement()
    {
        var released = new LifecycleState(Phase.Release, ProjectStatus.Released, 6);

        Assert.Equal(ErrorCodes.WrongPhase, LifecycleStateMachine.Apply(released, TransitionRequest.NewVersionInto(Phase.Analysis), new LifecycleSnapshot { BaseHasCode = true }).Error!.Code);
        Assert.Equal(ErrorCodes.WrongPhase, LifecycleStateMachine.Apply(released, TransitionRequest.NewVersion(), new LifecycleSnapshot { InfersStack = true, BaseHasCode = true }).Error!.Code);
        Assert.Equal(Phase.Refinement, LifecycleStateMachine.Apply(released, TransitionRequest.NewVersion(), LifecycleSnapshot.Empty).Value.To);
    }

    // ---- analysis → refinement: locked stack_profile and a composition, or locked with generalist ----

    [Theory]
    [InlineData(ApprovalMode.Strict)]
    [InlineData(ApprovalMode.Advisory)]
    [InlineData(ApprovalMode.Autonomous)]
    public void FR179_D_V5_5_Analysis_to_refinement_with_a_locked_profile_and_composition_applies_in_every_mode(ApprovalMode mode)
    {
        var outcome = LifecycleStateMachine.RequestAdvance(InAnalysis(), ProfileApproved with { Mode = mode });

        Assert.Null(outcome.Value.GateRequired);
        Assert.Equal((Phase.Analysis, Phase.Refinement, ProjectStatus.Active, 2), (outcome.Value.Transition!.From, outcome.Value.Transition.To, outcome.Value.Transition.Status, outcome.Value.Transition.Version));
    }

    [Fact]
    public void FR181_S4_27_Analysis_to_refinement_with_a_locked_generalist_profile_needs_no_composition()
    {
        var result = LifecycleStateMachine.Apply(InAnalysis(), TransitionRequest.Advance(), new LifecycleSnapshot { StackProfileLocked = true, StackProfileGeneralist = true });

        Assert.Equal(Phase.Refinement, result.Value.To);
    }

    [Theory]
    [InlineData(ApprovalMode.Strict)]
    [InlineData(ApprovalMode.Advisory)]
    [InlineData(ApprovalMode.Autonomous)]
    public void FR179_D_V5_5_Given_Autonomous_mode_and_a_drafted_profile_the_project_stays_in_analysis(ApprovalMode mode)
    {
        var outcome = LifecycleStateMachine.RequestAdvance(InAnalysis(), new LifecycleSnapshot { Mode = mode });

        Assert.Equal(ErrorCodes.PhasePreconditionFailed, outcome.Error!.Code);
        Assert.Contains(outcome.Error.Details, d => d.Contains("stack profile is not approved", StringComparison.Ordinal));
    }

    [Fact]
    public void FR179_S4_27_A_locked_profile_without_a_composition_or_generalist_can_not_leave_analysis()
    {
        var result = LifecycleStateMachine.Apply(InAnalysis(), TransitionRequest.Advance(), new LifecycleSnapshot { StackProfileLocked = true });

        Assert.Equal(ErrorCodes.PhasePreconditionFailed, result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.Contains("No stack composition is pinned", StringComparison.Ordinal));
    }

    [Fact]
    public void FR179_S4_27_A_rolled_back_project_in_analysis_leaves_it_with_the_same_conditions()
    {
        var result = LifecycleStateMachine.Apply(InAnalysis(ProjectStatus.RolledBack, 1), TransitionRequest.Advance(), ProfileApproved);

        Assert.Equal((Phase.Refinement, ProjectStatus.Active), (result.Value.To, result.Value.Status));
    }

    [Fact]
    public void FR179_Refinement_entered_any_other_way_has_no_stack_profile_condition()
    {
        var released = new LifecycleState(Phase.Release, ProjectStatus.Released, 6);

        Assert.True(LifecycleStateMachine.Apply(released, TransitionRequest.NewVersion(), LifecycleSnapshot.Empty).IsSuccess);
        Assert.True(LifecycleStateMachine.Apply(new LifecycleState(Phase.Planning, ProjectStatus.Active, 3), TransitionRequest.Rollback(Phase.Refinement, 1), LifecycleSnapshot.Empty).IsSuccess);
    }

    // ---- anything else → analysis, forward: never ----

    public static TheoryData<Phase> AllPhases() => [.. PhaseOrder.All];

    [Theory]
    [MemberData(nameof(AllPhases))]
    public void FR179_S4_27_No_forward_move_enters_analysis(Phase from)
    {
        var state = new LifecycleState(from, from == Phase.Release ? ProjectStatus.Released : PhaseOrder.StatusFor(from), 5);
        var everything = ProfileApproved with
        {
            ProjectRequestLocked = true,
            TechnicalSpecificationLocked = true,
            ImplementationPlanLocked = true,
            CurrentPlanStepCount = 1,
            CurrentPlanCompletedStepCount = 1,
            WorkingCopyPrepared = true,
            LatestReviewDecision = ReviewDecision.ReleaseEligible,
            LatestReviewProjectVersion = 5,
            EntryGateApproved = true,
        };

        Assert.Equal(ErrorCodes.WrongPhase, LifecycleStateMachine.Apply(state, TransitionRequest.AdvanceTo(Phase.Analysis), everything).Error!.Code);
        Assert.Equal(ErrorCodes.WrongPhase, LifecycleStateMachine.Apply(state, new TransitionRequest(TransitionKind.ReviewCycle, Phase.Analysis), everything).Error!.Code);
        Assert.NotEmpty(LifecycleStateMachine.UnmetEntryConditions(state, Phase.Analysis, everything));
    }

    // ---- analysis → specification or later: never ----

    [Theory]
    [InlineData(Phase.Specification)]
    [InlineData(Phase.Planning)]
    [InlineData(Phase.Execution)]
    [InlineData(Phase.Review)]
    [InlineData(Phase.Release)]
    public void FR179_S4_27_Analysis_never_moves_to_specification_or_later(Phase to)
    {
        Assert.Equal(ErrorCodes.WrongPhase, LifecycleStateMachine.Apply(InAnalysis(), TransitionRequest.AdvanceTo(to), ProfileApproved).Error!.Code);
        Assert.Equal(ErrorCodes.WrongPhase, LifecycleStateMachine.Apply(InAnalysis(checkpoint: 3), TransitionRequest.Rollback(to, 1), ProfileApproved).Error!.Code);
        Assert.Equal(ErrorCodes.WrongPhase, LifecycleStateMachine.Apply(InAnalysis(), TransitionRequest.Fallback(to), ProfileApproved).Error!.Code);
    }

    [Fact]
    public void FR179_Analysis_has_no_review_cycle_release_or_new_version_moves()
    {
        Assert.Equal(ErrorCodes.WrongPhase, LifecycleStateMachine.Apply(InAnalysis(), TransitionRequest.ReviewCycle(), ProfileApproved).Error!.Code);
        Assert.Equal(ErrorCodes.WrongPhase, LifecycleStateMachine.Apply(InAnalysis(), TransitionRequest.ReleaseNoop(), ProfileApproved).Error!.Code);
        Assert.Equal(ErrorCodes.NotReleasing, LifecycleStateMachine.Apply(InAnalysis(), TransitionRequest.ReleaseCompleted(), ProfileApproved).Error!.Code);
        Assert.Equal(ErrorCodes.NotReleased, LifecycleStateMachine.Apply(InAnalysis(), TransitionRequest.NewVersion(), ProfileApproved).Error!.Code);
    }

    // ---- rollback → a checkpoint in analysis: v1 rollback rules; rolled_back status applies ----

    [Theory]
    [InlineData(Phase.Refinement)]
    [InlineData(Phase.Planning)]
    [InlineData(Phase.Review)]
    public void FR179_S4_27_Rollback_to_a_checkpoint_in_analysis_follows_v1_rules(Phase from)
    {
        var result = LifecycleStateMachine.Apply(new LifecycleState(from, PhaseOrder.StatusFor(from), 4), TransitionRequest.Rollback(Phase.Analysis, 1), LifecycleSnapshot.Empty);

        Assert.Equal((Phase.Analysis, ProjectStatus.RolledBack, 1), (result.Value.To, result.Value.Status, result.Value.Version));
    }

    [Fact]
    public void FR179_S4_27_Refinement_to_analysis_needs_an_earlier_checkpoint()
    {
        // The caller only asks for analysis when the target checkpoint was recorded in it (RollbackRules.RestoredPhase);
        // without an earlier checkpoint there is nothing to roll back to.
        var result = LifecycleStateMachine.Apply(new LifecycleState(Phase.Refinement, ProjectStatus.Active, 1), TransitionRequest.Rollback(Phase.Analysis, 1), LifecycleSnapshot.Empty);

        Assert.Equal(ErrorCodes.InvalidTargetVersion, result.Error!.Code);
    }

    [Fact]
    public void FR183_S4_27_The_stack_profile_cascade_falls_back_to_analysis()
    {
        var result = LifecycleStateMachine.Apply(new LifecycleState(Phase.Refinement, ProjectStatus.Active, 2), TransitionRequest.Fallback(Phase.Analysis), LifecycleSnapshot.Empty);

        Assert.Equal((Phase.Analysis, ProjectStatus.Active, 2), (result.Value.To, result.Value.Status, result.Value.Version));
    }
}
