using Aide.Domain.Artifacts;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;
using static Aide.UnitTests.Lifecycle.SdlcPipelineFixture;

namespace Aide.UnitTests.Lifecycle;

/// <summary>FR-290 (FR-278, FR-283, FR-285, FR-286): <c>sdlc.pipeline.yaml</c> loads, validates and equals 0.6.1's lifecycle tables.</summary>
public class SdlcPipelineDefinitionTests
{
    public static TheoryData<Phase> Phases => [.. Sdlc.All];

    [Fact]
    public void FR290_the_SDLC_definition_loads_and_validates_with_no_errors()
    {
        var registry = Registry;

        Assert.Empty(registry.Errors.Select(e => e.Error.ToString()));
        var loaded = Assert.Single(registry.Definitions);
        Assert.True(loaded.IsValid);
        Assert.Equal((SdlcPack.PackId, SdlcPack.DefinitionName), (loaded.PackId, loaded.Name));
        Assert.Equal(SdlcPack.PipelineId, loaded.Definition!.Pipeline);
        Assert.Matches("^[0-9a-f]{64}$", loaded.Hash);
    }

    [Fact]
    public async Task FR290_the_readiness_check_is_healthy_with_the_shipped_definition()
    {
        var check = Services.GetRequiredService<PipelineDefinitionsHealthCheck>();

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void FR290_the_host_binds_every_guard_the_pack_registers()
    {
        var registry = Registry;
        var packGuards = Assert.Single(registry.Packs).Guards;

        Assert.Equal(SdlcGuardNames.All.Order(StringComparer.Ordinal), packGuards.Order(StringComparer.Ordinal));
        Assert.All(SdlcGuardNames.All, name => Assert.NotNull(registry.ImplementationOf(PipelineComponentKind.Guard, name)));
    }

    [Fact]
    public void FR290_stage_order_equals_the_0_6_1_phase_order() =>
        Assert.Equal(["analysis", "refinement", "specification", "planning", "execution", "review", "release"], Definition.Stages.Select(s => s.Id));

    [Fact]
    public void FR293_the_Phase_order_is_the_definition_stage_order() =>
        Assert.Equal(Enum.GetValues<Phase>().Order(), Sdlc.All);

    [Fact]
    public void FR290_stage_titles_are_the_phase_names() =>
        Assert.Equal(
            ["Analysis", "Refinement", "Specification", "Planning", "Execution", "Review", "Release"],
            Definition.Stages.Select(s => s.Title));

    [Theory]
    [InlineData(Phase.Analysis, ProjectStatus.Active)]
    [InlineData(Phase.Refinement, ProjectStatus.Active)]
    [InlineData(Phase.Specification, ProjectStatus.Active)]
    [InlineData(Phase.Planning, ProjectStatus.Active)]
    [InlineData(Phase.Execution, ProjectStatus.Building)]
    [InlineData(Phase.Review, ProjectStatus.Reviewing)]
    [InlineData(Phase.Release, ProjectStatus.Releasing)]
    public void FR290_each_stage_status_equals_the_0_6_1_status_for_its_phase(Phase phase, ProjectStatus expected) =>
        Assert.Equal(expected.ToWire(), StageOf(phase).Status);

    [Theory]
    [InlineData(Phase.Analysis, Phase.Refinement)]
    [InlineData(Phase.Refinement, Phase.Specification)]
    [InlineData(Phase.Specification, Phase.Planning)]
    [InlineData(Phase.Planning, Phase.Execution)]
    [InlineData(Phase.Execution, Phase.Review)]
    [InlineData(Phase.Review, Phase.Release)]
    [InlineData(Phase.Release, null)]
    public void FR290_the_linear_successor_equals_the_0_6_1_next_phase(Phase phase, Phase? expected) =>
        Assert.Equal(expected?.ToWire(), Definition.SuccessorOf(phase.ToWire()));

    [Fact]
    public void FR290_the_entry_gate_for_every_stage_and_approval_mode_equals_the_0_6_1_required_gate()
    {
        foreach (var phase in Sdlc.All)
        {
            foreach (var mode in Enum.GetValues<ApprovalMode>())
            {
                // 0.6.1's table (§4.2): spec_approval into planning (strict, advisory), plan_approval into execution
                // (strict), pre_release into release (every mode).
                string? expected = (phase, mode) switch
                {
                    (Phase.Planning, ApprovalMode.Strict or ApprovalMode.Advisory) => SdlcGates.SpecApproval,
                    (Phase.Execution, ApprovalMode.Strict) => SdlcGates.PlanApproval,
                    (Phase.Release, _) => SdlcGates.PreRelease,
                    _ => null,
                };
                Assert.True(
                    expected == BuiltInRequirements.RequiredGate(StageOf(phase), mode) && expected == Sdlc.RequiredGate(mode, phase),
                    $"{phase.ToWire()} in {mode.ToWire()}");
            }
        }
    }

    [Fact]
    public void FR290_each_gate_guards_the_0_6_1_entry_and_approves_the_0_6_1_artifact_with_approval_links()
    {
        var gated = Definition.Stages.Where(s => s.Gate is not null).ToList();

        Assert.Equal([SdlcGates.SpecApproval, SdlcGates.PlanApproval, SdlcGates.PreRelease], gated.Select(s => s.Gate!.Type));
        Assert.All(gated, stage =>
        {
            var gate = stage.Gate!;
            var (entry, artifact) = gate.Type switch
            {
                SdlcGates.SpecApproval => (Phase.Planning, SdlcArtifacts.TechnicalSpecification),
                SdlcGates.PlanApproval => (Phase.Execution, SdlcArtifacts.ImplementationPlan),
                _ => (Phase.Release, (string?)null),
            };
            Assert.Equal(entry.ToWire(), stage.Id);
            Assert.Equal(artifact, gate.Approves);
            Assert.True(gate.ApprovalLinks);
            Assert.Equal(
                ((Phase?)entry, artifact, true),
                (SdlcStages.GuardedEntry(Definition, gate.Type), SdlcStages.GatedArtifact(Definition, gate.Type), SdlcStages.SupportsApprovalLinks(Definition, gate.Type)));
        });
    }

    [Fact]
    public void FR290_start_stages_equal_the_0_6_1_start_phases()
    {
        var start = Definition.Start;

        Assert.Equal(["refinement", "analysis"], start);
        Assert.Equal([SdlcGuards.StartStage(infersStack: false), SdlcGuards.StartStage(infersStack: true)], start);
        Assert.Equal(Sdlc.Start(infersStack: false, baseHasCode: true).Value.Phase.ToWire(), start[0]);
        Assert.Equal(Sdlc.Start(infersStack: false, baseHasCode: false).Value.Phase.ToWire(), start[0]);
        var inferred = Sdlc.Start(infersStack: true, baseHasCode: true).Value;
        Assert.Equal(inferred.Phase.ToWire(), start[1]);
        Assert.Equal(inferred.Status.ToWire(), StageOf(inferred.Phase).Status);
    }

    [Theory]
    [MemberData(nameof(Phases))]
    public void FR290_agent_stages_run_their_agent_job_and_the_entry_job_where_0_6_1_queues_one(Phase phase)
    {
        var stage = StageOf(phase);
        if (phase is Phase.Execution or Phase.Release)
        {
            Assert.Equal(StageKind.Manual, stage.Kind);
            return;
        }

        Assert.Equal(StageKind.Agent, stage.Kind);
        Assert.Equal(SdlcJobTypes.ForAgent(stage.Agent!.Key), stage.Agent.JobType);
        Assert.Equal(SdlcArtifactRules.ProducingAgent(stage.Output!), stage.Agent.Key);
        Assert.Equal(phase, SdlcArtifactRules.ProducingPhase(stage.Output!));
        string? onEnterIn061 = phase switch
        {
            Phase.Specification => SdlcJobTypes.SpecGeneration,
            Phase.Planning => SdlcJobTypes.PlanGeneration,
            Phase.Review => SdlcJobTypes.Review,
            _ => null,
        };
        if (onEnterIn061 is { } onEnter)
        {
            Assert.Equal(onEnter, stage.Agent.JobType);
        }
    }

    [Fact]
    public void FR290_artifacts_are_the_SDLC_keys_with_their_titles_approval_and_cascade()
    {
        var artifacts = Definition.Artifacts;

        Assert.Equal(SdlcArtifacts.All.Order(StringComparer.Ordinal), artifacts.Select(a => a.Key).Order(StringComparer.Ordinal));
        Assert.Equal("Technical specification", Definition.FindArtifact(SdlcArtifacts.TechnicalSpecification)!.Title);
        Assert.Equal("Optimization plan", Definition.FindArtifact(SdlcArtifacts.OptimizationPlan)!.Title);
        Assert.All(artifacts, a =>
        {
            Assert.Equal(CodebaseSummaryRules.IsAutoLocked(a.Key) ? ArtifactApproval.AutoLock : ArtifactApproval.Required, a.Approval);
            var expected = a.Key switch
            {
                SdlcArtifacts.CodebaseSummary or SdlcArtifacts.SourceItems => CascadeMode.None,
                SdlcArtifacts.StackProfile => CascadeMode.SelfAndDownstream,
                _ => CascadeMode.Downstream,
            };
            Assert.Equal(expected, a.Cascade);
        });
    }

    [Theory]
    [InlineData(SdlcArtifacts.ProjectRequest)]
    [InlineData(SdlcArtifacts.TechnicalSpecification)]
    [InlineData(SdlcArtifacts.ImplementationPlan)]
    [InlineData(SdlcArtifacts.OptimizationPlan)]
    [InlineData(SdlcArtifacts.CodebaseSummary)]
    [InlineData(SdlcArtifacts.SourceItems)]
    [InlineData(SdlcArtifacts.StackProfile)]
    public void FR290_the_artifacts_downstream_of_the_inputs_equal_the_0_6_1_invalidated_types(string approved)
    {
        // 0.6.1's cascade table (§4.4, §4.27).
        string[] expected = approved switch
        {
            SdlcArtifacts.ProjectRequest => [SdlcArtifacts.TechnicalSpecification, SdlcArtifacts.ImplementationPlan, SdlcArtifacts.OptimizationPlan],
            SdlcArtifacts.TechnicalSpecification => [SdlcArtifacts.ImplementationPlan, SdlcArtifacts.OptimizationPlan],
            SdlcArtifacts.ImplementationPlan => [SdlcArtifacts.OptimizationPlan],
            SdlcArtifacts.StackProfile => [SdlcArtifacts.ProjectRequest, SdlcArtifacts.TechnicalSpecification, SdlcArtifacts.ImplementationPlan, SdlcArtifacts.OptimizationPlan],
            _ => [],
        };

        // §4.54's dependency rule over the stage inputs. Fallback stages and step resets are compared in M58.
        var definition = Definition;
        List<string> invalidated = definition.FindArtifact(approved)!.Cascade == CascadeMode.None ? [] : Downstream(definition, approved);

        Assert.Equal(expected.Order(StringComparer.Ordinal), invalidated.Order(StringComparer.Ordinal));
        Assert.Equal(expected, Sdlc.Cascade(approved, Phase.Release).InvalidatedTypes);
    }

    [Fact]
    public void FR290_statuses_map_every_project_status_and_released_completes()
    {
        var definition = Definition;

        Assert.All(Enum.GetValues<ProjectStatus>(), status =>
        {
            var expected = status == ProjectStatus.Released ? GenericRunStatus.Completed : GenericRunStatus.Active;
            Assert.Equal(expected, definition.GenericStatusOf(status.ToWire()));
        });
        Assert.Equal(ProjectStatus.RolledBack.ToWire(), definition.RollbackStatus);
    }

    [Fact]
    public void FR290_release_status_changes_equal_the_0_6_1_release_transitions()
    {
        // Each named change against the 0.6.1 transition it replaces (§4.51): the statuses it applies from, its target,
        // whether it completes the run (released) and the error from any other status.
        var requests = new Dictionary<string, Transition>
        {
            ["release_completed"] = SdlcTransitions.ReleaseCompleted(),
            ["release_awaiting_merge"] = SdlcTransitions.ReleaseAwaitingMerge(),
            ["release_merge_abandoned"] = SdlcTransitions.ReleaseMergeAbandoned(),
        };

        Assert.Equal(requests.Keys, Definition.StatusChanges.Select(c => c.Name));
        Assert.All(Definition.StatusChanges, change =>
        {
            var outcomes = Enum.GetValues<ProjectStatus>()
                .Select(status => (Status: status, Result: Sdlc.Apply(new LifecycleState(Phase.Release, status, 3), requests[change.Name], LifecycleSnapshot.Empty)))
                .ToList();
            var allowed = outcomes.Where(o => o.Result.IsSuccess).ToList();

            Assert.Equal(Phase.Release.ToWire(), change.Stage);
            Assert.Equal(allowed.Select(o => o.Status.ToWire()).Order(StringComparer.Ordinal), change.From.Order(StringComparer.Ordinal));
            var to = Assert.Single(allowed.Select(o => o.Result.Value.Status).Distinct());
            Assert.Equal(to.ToWire(), change.To);
            Assert.Equal(to == ProjectStatus.Released, change.CompletesRun);
            Assert.All(outcomes.Where(o => !o.Result.IsSuccess), o => Assert.Equal(change.ErrorCode, o.Result.Error!.Code));
            Assert.Equal("NOT_RELEASING", change.ErrorCode);
        });
    }

    [Fact]
    public void FR290_the_review_loop_returns_to_execution_on_review_required_capped_by_review_cycles()
    {
        var loop = StageOf(Phase.Review).Loop!;

        Assert.Equal(Phase.Execution.ToWire(), loop.To);
        Assert.Equal(ReviewDecision.ReviewRequired.ToWire(), loop.On);
        Assert.Equal(IterationCap.FromSetting(SdlcPack.ReviewCycles), loop.MaxIterations);
        Assert.Equal([new GuardRequirement(SdlcGuardNames.ReviewCycleReady)], loop.Requires);
        Assert.Equal([SdlcPack.ReviewCycles], Definition.Settings);
    }

    [Fact]
    public void FR290_FR286_one_execution_job_per_project() =>
        Assert.Equal([new ExclusiveJob(SdlcJobTypes.Execution, ConcurrencyScope.Project)], Definition.Concurrency.Exclusive);

    [Fact]
    public void FR290_approval_modes_and_default_equal_the_project_settings()
    {
        Assert.Equal(Enum.GetValues<ApprovalMode>(), Definition.ApprovalModes);
        Assert.Equal(Aide.Domain.Projects.ProjectDefaults.Mode, Definition.DefaultApprovalMode);
    }

    /// <summary>The artifacts that depend on <paramref name="key"/>: output by a stage that has it (or one of them) in its inputs.</summary>
    private static List<string> Downstream(PipelineDefinition definition, string key)
    {
        var found = new List<string>();
        var queue = new Queue<string>([key]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var stage in definition.Stages.Where(s => s.Output is not null && s.Inputs.Contains(current)))
            {
                if (stage.Output != key && !found.Contains(stage.Output!))
                {
                    found.Add(stage.Output!);
                    queue.Enqueue(stage.Output!);
                }
            }
        }

        return found;
    }
}
