using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Pack.Sdlc;
using static Aide.UnitTests.Lifecycle.SdlcPipelineFixture;

namespace Aide.UnitTests.Lifecycle;

/// <summary>
/// FR-287–FR-289 with the SDLC definition: spot checks of the engine against 0.6.1's results (codes, messages, reasons,
/// the gate needed, the job queued on entry, the cascade's step reset). The transition matrix itself is
/// <see cref="SdlcLifecycleTransitionTests"/> (rule 8); the M58 parity tests against the removed machine went with it (FR-293).
/// </summary>
public class SdlcEngineSpotCheckTests
{
    private const int Checkpoint = 4;
    private const int ReviewCycles = 3;

    [Fact]
    public void FR287_engine_error_codes_equal_the_host_error_table()
    {
        Assert.Equal(ErrorCodes.PhasePreconditionFailed, EngineErrorCodes.PhasePreconditionFailed);
        Assert.Equal(ErrorCodes.GatePending, EngineErrorCodes.GatePending);
        Assert.Equal(ErrorCodes.WrongPhase, EngineErrorCodes.WrongPhase);
        Assert.Equal(ErrorCodes.InvalidTargetVersion, EngineErrorCodes.InvalidTargetVersion);
    }

    [Theory]
    [InlineData(Phase.Analysis, null)]
    [InlineData(Phase.Refinement, null)]
    [InlineData(Phase.Specification, SdlcJobTypes.SpecGeneration)]
    [InlineData(Phase.Planning, SdlcJobTypes.PlanGeneration)]
    [InlineData(Phase.Execution, null)]
    [InlineData(Phase.Review, SdlcJobTypes.Review)]
    [InlineData(Phase.Release, SdlcJobTypes.Release)]
    public void FR290_enqueue_on_enter_equals_the_0_6_1_job_per_phase_entry(Phase phase, string? expected) =>
        Assert.Equal(expected, StageOf(phase).EnqueueOnEnter);

    [Fact]
    public void FR288_advisory_in_specification_with_the_spec_locked_needs_spec_approval_then_GATE_PENDING()
    {
        var state = new LifecycleState(Phase.Specification, ProjectStatus.Active, Checkpoint);
        var snapshot = new LifecycleSnapshot { Mode = ApprovalMode.Advisory, TechnicalSpecificationLocked = true };
        var pending = snapshot with { PendingGates = [SdlcGates.SpecApproval] };

        var needs = PipelineStateMachine.RequestAdvance(RunStateOf(state), FactsOf(state, snapshot), Definition);
        var refused = PipelineStateMachine.RequestAdvance(RunStateOf(state), FactsOf(state, pending), Definition);

        Assert.Equal(SdlcGates.SpecApproval, needs.Value.GateRequired);
        Assert.Equal((ErrorCodes.GatePending, "A spec_approval gate is already pending."), (refused.Error!.Code, refused.Error.Message));
    }

    [Fact]
    public void FR288_the_4_52_example_fails_with_the_same_code_message_and_reasons()
    {
        var state = new LifecycleState(Phase.Planning, ProjectStatus.Active, Checkpoint);
        var snapshot = new LifecycleSnapshot { Mode = ApprovalMode.Advisory, TechnicalSpecificationLocked = true, WorkingCopyPrepared = true };

        var actual = PipelineStateMachine.RequestAdvance(RunStateOf(state), FactsOf(state, snapshot), Definition).Error!;

        Assert.Equal(["The implementation plan is not approved.", "The implementation plan has no steps."], actual.Details);
        Assert.Equal(
            (ErrorCodes.PhasePreconditionFailed, "Can't enter execution: The implementation plan is not approved. The implementation plan has no steps."),
            (actual.Code, actual.Message));
    }

    [Fact]
    public void FR287_advance_enqueues_the_job_0_6_1_queues_on_entry()
    {
        var expected = new Dictionary<Phase, string?>
        {
            [Phase.Analysis] = null,
            [Phase.Refinement] = SdlcJobTypes.SpecGeneration,
            [Phase.Specification] = SdlcJobTypes.PlanGeneration,
            [Phase.Planning] = null,
            [Phase.Execution] = SdlcJobTypes.Review,
            [Phase.Review] = SdlcJobTypes.Release,
        };
        foreach (var (phase, job) in expected)
        {
            var state = new LifecycleState(phase, Sdlc.StatusFor(phase), Checkpoint);
            var moved = Sdlc.Apply(state, SdlcTransitions.Advance(), AllTrue);

            Assert.Equal(job, moved.Value.EnqueueOnEnter);
        }
    }

