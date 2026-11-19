using Aide.Domain.Common;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;
using static Aide.UnitTests.Lifecycle.SdlcPipelineFixture;

namespace Aide.UnitTests.Lifecycle;

/// <summary>
/// FR-290, §4.52: a stage's <c>requires:</c> (built-ins and SDLC guards, in declared order) and its gate, composed here,
/// give exactly the engine's <see cref="SdlcMachine.UnmetEntryConditions"/> for every phase, current phase and approval
/// mode, over the "all false" and "all true" snapshots and each fact varied one at a time from both. The review loop's
/// guard gives the review cycle's refusal reasons. (0.6.1's own reasons are pinned by <see cref="SdlcLifecycleTransitionTests"/>.)
/// </summary>
public class SdlcGuardParityTests
{
    private const int Checkpoint = 4;

    public static TheoryData<Phase> Phases => [.. Sdlc.All];

    [Theory]
    [MemberData(nameof(Phases))]
    public void FR290_stage_requirements_and_gate_equal_the_engine_UnmetEntryConditions(Phase entering)
    {
        var definition = Definition;
        var stage = StageOf(entering);
        var mismatches = new List<string>();

        foreach (var current in Sdlc.All)
        {
            var state = new LifecycleState(current, Sdlc.StatusFor(current), Checkpoint);
            foreach (var (label, snapshot) in Snapshots())
            {
                foreach (var includeGate in new[] { false, true })
                {
                    var expected = Sdlc.UnmetEntryConditions(state, entering, snapshot, includeGate);
                    var actual = Compose(definition, stage, state, snapshot, includeGate);
                    if (!expected.SequenceEqual(actual))
                    {
                        mismatches.Add($"from {current.ToWire()}, {label}, gate {includeGate}: expected [{string.Join(" | ", expected)}], got [{string.Join(" | ", actual)}]");
                    }
                }
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void FR290_the_review_cycle_guard_equals_the_ReviewCycle_unmet_reasons()
    {
        var state = new LifecycleState(Phase.Review, ProjectStatus.Reviewing, Checkpoint);
        var mismatches = new List<string>();

        foreach (var (label, snapshot) in Snapshots())
        {
            var applied = Sdlc.Apply(state, SdlcTransitions.ReviewCycle(), snapshot);
            var expected = applied.IsSuccess ? [] : applied.Error!.Details;
            if (!applied.IsSuccess)
            {
                Assert.Equal(ErrorCodes.PhasePreconditionFailed, applied.Error!.Code);
            }

            var actual = SdlcGuards.Evaluate(SdlcGuardNames.ReviewCycleReady, state, snapshot);
            if (!expected.SequenceEqual(actual))
            {
                mismatches.Add($"{label}: expected [{string.Join(" | ", expected)}], got [{string.Join(" | ", actual)}]");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void FR290_the_parity_example_of_4_52_lists_both_execution_reasons()
    {
        var snapshot = new LifecycleSnapshot { TechnicalSpecificationLocked = true, WorkingCopyPrepared = true };
        var state = new LifecycleState(Phase.Planning, ProjectStatus.Active, Checkpoint);

        Assert.Equal(
            ["The implementation plan is not approved.", "The implementation plan has no steps."],
            Compose(Definition, StageOf(Phase.Execution), state, snapshot, includeGate: false));
    }

    [Fact]
    public async Task FR290_the_host_guards_read_the_lifecycle_fact_and_return_the_SDLC_reasons()
    {
        using var scope = Services.CreateScope();
        foreach (var current in Sdlc.All)
        {
            var state = new LifecycleState(current, Sdlc.StatusFor(current), Checkpoint);
            foreach (var (label, snapshot) in new[] { ("all false", AllFalse), ("all true", AllTrue) })
            {
                var facts = FactBag.Empty.With(new SdlcLifecycleFacts(state, snapshot));
                foreach (var name in SdlcGuardNames.All)
                {
                    var guard = scope.ServiceProvider.GetPipelineGuard(name);
                    Assert.NotNull(guard);
                    var context = new GuardContext(Guid.NewGuid(), Guid.NewGuid(), current.ToWire(), snapshot.Mode, facts);
                    var result = await guard.EvaluateAsync(context, TestContext.Current.CancellationToken);

                    var expected = SdlcGuards.Evaluate(name, state, snapshot);
                    Assert.True(expected.SequenceEqual(result.Reasons), $"{name} from {current.ToWire()}, {label}");
                    Assert.Equal(expected.Count == 0, result.Ok);
                }
            }
        }
    }

    [Fact]
    public async Task FR290_a_host_guard_without_the_lifecycle_fact_fails_loudly()
    {
        using var scope = Services.CreateScope();
        var guard = scope.ServiceProvider.GetPipelineGuard(SdlcGuardNames.PlanHasSteps)!;
        var context = new GuardContext(Guid.NewGuid(), Guid.NewGuid(), "execution", ApprovalMode.Strict, FactBag.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.EvaluateAsync(context, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, "refinement")]
    [InlineData(true, "analysis")]
    public async Task FR290_the_start_selector_picks_analysis_for_a_version_that_infers_its_stack(bool infersStack, string expected)
    {
        using var scope = Services.CreateScope();
        var selector = scope.ServiceProvider.GetRequiredService<IRunStartSelector>();
        var facts = FactBag.Empty.With(new SdlcLifecycleFacts(LifecycleState.Initial, new LifecycleSnapshot { InfersStack = infersStack }));

        var start = await selector.SelectStartAsync(new RunStartContext(Guid.NewGuid(), SdlcPack.PipelineId, Definition.Start, facts), TestContext.Current.CancellationToken);

        Assert.Equal(SdlcPack.PipelineId, selector.PipelineId);
        Assert.Equal(expected, start);
    }

    [Fact]
    public async Task FR290_the_start_selector_without_the_lifecycle_fact_fails_loudly()
    {
        using var scope = Services.CreateScope();
        var selector = scope.ServiceProvider.GetRequiredService<IRunStartSelector>();
        var context = new RunStartContext(Guid.NewGuid(), SdlcPack.PipelineId, Definition.Start, FactBag.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(() => selector.SelectStartAsync(context, TestContext.Current.CancellationToken));
    }

    /// <summary>§4.52: requirements in declared order (built-in fixed messages, guard reasons), then the entry gate.</summary>
    private static List<string> Compose(PipelineDefinition definition, StageDefinition stage, LifecycleState state, LifecycleSnapshot snapshot, bool includeGate)
    {
        var reasons = new List<string>();
        foreach (var requirement in stage.Requires)
        {
            if (requirement is GuardRequirement guard)
            {
                reasons.AddRange(SdlcGuards.Evaluate(guard.Guard, state, snapshot));
            }
            else if (BuiltInRequirements.Unmet(definition, requirement, Locked(snapshot), snapshot.PendingGates) is { } reason)
            {
                reasons.Add(reason);
            }
        }

        if (includeGate && BuiltInRequirements.RequiredGate(stage, snapshot.Mode) is { } gate && !snapshot.EntryGateApproved)
        {
            reasons.Add(BuiltInRequirements.GateNotApproved(gate));
        }

        return reasons;
    }

    /// <summary>The artifact keys whose latest version the snapshot reports locked.</summary>
    private static List<string> Locked(LifecycleSnapshot s) =>
    [
        .. new (bool Locked, string Key)[]
        {
            (s.ProjectRequestLocked, SdlcArtifacts.ProjectRequest),
            (s.TechnicalSpecificationLocked, SdlcArtifacts.TechnicalSpecification),
            (s.ImplementationPlanLocked, SdlcArtifacts.ImplementationPlan),
            (s.StackProfileLocked, SdlcArtifacts.StackProfile),
        }.Where(x => x.Locked).Select(x => x.Key),
    ];

    private static readonly LifecycleSnapshot AllFalse = new();

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
        DevelopmentVersionExists = true,
        StackProfileLocked = true,
        StackCompositionPinned = true,
        StackProfileGeneralist = true,
        InfersStack = true,
        BaseHasCode = true,
    };

    /// <summary>"All false" and "all true", each with every fact varied one at a time, in every approval mode.</summary>
    private static IEnumerable<(string Label, LifecycleSnapshot Snapshot)> Snapshots()
    {
        foreach (var (baseLabel, start) in new[] { ("all false", AllFalse), ("all true", AllTrue) })
        {
            foreach (var mode in Enum.GetValues<ApprovalMode>())
            {
                var basis = start with { Mode = mode };
                var prefix = $"{baseLabel}, {mode.ToWire()}";
                yield return (prefix, basis);
                foreach (var (label, vary) in Variations())
                {
                    yield return ($"{prefix}, {label}", vary(basis));
                }
            }
        }
    }

    private static IEnumerable<(string Label, Func<LifecycleSnapshot, LifecycleSnapshot> Vary)> Variations()
    {
        yield return ("toggle project request", s => s with { ProjectRequestLocked = !s.ProjectRequestLocked });
        yield return ("toggle spec", s => s with { TechnicalSpecificationLocked = !s.TechnicalSpecificationLocked });
        yield return ("toggle plan", s => s with { ImplementationPlanLocked = !s.ImplementationPlanLocked });
        yield return ("toggle working copy", s => s with { WorkingCopyPrepared = !s.WorkingCopyPrepared });
        yield return ("toggle optimization plan", s => s with { OptimizationPlanLocked = !s.OptimizationPlanLocked });
        yield return ("toggle entry gate", s => s with { EntryGateApproved = !s.EntryGateApproved });
        yield return ("toggle version in development", s => s with { DevelopmentVersionExists = !s.DevelopmentVersionExists });
        yield return ("toggle stack profile", s => s with { StackProfileLocked = !s.StackProfileLocked });
        yield return ("toggle composition pinned", s => s with { StackCompositionPinned = !s.StackCompositionPinned });
        yield return ("toggle generalist", s => s with { StackProfileGeneralist = !s.StackProfileGeneralist });
        yield return ("toggle infers stack", s => s with { InfersStack = !s.InfersStack });
        yield return ("toggle base has code", s => s with { BaseHasCode = !s.BaseHasCode });

        foreach (var (total, completed) in new[] { (0, 0), (1, 0), (3, 2), (3, 3), (2, 3) })
        {
            yield return ($"steps {completed}/{total}", s => s with { CurrentPlanStepCount = total, CurrentPlanCompletedStepCount = completed });
        }

        foreach (var left in new[] { 0, 1 })
        {
            yield return ($"optimization steps left {left}", s => s with { OptimizationPlanStepCount = left });
        }

        foreach (var decision in new ReviewDecision?[] { null, ReviewDecision.ReleaseEligible, ReviewDecision.ReviewRequired, ReviewDecision.EscalationRequired })
        {
            foreach (var escalation in new EscalationDecision?[] { null, EscalationDecision.Continue, EscalationDecision.Rollback, EscalationDecision.Accept })
            {
                foreach (var reportCheckpoint in new int?[] { null, Checkpoint, Checkpoint - 1 })
                {
                    yield return (
                        $"review {decision?.ToWire() ?? "none"}/{escalation?.ToWire() ?? "none"} at {reportCheckpoint?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}",
                        s => s with { LatestReviewDecision = decision, EscalationOutcome = escalation, LatestReviewProjectVersion = reportCheckpoint });
                }
            }
        }

        string[][] pending =
        [
            [],
            [SdlcGates.PreRelease],
            [EngineGates.Escalation],
            [SdlcGates.SpecApproval, SdlcGates.PreRelease, SdlcGates.SpecApproval],
            [SdlcGates.PlanApproval, EngineGates.Escalation],
        ];
        foreach (var gates in pending)
        {
            yield return ($"pending [{string.Join(", ", gates)}]", s => s with { PendingGates = gates });
        }
    }
}
