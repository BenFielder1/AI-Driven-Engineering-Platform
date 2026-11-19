using Aide.Api.Pipelines;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;
using static Aide.UnitTests.Lifecycle.SdlcPipelineFixture;

namespace Aide.UnitTests.Lifecycle;

/// <summary>FR-292: the SDLC lifecycle service and the mapping to the engine's run state and facts (§4.51).</summary>
public class SdlcLifecycleTests
{
    private const int Checkpoint = 4;

    private static readonly LifecycleSnapshot CycleDue = new()
    {
        LatestReviewDecision = ReviewDecision.ReviewRequired,
        LatestReviewProjectVersion = Checkpoint,
        OptimizationPlanLocked = true,
        OptimizationPlanStepCount = 2,
        WorkingCopyPrepared = true,
    };

    [Fact]
    public void FR292_the_mapper_maps_the_state_the_facts_and_the_review_cycles_setting()
    {
        var state = new LifecycleState(Phase.Execution, ProjectStatus.RolledBack, Checkpoint);
        var snapshot = new LifecycleSnapshot
        {
            Mode = ApprovalMode.Strict,
            TechnicalSpecificationLocked = true,
            StackProfileLocked = true,
            EntryGateApproved = true,
            PendingGates = [SdlcGates.PreRelease],
        };

        var run = SdlcMachine.RunStateOf(state, reviewCycle: 2);
        var facts = SdlcMachine.FactsOf(state, snapshot, reviewCycles: 5);

        Assert.Equal(("execution", "rolled_back", Checkpoint, 2), (run.Stage, run.PackStatus, run.Checkpoint, run.IterationsOf("review")));
        Assert.Equal(ApprovalMode.Strict, facts.Mode);
        Assert.Equal([SdlcArtifacts.TechnicalSpecification, SdlcArtifacts.StackProfile], facts.LockedArtifacts);
        Assert.Equal([SdlcGates.PreRelease], facts.PendingGates);
        Assert.True(facts.EntryGateApproved);
        Assert.Equal(5, facts.Settings[SdlcPack.ReviewCycles]);
        Assert.Equal(SdlcGuardNames.All.Order(StringComparer.Ordinal), facts.Guards.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["The implementation plan has no steps."], facts.Guards[SdlcGuardNames.PlanHasSteps].Reasons);
        Assert.True(facts.Guards[SdlcGuardNames.LeavingAnalysis].Ok);
    }

    [Theory]
    [InlineData(3, 0, 1, 3)]
    [InlineData(3, 0, 3, 4)]
    [InlineData(1, 0, 3, 4)]
    [InlineData(3, 1, 3, 4)]
    [InlineData(3, 2, 3, 5)]
    [InlineData(2, 0, 0, 2)]
    public void D_0_7_0_30_review_cycles_is_max_plus_extra_but_never_below_the_current_cycle_plus_one(int max, int extra, int cycle, int expected)
    {
        Assert.Equal(expected, SdlcReviewLoop.ReviewCycles(max, extra, cycle));
        Assert.Equal(new ReviewLoopState(cycle, expected), SdlcLifecycle.LoopOf(new Project { MaxReviewCycles = max, ExtraReviewCycles = extra, ReviewCycle = cycle }));
    }

    [Fact]
    public void D_0_7_0_30_a_cycle_0_6_1_allows_after_max_review_cycles_is_lowered_is_applied()
    {
        var lifecycle = Lifecycle();
        var project = new Project { CurrentPhase = Phase.Review, Status = ProjectStatus.Reviewing, Checkpoint = Checkpoint, ReviewCycle = 3, MaxReviewCycles = 1 };

        var cycle = SdlcLifecycle.Apply(lifecycle.OnCurrent(SdlcLifecycle.CopyOf(project)), project, SdlcTransitions.ReviewCycle(), CycleDue);

        Assert.True(cycle.IsSuccess, cycle.Error?.Message);
        Assert.Equal((Phase.Execution, ProjectStatus.Building, Checkpoint), (cycle.Value.To, cycle.Value.Status, cycle.Value.Version));
    }

    [Fact]
    public void FR292_the_lifecycle_service_is_registered_as_a_singleton()
    {
        var services = new ServiceCollection().AddSdlcPipeline();

        Assert.Contains(services, d => d.ServiceType == typeof(SdlcLifecycle) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Equal(SdlcPack.PipelineId, Services.GetRequiredService<SdlcLifecycle>().Definition.Pipeline);
    }

    [Fact]
    public void FR292_with_no_valid_SDLC_definition_every_decision_fails_closed()
    {
        var lifecycle = new SdlcLifecycle(
            new ServiceCollection().AddPipelineRegistry().BuildServiceProvider().GetRequiredService<PipelineRegistryHost>(),
            new PipelineDefinitionCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SdlcLifecycle>.Instance);

        Assert.Throws<InvalidOperationException>(() => lifecycle.Start(infersStack: false, baseHasCode: true));
    }

    [Fact]
    public void FR292_a_new_project_starts_where_the_engine_starts_the_run()
    {
        var lifecycle = Lifecycle();

        Assert.Equal(new LifecycleState(Phase.Refinement, ProjectStatus.Active, 1), lifecycle.Start(infersStack: false, baseHasCode: false).Value);
        Assert.Equal(new LifecycleState(Phase.Analysis, ProjectStatus.Active, 1), lifecycle.Start(infersStack: true, baseHasCode: true).Value);
    }

    private static SdlcLifecycle Lifecycle() => Services.GetRequiredService<SdlcLifecycle>();
}
