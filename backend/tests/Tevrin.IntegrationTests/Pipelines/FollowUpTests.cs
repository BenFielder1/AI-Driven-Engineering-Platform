using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Runs;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-354, §4.70 (M69): follow-up stages end to end on the Fake provider, with <c>fixture-postmortem</c>: completing a run
/// queues one delayed <c>stage.run</c> job per <c>after</c> duration (<c>run_after = completed_at + after</c>); a due one
/// grades the completed run, stores its output labelled <c>followup:&lt;duration&gt;</c> and leaves the run
/// <c>completed</c>; cancelling the completed run cancels the rest; a run cancelled before it completes schedules none.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class FollowUpTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string NoLessons = """{ "lessons": [] }""";

    private static WebApplicationFactory<Program> App(TevrinApiFactory factory, RecordingResponder grader) =>
        FixtureFlowApp.Create(
            factory, new PositionLimit(), new RecordingResponder("worker", "Summary."), new ScriptedResponder("reviewer", NoLessons), grader);

    private static async Task<PipelineRunDto> StartAsync(ProjectFixture fixture) =>
        await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs",
                new { pipelineId = FlowFixturePack.Postmortem, approvalMode = "advisory", inputs = new { task = "Summarise the report." } },
                Ct),
            HttpStatusCode.Created);

    private static async Task<PipelineRun> CompletedRunAsync(WebApplicationFactory<Program> app, ProjectFixture fixture)
    {
        var run = await StartAsync(fixture);
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);
        var done = await RunRowAsync(app, run.Id);
        Assert.Equal(GenericRunStatus.Completed, done.Status);
        Assert.NotNull(done.CompletedAt);
        return done;
    }

    private static Task<PipelineRun> RunRowAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct));

    private static Task<Job> JobAsync(WebApplicationFactory<Program> app, Guid jobId) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct));

    private static string? LabelOf(Job job) => StageRunPayload.Parse(job.Payload)?.FollowUp;

    [Fact]
    public async Task FR354_completing_a_run_queues_one_delayed_job_per_duration_at_completed_at_plus_after()
    {
        await using var app = App(factory, new RecordingResponder("grader", "Grade: B."));
        using var fixture = await ProjectApi.FixtureAsync(app);

        var run = await CompletedRunAsync(app, fixture);

        var completedAt = run.CompletedAt!.Value;
        var followUps = (await FixtureLinearApp.QueuedFollowUpJobsAsync(app, fixture.Project.Id)).OrderBy(j => j.RunAfter).ToList();
        Assert.Equal(
            [
                ("followup:1d", completedAt.AddDays(1)),
                ("followup:5d", completedAt.AddDays(5)),
                ("followup:20d", completedAt.AddDays(20)),
            ],
            followUps.Select(j => (LabelOf(j), j.RunAfter)));
        Assert.All(followUps, j =>
        {
            var payload = StageRunPayload.Parse(j.Payload)!;
            Assert.Equal((run.Id, "grade", 0, (string?)null), (payload.RunId, payload.FollowUpStage, j.Attempts, j.LastError));
        });

        // No follow-up runs as part of the main flow, and the run has no execution of the follow-up stage yet.
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.StageExecutions.AsNoTracking().AnyAsync(e => e.PipelineRunId == run.Id && e.StageId == "grade", Ct)));
    }

    [Fact]
    public async Task FR354_a_due_followup_stores_its_labelled_output_and_the_run_stays_completed()
    {
        var grader = new RecordingResponder("grader", "Grade: B.");
        await using var app = App(factory, grader);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await CompletedRunAsync(app, fixture);
        var worker = Assert.Single(await AgentJobs.RunsAsync(app, fixture.Project.Id), r => r.PipelineRunId == run.Id && r.Agent == "worker");
        await ProjectApi.ReadAsync<OutcomeScoreDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"/api/pipeline-runs/{run.Id}/outcomes",
                new { target = new { agentRunId = worker.Id }, metric = "accuracy", kind = "number", value = 0.8, observedAt = run.CompletedAt, source = "crm" },
                Ct),
            HttpStatusCode.Created);
        var due = (await FixtureLinearApp.QueuedFollowUpJobsAsync(app, fixture.Project.Id)).Single(j => LabelOf(j) == "followup:1d");

        await AgentJobs.RunAsync(app, due);

        var ran = await JobAsync(app, due.Id);
        Assert.Equal((JobStatus.Succeeded, (string?)null), (ran.Status, ran.LastError));
        var after = await RunRowAsync(app, run.Id);
        Assert.Equal((GenericRunStatus.Completed, run.CompletedAt, run.PackStatus), (after.Status, after.CompletedAt, after.PackStatus));

        // The grader read the run's artifacts, its outcome scores and its own label.
        var prompt = Assert.Single(grader.Prompts);
        Assert.Contains("Result: Summary.", prompt, StringComparison.Ordinal);
        Assert.Contains("accuracy: 0.8", prompt, StringComparison.Ordinal);
        Assert.Contains("Label: followup:1d", prompt, StringComparison.Ordinal);

        // Its output is the grade artifact labelled followup:1d; the main flow's artifacts carry no label.
        var grade = await ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking()
            .SingleAsync(a => a.ProjectId == fixture.Project.Id && a.PipelineRunId == run.Id && a.Type == "grade", Ct));
        Assert.Equal(("followup:1d", ArtifactStatus.Locked, "Grade: B.", 1), (grade.Variant, grade.Status, grade.Content, grade.Version));
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking()
            .AnyAsync(a => a.ProjectId == fixture.Project.Id && a.PipelineRunId == run.Id && a.Type != "grade" && a.Variant != null, Ct)));

        // The follow-up's execution is labelled, finished, and in the run's timeline.
        var execution = await ProjectApi.WithDbAsync(app, db => db.StageExecutions.AsNoTracking()
            .SingleAsync(e => e.PipelineRunId == run.Id && e.StageId == "grade", Ct));
        Assert.Equal(("followup:1d", "completed"), (execution.FollowUp, execution.Outcome));
        Assert.NotNull(execution.ExitedAt);
        var timeline = await ProjectApi.ReadAsync<PipelineTimelinePageDto>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{run.Id}/timeline", Ct));
        Assert.Contains(timeline.Items, i => i.Id == execution.Id && i.StageId == "grade");

        // A redelivery of the same job does nothing more.
        await AgentJobs.RunAsync(app, due);
        Assert.Single(grader.Prompts);
        Assert.Equal(2, (await FixtureLinearApp.QueuedFollowUpJobsAsync(app, fixture.Project.Id)).Count);
    }

    [Fact]
    public async Task FR354_cancelling_the_completed_run_cancels_the_remaining_followups()
    {
        var grader = new RecordingResponder("grader", "Grade: B.");
        await using var app = App(factory, grader);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await CompletedRunAsync(app, fixture);
        var followUps = await FixtureLinearApp.QueuedFollowUpJobsAsync(app, fixture.Project.Id);
        var first = followUps.Single(j => LabelOf(j) == "followup:1d");
        await AgentJobs.RunAsync(app, first);
        var cancel = $"/api/pipeline-runs/{run.Id}/cancel";

        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(cancel, null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        var cancelled = await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Owner.Client.PostAsync(cancel, null, Ct));

        Assert.Equal(GenericRunStatus.Completed, cancelled.Status);
        Assert.Equal(GenericRunStatus.Completed, (await RunRowAsync(app, run.Id)).Status);
        Assert.Empty(await FixtureLinearApp.QueuedFollowUpJobsAsync(app, fixture.Project.Id));
        var statuses = new List<(string? Label, JobStatus Status)>();
        foreach (var job in followUps)
        {
            statuses.Add((LabelOf(job), (await JobAsync(app, job.Id)).Status));
        }

        Assert.Equal(
            [("followup:1d", JobStatus.Succeeded), ("followup:20d", JobStatus.Cancelled), ("followup:5d", JobStatus.Cancelled)],
            statuses.OrderBy(s => s.Label, StringComparer.Ordinal));

        // With none left, a second cancel is refused as for any final run.
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(cancel, null, Ct), HttpStatusCode.Conflict, ErrorCodes.RunNotActive);
    }

    [Fact]
    public async Task FR354_a_run_cancelled_before_it_completes_schedules_no_followups()
    {
        await using var app = App(factory, new RecordingResponder("grader", "Grade: B."));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await StartAsync(fixture);

        var cancelled = await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Owner.Client.PostAsync($"/api/pipeline-runs/{run.Id}/cancel", null, Ct));
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        Assert.Equal(GenericRunStatus.Cancelled, cancelled.Status);
        Assert.Equal(GenericRunStatus.Cancelled, (await RunRowAsync(app, run.Id)).Status);
        var jobs = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == fixture.Project.Id && j.Type == EngineJobTypes.StageRun).ToListAsync(Ct));
        Assert.DoesNotContain(jobs, j => LabelOf(j) is not null);
    }
}
