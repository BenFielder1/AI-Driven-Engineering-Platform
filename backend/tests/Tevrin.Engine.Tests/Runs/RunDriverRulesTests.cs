using System.Text.Json;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Runs;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-331, FR-332, FR-335, §4.62: the run driver's pure decisions, on <c>fixture-linear</c>.</summary>
public class RunDriverRulesTests
{
    private static readonly PipelineRegistry Registry = new([new FixtureLinearPack()], [], Array.Empty<JobHandlerRegistration>());

    private static PipelineDefinition Linear
    {
        get
        {
            Assert.True(Registry.TryGet(FixtureLinearPack.PipelineId, out var loaded), string.Join("; ", Registry.Errors.Select(e => e.Error.ToString())));
            return loaded.Definition!;
        }
    }

    private static StageDefinition Stage(string id) => Linear.FindStage(id)!;

    [Fact]
    public void FR332_fixture_linear_is_valid_and_engine_controlled()
    {
        Assert.Empty(Registry.Errors);
        Assert.Equal(RunControl.Engine, Registry.RunControlOf(FixtureLinearPack.PipelineId));
        Assert.Equal(["draft", "check", "approve", "publish"], Linear.Stages.Select(s => s.Id));
    }

    [Fact]
    public void FR332_an_agent_stage_with_everything_met_runs_its_agent() =>
        Assert.Equal(new DriverStep.RunAgent("drafter"), RunDriverRules.Plan(Stage("draft"), new StageEntryFacts()));

    [Fact]
    public void FR332_a_handler_stage_runs_its_handler() =>
        Assert.Equal(new DriverStep.RunHandler(FixtureLinearPack.PublishHandler), RunDriverRules.Plan(Stage("publish"), new StageEntryFacts()));

    [Fact]
    public void FR335_a_missing_input_waits_before_anything_else()
    {
        var missing = RunDriverRules.MissingInputs(Stage("draft"), []);
        Assert.Equal(["brief"], missing);
        var step = RunDriverRules.Plan(Stage("draft"), new StageEntryFacts { MissingInputs = missing, UnmetReasons = ["other"], Gate = EntryGateState.Missing });
        Assert.IsType<DriverStep.WaitOnInput>(step);
    }

    [Fact]
    public void FR335_a_present_input_is_not_missing() =>
        Assert.Empty(RunDriverRules.MissingInputs(Stage("draft"), ["brief"]));

    [Fact]
    public void FR332_an_unmet_requirement_fails_the_stage_with_its_reasons() =>
        Assert.Equal(["no"], Assert.IsType<DriverStep.FailStage>(RunDriverRules.Plan(Stage("check"), new StageEntryFacts { UnmetReasons = ["no"] })).Reasons);

    [Theory]
    [InlineData(ApprovalMode.Strict, false, false, EntryGateState.Missing)]
    [InlineData(ApprovalMode.Advisory, false, true, EntryGateState.Pending)]
    [InlineData(ApprovalMode.Advisory, true, false, EntryGateState.Approved)]
    [InlineData(ApprovalMode.Autonomous, false, false, EntryGateState.NotNeeded)]
    public void FR332_the_entry_gate_state_follows_the_mode_and_the_runs_gates(ApprovalMode mode, bool approved, bool pending, EntryGateState expected) =>
        Assert.Equal(expected, RunDriverRules.GateState(Stage("approve"), mode, approved, pending));

    [Fact]
    public void FR332_a_needed_gate_is_raised_then_waited_on_then_passed_once_approved()
    {
        Assert.Equal(new DriverStep.RaiseGate("publish_approval"), RunDriverRules.Plan(Stage("approve"), new StageEntryFacts { Gate = EntryGateState.Missing }));
        Assert.Equal(new DriverStep.WaitOnGate("publish_approval"), RunDriverRules.Plan(Stage("approve"), new StageEntryFacts { Gate = EntryGateState.Pending }));
        Assert.IsType<DriverStep.PassApproval>(RunDriverRules.Plan(Stage("approve"), new StageEntryFacts { Gate = EntryGateState.Approved }));
        Assert.IsType<DriverStep.PassApproval>(RunDriverRules.Plan(Stage("approve"), new StageEntryFacts()));
    }

    [Fact]
    public void FR332_a_manual_stage_without_a_gate_stops() =>
        Assert.IsType<DriverStep.StopManual>(RunDriverRules.Plan(Stage("approve") with { Gate = null }, new StageEntryFacts()));

    [Fact]
    public void FR332_after_a_stage_the_run_advances_to_the_next_with_checkpoint_plus_one()
    {
        var start = PipelineStateMachine.Start(Linear, "draft");
        var next = Assert.IsType<DriverNext.Advance>(RunDriverRules.After(Linear, start)).State;
        Assert.Equal(("check", "checking", 2, 1), (next.Stage, next.PackStatus, next.Checkpoint, next.IterationsOf("check")));
    }

    [Fact]
    public void FR332_the_example_walks_four_stages_and_completes_after_the_last()
    {
        var state = PipelineStateMachine.Start(Linear, "draft");
        var visited = new List<string> { state.Stage };
        while (RunDriverRules.After(Linear, state) is DriverNext.Advance advance)
        {
            state = advance.State;
            visited.Add(state.Stage);
        }

        Assert.Equal(["draft", "check", "approve", "publish"], visited);
        Assert.Equal(new DriverNext.Complete("published"), RunDriverRules.After(Linear, state));
    }

