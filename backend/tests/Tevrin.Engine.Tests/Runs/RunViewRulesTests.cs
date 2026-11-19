using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Tests.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-300, §4.57, D-0.7.0-32: generic run status and the stage list.</summary>
public class RunViewRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private static PipelineDefinition Minimal()
    {
        var result = PipelineDefinitionLoader.ParseAndValidate(DefinitionYaml.Minimal, DefinitionYaml.Catalog);
        Assert.Empty(result.Errors);
        return result.Definition!;
    }

    [Theory]
    [InlineData(GenericRunStatus.Active, true, true, GenericRunStatus.WaitingOnGate)]
    [InlineData(GenericRunStatus.Active, true, false, GenericRunStatus.WaitingOnGate)]
    [InlineData(GenericRunStatus.Active, false, true, GenericRunStatus.Running)]
    [InlineData(GenericRunStatus.Active, false, false, GenericRunStatus.Active)]
    [InlineData(GenericRunStatus.Completed, true, true, GenericRunStatus.Completed)]
    [InlineData(GenericRunStatus.Failed, false, true, GenericRunStatus.Failed)]
    [InlineData(GenericRunStatus.Cancelled, true, false, GenericRunStatus.Cancelled)]
    public void FR300_generic_status_is_derived_from_the_stored_status_gates_and_jobs(
        GenericRunStatus stored, bool pendingGate, bool activeJob, GenericRunStatus expected) =>
        Assert.Equal(expected, RunViewRules.DeriveStatus(stored, pendingGate, activeJob));

    [Fact]
    public void FR300_the_current_stage_is_active_entered_stages_completed_and_later_ones_pending()
    {
        StageEntry[] executions =
        [
            new("draft", T0, T0.AddMinutes(1)),
            new("check", T0.AddMinutes(1), T0.AddMinutes(2)),
            new("draft", T0.AddMinutes(2), T0.AddMinutes(4)),
            new("check", T0.AddMinutes(4), null),
        ];

        var stages = RunViewRules.Stages(Minimal(), "check", runFinal: false, executions, [], T0.AddMinutes(10));

        Assert.Equal(["draft", "check", "summarise"], stages.Select(s => s.Id));
        Assert.Equal([StageState.Completed, StageState.Active, StageState.Pending], stages.Select(s => s.State));
        Assert.Equal([2, 2, 0], stages.Select(s => s.Iterations));
        Assert.Equal(T0.AddMinutes(2), stages[0].EnteredAt);
        Assert.Equal(T0.AddMinutes(4), stages[0].ExitedAt);
        Assert.Equal(3 * 60_000L, stages[0].DurationMs);
        Assert.Null(stages[1].ExitedAt);
        Assert.Equal(7 * 60_000L, stages[1].DurationMs); // 1 minute closed + 6 minutes open until now
        Assert.Null(stages[2].DurationMs);
        Assert.Equal("Check", stages[1].Title);
    }

    [Fact]
    public void FR300_a_stage_passed_over_is_skipped_and_a_final_run_completes_its_current_stage()
    {
        StageEntry[] executions = [new("summarise", T0, T0.AddMinutes(1))];

        var running = RunViewRules.Stages(Minimal(), "summarise", runFinal: false, executions, [], T0);
        Assert.Equal([StageState.Skipped, StageState.Skipped, StageState.Active], running.Select(s => s.State));

        var ended = RunViewRules.Stages(Minimal(), "check", runFinal: true, [], [], T0);
        Assert.Equal([StageState.Skipped, StageState.Completed, StageState.Skipped], ended.Select(s => s.State));
    }

    [Fact]
    public void D_0_7_0_32_stages_without_executions_fall_back_to_entries_from_transitions()
    {
        var fromTransitions = RunViewRules.EntriesFromTransitions(
        [
            new TransitionEntry(null, "draft", PipelineTransitionKinds.Start, T0),
            new TransitionEntry("draft", "draft", null, T0.AddMinutes(1)), // a same-stage move that isn't an entry
            new TransitionEntry("draft", "check", PipelineTransitionKinds.Advance, T0.AddMinutes(2)),
        ]);

        Assert.Equal(
            [new StageEntry("draft", T0, T0.AddMinutes(2)), new StageEntry("check", T0.AddMinutes(2), null)],
            fromTransitions);

        StageEntry[] executions = [new("check", T0.AddMinutes(3), null)];
        var stages = RunViewRules.Stages(Minimal(), "check", runFinal: false, executions, fromTransitions, T0.AddMinutes(5));
        Assert.Equal(StageState.Completed, stages[0].State);
        Assert.Equal(1, stages[0].Iterations);
        Assert.Equal(T0.AddMinutes(3), stages[1].EnteredAt); // executions win where they exist
    }

    [Fact]
    public void D_0_7_0_35_after_a_rollback_later_stages_are_pending_and_keep_their_iterations()
    {
        StageEntry[] executions =
        [
            new("draft", T0, T0.AddMinutes(1)),
            new("check", T0.AddMinutes(1), T0.AddMinutes(2)),
            new("summarise", T0.AddMinutes(2), T0.AddMinutes(3)),
            new("draft", T0.AddMinutes(3), null),
        ];

        var stages = RunViewRules.Stages(Minimal(), "draft", runFinal: false, executions, [], T0.AddMinutes(4));

        Assert.Equal([StageState.Active, StageState.Pending, StageState.Pending], stages.Select(s => s.State));
        Assert.Equal([2, 1, 1], stages.Select(s => s.Iterations));
        Assert.Equal(60_000L, stages[2].DurationMs);
    }

    [Fact]
    public void FR300_running_counts_queued_and_running_jobs_of_the_project_except_ignored_types()
    {
        var project = Guid.CreateVersion7();
        var active = Tevrin.Engine.Api.RunActivity.ActiveJobsOf(project, ["notification"]).Compile();

        Assert.True(active(new Job { ProjectId = project, Type = "plan_generation", Status = Domain.Jobs.JobStatus.Queued }));
        Assert.True(active(new Job { ProjectId = project, Type = "release", Status = Domain.Jobs.JobStatus.Running }));
        Assert.False(active(new Job { ProjectId = project, Type = "notification", Status = Domain.Jobs.JobStatus.Queued }));
        Assert.False(active(new Job { ProjectId = project, Type = "release", Status = Domain.Jobs.JobStatus.Succeeded }));
        Assert.False(active(new Job { ProjectId = Guid.CreateVersion7(), Type = "release", Status = Domain.Jobs.JobStatus.Running }));
    }
}
