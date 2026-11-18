using Aide.Api.Pipelines;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;
using static Aide.UnitTests.Lifecycle.SdlcPipelineFixture;

namespace Aide.UnitTests.Lifecycle;

/// <summary>FR-291: the parity mapper, the comparer and <see cref="ParityStateMachine"/> (throw in tests, log in production).</summary>
public class LifecycleParityTests
{
    private const int Checkpoint = 4;

    private static readonly LifecycleSnapshot SpecLocked = new() { Mode = ApprovalMode.Autonomous, ProjectRequestLocked = true, TechnicalSpecificationLocked = true };

    private static readonly LifecycleSnapshot CycleDue = new()
    {
        LatestReviewDecision = ReviewDecision.ReviewRequired,
        LatestReviewProjectVersion = Checkpoint,
        OptimizationPlanLocked = true,
        OptimizationPlanStepCount = 2,
        WorkingCopyPrepared = true,
    };

    [Fact]
    public void FR291_the_mapper_maps_the_state_the_facts_and_the_review_cycles_setting()
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

        var run = LifecycleParityMapper.RunStateOf(state, reviewCycle: 2);
        var facts = LifecycleParityMapper.FactsOf(state, snapshot, reviewCycles: 5);

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
    [InlineData(TransitionKind.Advance, RunTransitionKind.Advance, null)]
    [InlineData(TransitionKind.ReviewCycle, RunTransitionKind.Loop, null)]
    [InlineData(TransitionKind.Rollback, RunTransitionKind.Rollback, null)]
    [InlineData(TransitionKind.InvalidationFallback, RunTransitionKind.Fallback, null)]
    [InlineData(TransitionKind.ReleaseNoop, RunTransitionKind.Noop, null)]
    [InlineData(TransitionKind.ReleaseCompleted, RunTransitionKind.StatusChange, "release_completed")]
    [InlineData(TransitionKind.ReleaseAwaitingMerge, RunTransitionKind.StatusChange, "release_awaiting_merge")]
    [InlineData(TransitionKind.ReleaseMergeAbandoned, RunTransitionKind.StatusChange, "release_merge_abandoned")]
    public void FR291_the_mapper_maps_each_transition_kind(TransitionKind kind, RunTransitionKind expected, string? statusChange)
    {
        var transition = LifecycleParityMapper.TransitionOf(new TransitionRequest(kind, Phase.Planning, 2, "why"))!;

        Assert.Equal(expected, transition.Kind);
        Assert.Equal(expected, LifecycleParityMapper.KindOf(kind));
        Assert.Equal(statusChange, transition.StatusChange);
        Assert.Equal("why", transition.Reason);
        Assert.Equal(statusChange is null ? "planning" : null, transition.TargetStage);
        Assert.Equal(kind == TransitionKind.Rollback ? 2 : null, transition.TargetCheckpoint);
        Assert.Equal(kind == TransitionKind.ReviewCycle ? "review_required" : null, transition.LoopOutcome);
    }

    [Fact]
    public void FR291_a_new_version_is_a_new_run_not_a_transition()
    {
        Assert.Null(LifecycleParityMapper.TransitionOf(TransitionRequest.NewVersion()));
        Assert.Null(LifecycleParityMapper.KindOf(TransitionKind.NewVersion));
    }

    [Fact]
    public void FR291_the_comparer_finds_no_difference_between_equal_results()
    {
        var state = new LifecycleState(Phase.Specification, ProjectStatus.Active, Checkpoint);

        var check = LifecycleParity.Apply(state, TransitionRequest.Advance("go"), SpecLocked, new ReviewLoopState(0, 3), Definition);

        Assert.True(check.Agrees, string.Join("; ", check.Differences));
        Assert.Equal(Phase.Planning, check.Old.Value.To);
        Assert.Contains("specification → planning/active", check.Engine, StringComparison.Ordinal);
    }

    [Fact]
    public void FR291_the_comparer_reports_stage_status_checkpoint_kind_reason_and_review_state_differences()
    {
        var old = Result.Success(new PhaseTransition(Phase.Review, Phase.Execution, ProjectStatus.Building, 4, TransitionKind.ReviewCycle, "cycle"));
        var engine = Result.Success(new RunTransition("review", "planning", "rolled_back", GenericRunStatus.Active, 3, RunTransitionKind.Rollback, "other"));

        var differences = LifecycleParityComparer.Compare(old, engine);

        Assert.Equal(
            [
                "to: old 'execution', engine 'planning'",
                "status: old 'building', engine 'rolled_back'",
                "checkpoint: old '4', engine '3'",
                "kind: old 'Loop', engine 'Rollback'",
                "reason: old 'cycle', engine 'other'",
                "clears review state: old 'False', engine 'True'",
            ],
            differences);
    }

