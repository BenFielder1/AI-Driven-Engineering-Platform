using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>FR-363 (M72): the run's follow-ups section (due times, then the execution once run) and the timeline label.</summary>
public sealed partial class FollowUpTests
{
    [Fact]
    public async Task FR363_the_run_lists_its_followups_with_due_times_and_a_run_followup_with_its_execution_and_label()
    {
        await using var app = App(factory, new RecordingResponder("grader", "Grade: B."));
        using var fixture = await ProjectApi.FixtureAsync(app);

        var started = await StartAsync(fixture);
        Assert.Equal(
            [("followup:1d", (DateTimeOffset?)null), ("followup:5d", null), ("followup:20d", null)],
            started.FollowUps.Select(f => (f.Label, f.DueAt)));

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);
        var run = await RunRowAsync(app, started.Id);
        var completedAt = run.CompletedAt!.Value;
        var dto = await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{run.Id}", Ct));
        Assert.Equal(
            [
                ("grade", "followup:1d", (DateTimeOffset?)completedAt.AddDays(1), (Guid?)null),
                ("grade", "followup:5d", completedAt.AddDays(5), null),
                ("grade", "followup:20d", completedAt.AddDays(20), null),
            ],
            dto.FollowUps.Select(f => (f.Stage, f.Label, f.DueAt, f.StageExecutionId)));

        var due = (await FixtureLinearApp.QueuedFollowUpJobsAsync(app, fixture.Project.Id)).Single(j => LabelOf(j) == "followup:1d");
        await AgentJobs.RunAsync(app, due);

        dto = await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{run.Id}", Ct));
        var ran = dto.FollowUps.Single(f => f.Label == "followup:1d");
        Assert.NotNull(ran.StageExecutionId);
        Assert.NotNull(ran.ExitedAt);
        Assert.Equal("completed", ran.Outcome);
        Assert.All(dto.FollowUps.Where(f => f.Label != "followup:1d"), f => Assert.Null(f.StageExecutionId));

        var timeline = await ProjectApi.ReadAsync<PipelineTimelinePageDto>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{run.Id}/timeline?limit=200", Ct));
        var entry = Assert.Single(timeline.Items, i => i.Id == ran.StageExecutionId);
        Assert.Equal("followup:1d", entry.FollowUp);
        Assert.Contains(timeline.Items, i => i.Kind == PipelineTimelineKind.ArtifactVersion && i.ArtifactKey == "grade" && i.Variant == "followup:1d");

        var definition = await ProjectApi.ReadAsync<PipelineDefinitionDto>(
            await fixture.Viewer.Client.GetAsync($"/api/pipelines/{FlowFixturePack.Postmortem}/definitions/{run.DefinitionHash}", Ct));
        var followUp = Assert.Single(definition.FollowUps);
        Assert.Equal("grade", followUp.Stage);
        Assert.Equal(["1d", "5d", "20d"], followUp.After);
    }
}