    [Fact]
    public void FR332_a_rejected_gate_without_on_reject_fails_the_run()
    {
        var state = new RunState("approve", "approving", 3, PipelineRunRules.NoIterations);
        Assert.IsType<GateRejection.FailRun>(RunDriverRules.OnGateRejected(Linear, state, "publish_approval", null));
    }

    [Fact]
    public void FR332_a_rejected_gate_with_on_reject_re_enters_its_target_within_the_cap()
    {
        var definition = Linear with
        {
            Stages = [.. Linear.Stages.Select(s => s.Id == "approve" ? s with { OnReject = new RejectTransition("draft", IterationCap.Fixed(2)) } : s)],
        };
        var once = new RunState("approve", "approving", 3, new Dictionary<string, int> { ["draft"] = 1 });
        var reenter = Assert.IsType<GateRejection.Reenter>(RunDriverRules.OnGateRejected(definition, once, "publish_approval", 2)).State;
        Assert.Equal(("draft", 3, 2), (reenter.Stage, reenter.Checkpoint, reenter.IterationsOf("draft")));

        var twice = once with { Iterations = new Dictionary<string, int> { ["draft"] = 2 } };
        Assert.IsType<GateRejection.FailRun>(RunDriverRules.OnGateRejected(definition, twice, "publish_approval", 2));
    }

    [Fact]
    public void FR332_a_redelivery_for_a_finished_or_superseded_execution_is_stale()
    {
        var run = new PipelineRun { CurrentStage = "check", Status = GenericRunStatus.Active };
        var open = new StageExecution { PipelineRunId = run.Id, StageId = "check" };
        Assert.False(RunDriverRules.IsStale(run, open));
        Assert.True(RunDriverRules.IsStale(run, null));
        Assert.True(RunDriverRules.IsStale(run, new StageExecution { PipelineRunId = run.Id, StageId = "check", ExitedAt = DateTimeOffset.UnixEpoch }));
        Assert.True(RunDriverRules.IsStale(run, new StageExecution { PipelineRunId = run.Id, StageId = "draft" }));
        Assert.True(RunDriverRules.IsStale(run, new StageExecution { PipelineRunId = Guid.NewGuid(), StageId = "check" }));
        run.Status = GenericRunStatus.Cancelled;
        Assert.True(RunDriverRules.IsStale(run, open));
    }

    [Fact]
    public void FR331_the_start_lock_key_is_stable_and_differs_by_project_and_pipeline()
    {
        var project = Guid.Parse("0190a0b0-0000-7000-8000-000000000001");
        Assert.Equal(RunStartLock.Key(project, "fixture-linear"), RunStartLock.Key(project, "fixture-linear"));
        Assert.NotEqual(RunStartLock.Key(project, "fixture-linear"), RunStartLock.Key(project, "fixture-review"));
        Assert.NotEqual(RunStartLock.Key(project, "fixture-linear"), RunStartLock.Key(Guid.NewGuid(), "fixture-linear"));
    }

    [Fact]
    public void FR331_the_approval_mode_is_the_requested_one_else_the_projects()
    {
        Assert.Equal(ApprovalMode.Strict, RunInputs.ApprovalModeOf(Linear, ApprovalMode.Strict, ApprovalMode.Advisory));
        Assert.Equal(ApprovalMode.Autonomous, RunInputs.ApprovalModeOf(Linear, null, ApprovalMode.Autonomous));
        var strictOnly = Linear with { ApprovalModes = [ApprovalMode.Strict] };
        Assert.Null(RunInputs.ApprovalModeOf(strictOnly, ApprovalMode.Advisory, ApprovalMode.Advisory));
        Assert.Equal(ApprovalMode.Advisory, RunInputs.ApprovalModeOf(strictOnly with { DefaultApprovalMode = ApprovalMode.Advisory }, null, ApprovalMode.Autonomous));
    }

    [Fact]
    public void FR331_FR335_an_input_that_fails_its_schema_is_INPUT_INVALID_with_the_paths()
    {
        var schemas = new ArtifactSchemas(Registry);
        var brief = Linear.FindArtifact("brief")!;
        using var bad = JsonDocument.Parse("""{"words":0}""");
        var result = RunInputs.Content(brief, bad.RootElement, FixtureLinearPack.PipelineId, schemas);
        Assert.True(result.IsFailure);
        Assert.Equal("INPUT_INVALID", result.Error!.Code);
        Assert.Contains(result.Error.Details, d => d.StartsWith("/words:", StringComparison.Ordinal));

        using var good = JsonDocument.Parse("""{"topic":"Tides"}""");
        Assert.True(RunInputs.Content(brief, good.RootElement, FixtureLinearPack.PipelineId, schemas).IsSuccess);
    }

    [Fact]
    public void FR332_run_scoped_job_types_are_the_driver_and_trigger_fires_and_no_sdlc_type() =>
        Assert.Equal([EngineJobTypes.StageRun, EngineJobTypes.TriggerFire], EngineJobTypes.RunScoped);

    [Fact]
    public void FR335_touching_a_run_writes_its_row_so_its_xmin_guards_the_save()
    {
        using var db = new OfflineEngineDbContext();
        var run = new PipelineRun { CurrentStage = "draft", Status = GenericRunStatus.Active };
        db.PipelineRuns.Attach(run);
        Assert.Equal(Microsoft.EntityFrameworkCore.EntityState.Unchanged, db.Entry(run).State);

        EngineRunMoves.Touch(db, run);

        Assert.Equal(Microsoft.EntityFrameworkCore.EntityState.Modified, db.Entry(run).State);
        Assert.Throws<InvalidOperationException>(() => EngineRunMoves.Touch(db, new PipelineRun()));
    }
}