    [Fact]
    public void FR291_the_comparer_reports_success_error_code_message_and_reasons_differences()
    {
        var refused = Result.Failure<PhaseTransition>(new DomainError(ErrorCodes.PhasePreconditionFailed, "Can't enter execution: a b") { Details = ["a", "b"] });
        var otherReasons = Result.Failure<RunTransition>(new DomainError(EngineErrorCodes.WrongPhase, "Can't enter execution: b a") { Details = ["b", "a"] });
        var applied = Result.Success(new RunTransition("planning", "execution", "building", GenericRunStatus.Active, 5, RunTransitionKind.Advance, "x"));

        Assert.Equal(
            [
                "error code: old 'PHASE_PRECONDITION_FAILED', engine 'WRONG_PHASE'",
                "message: old 'Can't enter execution: a b', engine 'Can't enter execution: b a'",
                "details: old [a | b], engine [b | a]",
            ],
            LifecycleParityComparer.Compare(refused, otherReasons));
        Assert.StartsWith("success: old refused (PHASE_PRECONDITION_FAILED", Assert.Single(LifecycleParityComparer.Compare(refused, applied)), StringComparison.Ordinal);
    }

    [Fact]
    public void FR291_the_comparer_reports_a_different_gate_needed()
    {
        var old = Result.Success(AdvanceOutcome.NeedsGate(SdlcGates.SpecApproval));
        var engine = Result.Success(RunAdvanceOutcome.NeedsGate(SdlcGates.PlanApproval));

        Assert.Equal(["gate needed: old 'spec_approval', engine 'plan_approval'"], LifecycleParityComparer.Compare(old, engine));
    }

    [Fact]
    public void FR291_the_comparer_reports_cascade_differences()
    {
        var old = new InvalidationEffect([SdlcArtifacts.OptimizationPlan], StepReset.StepsOfOtherPlans, Phase.Execution);
        var engine = new CascadeEffect(SdlcArtifacts.ImplementationPlan, [], [], null);

        Assert.Equal(
            [
                "invalidated: old [optimization_plan], engine []",
                "pack effect: old 'True', engine 'False'",
                "fallback: old 'execution', engine 'null'",
            ],
            LifecycleParityComparer.Compare(old, engine));
    }

    [Fact]
    public void FR291_the_parity_machine_returns_the_old_result_for_every_kind_of_decision()
    {
        var parity = Machine(throwOnMismatch: true, out _);
        var project = new Project { CurrentPhase = Phase.Specification, Status = ProjectStatus.Active, Checkpoint = Checkpoint, MaxReviewCycles = 3 };

        var applied = parity.Apply(project, TransitionRequest.Advance(), SpecLocked);
        var outcome = parity.RequestAdvance(project, SpecLocked with { Mode = ApprovalMode.Advisory });
        var effect = parity.CascadeEffect(project.Id, SdlcArtifacts.ProjectRequest, Phase.Review);
        var started = parity.Start(project.Id, infersStack: true, baseHasCode: true);

        Assert.Equal(Phase.Planning, applied.Value.To);
        Assert.Equal(SdlcGates.SpecApproval, outcome.Value.GateRequired);
        Assert.Equal((StepReset.AllSteps, Phase.Specification), (effect.StepReset, effect.FallbackPhase));
        Assert.Equal(Phase.Analysis, started.Value.Phase);
    }