    [Fact]
    public void FR287_release_completed_completes_the_run()
    {
        var state = new LifecycleState(Phase.Release, ProjectStatus.AwaitingMerge, Checkpoint);

        var moved = PipelineStateMachine.Apply(RunStateOf(state), Transition.Change("release_completed"), FactsOf(state, AllTrue), Definition).Value;

        Assert.True(moved.CompletesRun);
        Assert.Equal(GenericRunStatus.Completed, moved.Status);
    }

    [Fact]
    public void FR287_the_review_loop_is_refused_once_review_cycles_are_used_up()
    {
        var state = new LifecycleState(Phase.Review, ProjectStatus.Reviewing, Checkpoint);
        var snapshot = AllTrue with { LatestReviewDecision = ReviewDecision.ReviewRequired, EscalationOutcome = null };

        var below = PipelineStateMachine.Apply(RunStateOf(state, reviewCycle: ReviewCycles - 1), Transition.Loop("review_required"), FactsOf(state, snapshot), Definition);
        var atCap = PipelineStateMachine.Apply(RunStateOf(state, reviewCycle: ReviewCycles), Transition.Loop("review_required"), FactsOf(state, snapshot), Definition);

        Assert.Equal(("execution", "building", "Review cycle: re-entering execution"), (below.Value.To, below.Value.PackStatus, below.Value.Reason));
        Assert.Equal(["The review cycle limit of 3 is reached."], atCap.Error!.Details);
    }

    [Theory]
    [InlineData(SdlcArtifacts.ProjectRequest, StepReset.AllSteps)]
    [InlineData(SdlcArtifacts.TechnicalSpecification, StepReset.AllSteps)]
    [InlineData(SdlcArtifacts.ImplementationPlan, StepReset.StepsOfOtherPlans)]
    [InlineData(SdlcArtifacts.OptimizationPlan, StepReset.None)]
    [InlineData(SdlcArtifacts.CodebaseSummary, StepReset.None)]
    [InlineData(SdlcArtifacts.SourceItems, StepReset.None)]
    [InlineData(SdlcArtifacts.StackProfile, StepReset.AllSteps)]
    public void FR289_the_SDLC_hook_resets_the_steps_of_4_54_and_counts_them_as_produced_in_execution(string approved, StepReset reset)
    {
        Assert.Equal(reset, SdlcInvalidationHook.StepResetFor(approved));
        Assert.Equal(reset, Sdlc.Cascade(approved, Phase.Release).StepReset);
        Assert.Equal(reset == StepReset.None ? [] : ["execution"], SdlcInvalidationHook.Instance.FallbackStages(approved, []));
    }

    [Fact]
    public void FR289_the_SDLC_hook_is_registered_for_the_sdlc_pipeline() =>
        Assert.Equal(SdlcPack.PipelineId, Assert.Single(Services.GetServices<IInvalidationHook>()).PipelineId);

    [Theory]
    [InlineData(SdlcArtifacts.ProjectRequest, "specification")]
    [InlineData(SdlcArtifacts.TechnicalSpecification, "planning")]
    [InlineData(SdlcArtifacts.ImplementationPlan, "execution")]
    [InlineData(SdlcArtifacts.OptimizationPlan, null)]
    [InlineData(SdlcArtifacts.CodebaseSummary, null)]
    [InlineData(SdlcArtifacts.SourceItems, null)]
    [InlineData(SdlcArtifacts.StackProfile, "analysis")]
    public void FR289_each_4_54_row_falls_back_as_specified_from_release(string approved, string? fallback) =>
        Assert.Equal(fallback, DefinitionCascade.Effect(approved, "release", Definition, SdlcInvalidationHook.Instance).FallbackStage);

    private static RunState RunStateOf(LifecycleState state, int reviewCycle = 1) => SdlcMachine.RunStateOf(state, reviewCycle);

    /// <summary>The run facts the SDLC caller computes (<see cref="SdlcMachine.FactsOf"/>).</summary>
    private static RunFacts FactsOf(LifecycleState state, LifecycleSnapshot snapshot) => SdlcMachine.FactsOf(state, snapshot, ReviewCycles);

    private static readonly LifecycleSnapshot AllTrue = new()
    {
        ProjectRequestLocked = true,
        TechnicalSpecificationLocked = true,
        ImplementationPlanLocked = true,
        CurrentPlanStepCount = 3,
        CurrentPlanCompletedStepCount = 3,
        WorkingCopyPrepared = true,
        LatestReviewDecision = ReviewDecision.ReleaseEligible,
        LatestReviewProjectVersion = Checkpoint,
        EscalationOutcome = EscalationDecision.Accept,
        OptimizationPlanLocked = true,
        OptimizationPlanStepCount = 2,
        EntryGateApproved = true,
        StackProfileLocked = true,
        StackCompositionPinned = true,
        StackProfileGeneralist = true,
    };
}
