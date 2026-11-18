using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Jobs;
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
/// FR-287–FR-289 with the SDLC definition: spot checks of the engine against 0.6.1's <see cref="LifecycleStateMachine"/>
/// and <see cref="InvalidationCascade"/> (the exhaustive §4.56 parity test is <see cref="LifecycleParityCombinationTests"/>), and FR-290's
/// <c>enqueue_on_enter</c> against <see cref="PhaseJobs"/>.
/// </summary>
public class SdlcEngineSpotCheckTests
{
    private const int Checkpoint = 4;
    private const int ReviewCycles = 3;

    public static TheoryData<Phase> Phases => [.. PhaseOrder.All];

    [Fact]
    public void FR287_engine_error_codes_equal_the_host_error_table()
    {
        Assert.Equal(ErrorCodes.PhasePreconditionFailed, EngineErrorCodes.PhasePreconditionFailed);
        Assert.Equal(ErrorCodes.GatePending, EngineErrorCodes.GatePending);
        Assert.Equal(ErrorCodes.WrongPhase, EngineErrorCodes.WrongPhase);
        Assert.Equal(ErrorCodes.InvalidTargetVersion, EngineErrorCodes.InvalidTargetVersion);
    }

    [Theory]
    [MemberData(nameof(Phases))]
    public void FR290_enqueue_on_enter_equals_PhaseJobs_OnEnter(Phase phase) =>
        Assert.Equal(PhaseJobs.OnEnter(phase), StageOf(phase).EnqueueOnEnter);

    [Fact]
    public void FR288_advisory_in_specification_with_the_spec_locked_needs_spec_approval_then_GATE_PENDING()
    {
        var state = new LifecycleState(Phase.Specification, ProjectStatus.Active, Checkpoint);
        var snapshot = new LifecycleSnapshot { Mode = ApprovalMode.Advisory, TechnicalSpecificationLocked = true };
        var pending = snapshot with { PendingGates = [SdlcGates.SpecApproval] };

        var needs = PipelineStateMachine.RequestAdvance(RunStateOf(state), FactsOf(state, snapshot), Definition);
        var refused = PipelineStateMachine.RequestAdvance(RunStateOf(state), FactsOf(state, pending), Definition);
        var old = LifecycleStateMachine.RequestAdvance(state, pending);

        Assert.Equal(LifecycleStateMachine.RequestAdvance(state, snapshot).Value.GateRequired, needs.Value.GateRequired);
        Assert.Equal(SdlcGates.SpecApproval, needs.Value.GateRequired);
        Assert.Equal((old.Error!.Code, old.Error.Message), (refused.Error!.Code, refused.Error.Message));
    }