    [Fact]
    public void FR291_a_mismatch_throws_with_the_differences_when_Throw_is_on()
    {
        var parity = Machine(throwOnMismatch: true, out var logs);

        var thrown = Assert.Throws<ParityMismatchException>(() => ApplyAtTheCap(parity, AtTheReviewCap()));

        Assert.Contains("ReviewCycle", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("success: old applied, engine refused (PHASE_PRECONDITION_FAILED: Can't enter execution: The review cycle limit of 3 is reached.)", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public void FR291_a_mismatch_logs_an_error_and_returns_the_old_result_when_Throw_is_off()
    {
        var parity = Machine(throwOnMismatch: false, out var logs);
        var project = AtTheReviewCap();

        var result = ApplyAtTheCap(parity, project);

        Assert.Equal((Phase.Execution, ProjectStatus.Building), (result.Value.To, result.Value.Status));
        var (level, message) = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains(project.Id.ToString(), message, StringComparison.Ordinal);
        Assert.Contains("review → execution/building", message, StringComparison.Ordinal);
        Assert.Contains("The review cycle limit of 3 is reached.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR291_no_valid_SDLC_definition_is_a_mismatch_logged_once_per_process()
    {
        var empty = new PipelineRegistryHost([], [], []);
        var logs = new ListLogger<ParityStateMachine>();
        var throwing = new ParityStateMachine(empty, Options.Create(new ParityOptions { Throw = true }), logs);
        var logging = new ParityStateMachine(empty, Options.Create(new ParityOptions { Throw = false }), logs);
        var project = new Project { CurrentPhase = Phase.Release, Status = ProjectStatus.Releasing, Checkpoint = Checkpoint };

        Assert.Throws<ParityMismatchException>(() => throwing.Apply(project, TransitionRequest.ReleaseCompleted(), LifecycleSnapshot.Empty));
        Assert.Equal(ProjectStatus.Released, logging.Apply(project, TransitionRequest.ReleaseCompleted(), LifecycleSnapshot.Empty).Value.Status);
        Assert.Equal(ProjectStatus.Released, logging.Apply(project, TransitionRequest.ReleaseCompleted(), LifecycleSnapshot.Empty).Value.Status);
        var (level, message) = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains("no valid SDLC definition", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR291_an_engine_exception_throws_a_ParityMismatchException_with_it_inside_when_Throw_is_on()
    {
        var parity = new ParityStateMachine(CapSettingMissing.Host, Options.Create(new ParityOptions { Throw = true }), new ListLogger<ParityStateMachine>());

        var thrown = Assert.Throws<ParityMismatchException>(() => parity.Apply(CycleReady(), TransitionRequest.ReviewCycle(), CycleDue));

        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.Contains("engine threw: Setting 'other_cycles' was not resolved", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR291_an_engine_exception_logs_an_error_and_returns_the_old_result_when_Throw_is_off()
    {
        var logs = new ListLogger<ParityStateMachine>();
        var parity = new ParityStateMachine(CapSettingMissing.Host, Options.Create(new ParityOptions { Throw = false }), logs);
        var expected = LifecycleStateMachine.Apply(ParityStateMachine.StateOf(CycleReady()), TransitionRequest.ReviewCycle(), CycleDue).Value;

        var result = parity.Apply(CycleReady(), TransitionRequest.ReviewCycle(), CycleDue);

        Assert.Equal(expected, result.Value);
        var (level, message) = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains("engine threw: Setting 'other_cycles' was not resolved", message, StringComparison.Ordinal);
        Assert.Contains("review → execution/building @4", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR291_the_mismatch_log_holds_no_free_text_reason_and_truncates_each_difference()
    {
        var parity = Machine(throwOnMismatch: false, out var logs);
        var comment = new string('c', 500);
        var project = AtTheReviewCap();

        parity.Apply(project.Id, ParityStateMachine.StateOf(project), TransitionRequest.ReviewCycle(comment), CycleDue, new ReviewLoopState(3, 3));

        var (_, message) = Assert.Single(logs.Entries);
        Assert.DoesNotContain(comment[..ParityStateMachine.LoggedValueMaxLength], message, StringComparison.Ordinal);
        Assert.Contains("(ReviewCycle)", message, StringComparison.Ordinal);
        Assert.Contains("PHASE_PRECONDITION_FAILED", message, StringComparison.Ordinal);
        Assert.Contains("review → execution/building @4", message, StringComparison.Ordinal);
        Assert.Equal(ParityStateMachine.LoggedValueMaxLength + 1, ParityStateMachine.ForLog(comment).Length);
        Assert.Equal("short", ParityStateMachine.ForLog("short"));
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
        Assert.Equal(new ReviewLoopState(cycle, expected), ParityStateMachine.LoopOf(new Project { MaxReviewCycles = max, ExtraReviewCycles = extra, ReviewCycle = cycle }));
    }

    [Fact]
    public void D_0_7_0_30_a_cycle_0_6_1_allows_after_max_review_cycles_is_lowered_agrees()
    {
        var parity = Machine(throwOnMismatch: true, out _);
        var project = AtTheReviewCap();
        project.MaxReviewCycles = 1;

        Assert.Equal(Phase.Execution, parity.Apply(project, TransitionRequest.ReviewCycle(), CycleDue).Value.To);
    }

    [Fact]
    public void FR291_parity_mode_is_registered_and_logs_by_default()
    {
        var services = new ServiceCollection().AddSdlcPipeline();

        Assert.Contains(services, d => d.ServiceType == typeof(ParityStateMachine) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.False(new ParityOptions().Throw);
    }

    /// <summary>In review with 3 cycles used.</summary>
    private static Project AtTheReviewCap() =>
        new() { CurrentPhase = Phase.Review, Status = ProjectStatus.Reviewing, Checkpoint = Checkpoint, ReviewCycle = 3, MaxReviewCycles = 3 };

    /// <summary>
    /// A real mismatch: a review cycle with the loop cap forced to the current cycle (bypassing D-0.7.0-30's setting, which
    /// never allows that), so 0.6.1 starts the cycle and the engine's backstop refuses it.
    /// </summary>
    private static Result<PhaseTransition> ApplyAtTheCap(ParityStateMachine parity, Project project) =>
        parity.Apply(project.Id, ParityStateMachine.StateOf(project), TransitionRequest.ReviewCycle(), CycleDue, new ReviewLoopState(3, 3));

    private static ParityStateMachine Machine(bool throwOnMismatch, out ListLogger<ParityStateMachine> logs)
    {
        logs = new ListLogger<ParityStateMachine>();
        return new ParityStateMachine(Services.GetRequiredService<PipelineRegistryHost>(), Options.Create(new ParityOptions { Throw = throwOnMismatch }), logs);
    }

    /// <summary>In review, cycle 1 of 3: a review cycle the old machine applies.</summary>
    private static Project CycleReady() =>
        new() { CurrentPhase = Phase.Review, Status = ProjectStatus.Reviewing, Checkpoint = Checkpoint, ReviewCycle = 1, MaxReviewCycles = 3 };

    /// <summary>
    /// The SDLC definition with the review loop capped by a setting (<c>other_cycles</c>) the parity mapper never supplies,
    /// so the engine throws on a review cycle that is otherwise allowed (the engine-exception branch).
    /// </summary>
    private sealed class CapSettingMissing : IPipelinePack
    {
        private static readonly Lazy<PipelineRegistryHost> LazyHost = new(() =>
        {
            var services = new ServiceCollection().AddPipelineRegistry().AddPipelinePack(new CapSettingMissing());
            services.AddPipelineGuard<StartOnlyGuard>(SdlcGuardNames.StartOnly);
            services.AddPipelineGuard<LeavingAnalysisGuard>(SdlcGuardNames.LeavingAnalysis);
            services.AddPipelineGuard<PlanHasStepsGuard>(SdlcGuardNames.PlanHasSteps);
            services.AddPipelineGuard<WorkingCopyPreparedGuard>(SdlcGuardNames.WorkingCopyPrepared);
            services.AddPipelineGuard<PlanStepsCompletedGuard>(SdlcGuardNames.PlanStepsCompleted);
            services.AddPipelineGuard<ReleaseEligibleGuard>(SdlcGuardNames.ReleaseEligible);
            services.AddPipelineGuard<ReviewCycleReadyGuard>(SdlcGuardNames.ReviewCycleReady);
            var host = services.BuildServiceProvider().GetRequiredService<PipelineRegistryHost>();
            Assert.True(host.Registry!.TryGet(SdlcPack.PipelineId, out var loaded) && loaded.IsValid, string.Join("; ", host.Registry.Errors.Select(e => e.Error.ToString())));
            return host;
        });

        public static PipelineRegistryHost Host => LazyHost.Value;

        public string Id => SdlcPack.PackId;

        public void Register(IPackRegistry registry)
        {
            using var reader = new StreamReader(SdlcPack.OpenDefinition());
            var yaml = reader.ReadToEnd()
                .Replace("settings: [review_cycles]", "settings: [other_cycles]", StringComparison.Ordinal)
                .Replace("setting: review_cycles", "setting: other_cycles", StringComparison.Ordinal);
            registry.AddDefinition(SdlcPack.DefinitionName, yaml);
            foreach (var guard in SdlcGuardNames.All)
            {
                registry.AddGuard(guard);
            }

            registry.AddSetting("other_cycles");
            foreach (var jobType in SdlcJobTypes.All)
            {
                registry.AddJobType(jobType);
            }

            registry.AddArtifactProducer(SdlcArtifacts.SourceItems);
            registry.AddArtifactProducer(SdlcArtifacts.CodebaseSummary);
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