    [Fact]
    public void FR288_the_4_52_example_fails_with_the_same_code_message_and_reasons()
    {
        var state = new LifecycleState(Phase.Planning, ProjectStatus.Active, Checkpoint);
        var snapshot = new LifecycleSnapshot { Mode = ApprovalMode.Advisory, TechnicalSpecificationLocked = true, WorkingCopyPrepared = true };

        var expected = LifecycleStateMachine.RequestAdvance(state, snapshot).Error!;
        var actual = PipelineStateMachine.RequestAdvance(RunStateOf(state), FactsOf(state, snapshot), Definition).Error!;

        Assert.Equal(["The implementation plan is not approved.", "The implementation plan has no steps."], actual.Details);
        AssertSameError(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Phases))]
    public void FR287_advance_from_every_phase_with_all_facts_true_matches_0_6_1(Phase phase)
    {
        var state = new LifecycleState(phase, PhaseOrder.StatusFor(phase), Checkpoint);
        foreach (var mode in Enum.GetValues<ApprovalMode>())
        {
            var snapshot = AllTrue with { Mode = mode };
            Compare(state, TransitionRequest.Advance(), snapshot);
            Compare(state, TransitionRequest.Advance() with { Reason = "given" }, snapshot);
            Compare(state, TransitionRequest.Advance(), snapshot with { EntryGateApproved = false });
            Compare(state, TransitionRequest.Advance(), LifecycleSnapshot.Empty with { Mode = mode });
            Compare(state, TransitionRequest.AdvanceTo(Phase.Release), snapshot);
        }
    }

    [Fact]
    public void FR287_advance_enqueues_the_job_0_6_1_queues_on_entry()
    {
        foreach (var phase in PhaseOrder.All.Where(p => PhaseOrder.Next(p) is not null))
        {
            var state = new LifecycleState(phase, PhaseOrder.StatusFor(phase), Checkpoint);
            var moved = PipelineStateMachine.Apply(RunStateOf(state), Transition.Advance(), FactsOf(state, AllTrue), Definition);

            Assert.Equal(PhaseJobs.OnEnter(PhaseOrder.Next(phase)!.Value), moved.Value.EnqueueOnEnter);
        }
    }

    [Theory]
    [MemberData(nameof(Phases))]
    public void FR287_review_cycle_rollback_fallback_and_noop_from_every_phase_match_0_6_1(Phase phase)
    {
        var state = new LifecycleState(phase, PhaseOrder.StatusFor(phase), Checkpoint);
        var cycleDue = AllTrue with { LatestReviewDecision = ReviewDecision.ReviewRequired, EscalationOutcome = null };
        foreach (var snapshot in new[] { cycleDue, AllTrue, LifecycleSnapshot.Empty, cycleDue with { OptimizationPlanStepCount = 0, WorkingCopyPrepared = false } })
        {
            Compare(state, TransitionRequest.ReviewCycle(), snapshot);
        }

        foreach (var target in PhaseOrder.All)
        {
            foreach (var checkpoint in new[] { 0, 1, Checkpoint - 1, Checkpoint, Checkpoint + 1 })
            {
                Compare(state, TransitionRequest.Rollback(target, checkpoint), AllTrue);
            }

            Compare(state, TransitionRequest.Fallback(target), AllTrue);
        }

        Compare(state, TransitionRequest.ReleaseNoop(), AllTrue);
        Compare(state, TransitionRequest.ReleaseNoop("given"), AllTrue);
    }

    [Theory]
    [MemberData(nameof(Phases))]
    public void FR287_release_status_changes_from_every_phase_and_status_match_0_6_1(Phase phase)
    {
        foreach (var status in Enum.GetValues<ProjectStatus>())
        {
            var state = new LifecycleState(phase, status, Checkpoint);
            Compare(state, TransitionRequest.ReleaseCompleted(), AllTrue);
            Compare(state, TransitionRequest.ReleaseAwaitingMerge(), AllTrue);
            Compare(state, TransitionRequest.ReleaseMergeAbandoned(), AllTrue);
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

    [Fact]
    public void FR289_the_SDLC_cascade_with_the_SDLC_hook_equals_InvalidationCascade_for_every_artifact_and_phase()
    {
        var mismatches = new List<string>();

        foreach (var approved in SdlcArtifacts.All)
        {
            foreach (var current in PhaseOrder.All)
            {
                var check = LifecycleParity.CascadeEffect(approved, current, Definition);
                if (!check.Agrees)
                {
                    mismatches.Add($"{approved} in {current.ToWire()}: {string.Join("; ", check.Differences)}");
                }
            }
        }

        Assert.Empty(mismatches);
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
        Assert.Equal(InvalidationCascade.Effect(approved, Phase.Release).StepReset, SdlcInvalidationHook.StepResetFor(approved));
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

    /// <summary>Runs both machines and asserts the same outcome: success, code, message, reasons, stage, status,
    /// checkpoint and reason.</summary>
    private static void Compare(LifecycleState state, TransitionRequest request, LifecycleSnapshot snapshot)
    {
        var label = $"{request.Kind} {request.TargetPhase?.ToWire()} {request.TargetVersion} from {state.Phase.ToWire()}/{state.Status.ToWire()} ({snapshot.Mode.ToWire()})";
        var expected = LifecycleStateMachine.Apply(state, request, snapshot);
        var actual = PipelineStateMachine.Apply(RunStateOf(state), TransitionOf(request), FactsOf(state, snapshot), Definition);

        Assert.True(expected.IsSuccess == actual.IsSuccess, $"{label}: {expected.Error?.Message} vs {actual.Error?.Message}");
        if (!expected.IsSuccess)
        {
            AssertSameError(expected.Error!, actual.Error!, label);
            return;
        }

        var (old, now) = (expected.Value, actual.Value);
        Assert.True(
            (old.From.ToWire(), old.To.ToWire(), old.Status.ToWire(), old.Version, old.Reason) == (now.From, now.To, now.PackStatus, now.Checkpoint, now.Reason),
            $"{label}: {old} vs {now}");
        Assert.True(old.ClearsReviewState == now.ClearsLoopState, label);
    }

    private static void AssertSameError(DomainError expected, DomainError actual, string label = "")
    {
        Assert.True(expected.Code == actual.Code, $"{label}: {expected.Code} vs {actual.Code}");
        Assert.True(expected.Message == actual.Message, $"{label}: '{expected.Message}' vs '{actual.Message}'");
        Assert.True(expected.Details.SequenceEqual(actual.Details), label);
    }

    private static RunState RunStateOf(LifecycleState state, int reviewCycle = 1) => LifecycleParityMapper.RunStateOf(state, reviewCycle);

    /// <summary>The run facts the SDLC caller computes (<see cref="LifecycleParityMapper.FactsOf"/>).</summary>
    private static RunFacts FactsOf(LifecycleState state, LifecycleSnapshot snapshot) => LifecycleParityMapper.FactsOf(state, snapshot, ReviewCycles);

    /// <summary>0.6.1's request as the engine's transition (<see cref="LifecycleParityMapper.TransitionOf"/>, §4.51).</summary>
    private static Transition TransitionOf(TransitionRequest request) =>
        LifecycleParityMapper.TransitionOf(request) ?? throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "A new version is a new run, not a transition.");

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
