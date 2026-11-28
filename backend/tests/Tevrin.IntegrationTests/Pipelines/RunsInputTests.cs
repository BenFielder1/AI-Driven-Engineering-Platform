using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Runtime.Runs;
using Tevrin.Engine.Runtime.Triggers;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-357 (M70, §3.6): <c>from_runs</c> inputs end to end on the Fake provider with <c>fixture-trends</c>: a scheduled (or
/// manual) run collects the final <c>scores</c> of the project's <c>fixture-qa</c> runs completed in the last 7 days,
/// newest first, as <c>{{SCORES_RUNS}}</c>; a handler gets the same list as a fact; a follow-up reading its own pipeline's
/// reports leaves out the current run. The <c>fixture-qa</c> runs are seeded rows (the map itself is MapDriverTests').
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RunsInputTests(TevrinApiFactory factory)
{
    /// <summary>Monday 2 Dec 2030, 05:59Z: a fake clock after today (see TriggerTests), a minute before the schedule fires.</summary>
    private static readonly DateTimeOffset MondayBeforeSix = new(2030, 12, 2, 5, 59, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Six = MondayBeforeSix.AddMinutes(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> Create(TevrinApiFactory api, TriggerClock clock, params IFakeAgentResponder[] responders) =>
        FixtureFlowApp.Create(api, new PositionLimit(), responders)
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));

    private static async Task<Guid> StartAsync(ProjectFixture fixture, string pipelineId)
    {
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", new { pipelineId, approvalMode = "advisory" }, Ct),
            HttpStatusCode.Created);
        return run.Id;
    }

    private static Task<PipelineRun> RunAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct));

    /// <summary>
    /// A <c>fixture-qa</c> run row ended with <paramref name="status"/> <paramref name="ago"/> before <see cref="Six"/> (started
    /// an hour earlier; <c>null</c>: still active), with a superseded v1, the final v2 <paramref name="score"/> and a map item.
    /// It's pinned to the project's SDLC definition hash, which only satisfies the foreign key. Returns its start time.
    /// </summary>
    private static Task<DateTimeOffset> SeedQaRunAsync(
        WebApplicationFactory<Program> app, Guid projectId, TimeSpan? ago, string score, GenericRunStatus status = GenericRunStatus.Completed) =>
        ProjectApi.WithDbAsync(app, async db =>
        {
            var hash = await db.PipelineRuns.AsNoTracking().Where(r => r.ProjectId == projectId && r.SubjectId != null).Select(r => r.DefinitionHash).FirstAsync(Ct);
            var completedAt = Six - (ago ?? TimeSpan.Zero);
            var run = new PipelineRun
            {
                ProjectId = projectId,
                PipelineId = FlowFixturePack.Qa,
                DefinitionHash = hash,
                CurrentStage = "score",
                PackStatus = "scoring",
                Status = ago is null ? GenericRunStatus.Active : status,
                ApprovalMode = ApprovalMode.Advisory,
                StartedAt = completedAt.AddHours(-1),
                CompletedAt = ago is null ? null : completedAt,
            };
            db.PipelineRuns.Add(run);
            db.Artifacts.Add(Scores(run, 1, $"{{\"draft\": \"{score}\"}}", ArtifactStatus.Superseded));
            db.Artifacts.Add(Scores(run, 2, $"{{\"mean\": \"{score}\"}}", ArtifactStatus.Locked));
            db.Artifacts.Add(Scores(run, 3, $"{{\"item\": \"{score}\"}}", ArtifactStatus.Locked, itemIndex: 0));
            await db.SaveChangesAsync(Ct);
            return run.StartedAt;
        });

    private static Artifact Scores(PipelineRun run, int version, string content, ArtifactStatus status, int? itemIndex = null) => new()
    {
        ProjectId = run.ProjectId,
        PipelineRunId = run.Id,
        Type = "scores",
        Version = version,
        Content = content,
        ContentHash = ContentHash.Compute(content),
        Status = status,
        ItemIndex = itemIndex,
        CheckpointAtCreation = 1,
        CreatedAt = run.StartedAt,
    };

    /// <summary>Fires the project's due schedules once (a sweep, then the queued <c>trigger.fire</c> jobs).</summary>
    private static async Task FireSchedulesAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PipelineSchedules>().QueueDueFiresAsync(Ct);
        }

        var fires = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == EngineJobTypes.TriggerFire && j.Status == JobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(Ct));
        foreach (var job in fires)
        {
            await AgentJobs.RunAsync(app, job);
        }
    }

    [Fact]
    public async Task FR357_a_scheduled_trends_run_reads_the_final_scores_of_completed_qa_runs_in_the_last_7_days_newest_first()
    {
        var clock = new TriggerClock(MondayBeforeSix);
        var analyst = new RecordingResponder("trend-analyst", "Scores are steady.");
        await using var app = Create(factory, clock, analyst);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var oneDay = await SeedQaRunAsync(app, id, TimeSpan.FromDays(1), "one-day");
        await SeedQaRunAsync(app, id, TimeSpan.FromDays(3), "three-days");
        await SeedQaRunAsync(app, id, TimeSpan.FromDays(6), "six-days");
        await SeedQaRunAsync(app, id, TimeSpan.FromDays(8), "eight-days");
        await SeedQaRunAsync(app, id, TimeSpan.FromDays(2), "failed", GenericRunStatus.Failed);
        await SeedQaRunAsync(app, id, TimeSpan.FromDays(2), "cancelled", GenericRunStatus.Cancelled);
        await SeedQaRunAsync(app, id, ago: null, "active");

        // A weekly schedule fires at 06:00 and starts the run; the analyst reads the list.
        await ProjectApi.ReadAsync<PipelineScheduleDto>(
            await fixture.Owner.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-schedules", new { pipelineId = FlowFixturePack.Trends, cron = "0 6 * * 1", timezone = "Etc/UTC" }, Ct),
            HttpStatusCode.Created);
        clock.Advance(TimeSpan.FromMinutes(1));
        await FireSchedulesAsync(app, id);
        await FixtureLinearApp.DriveAsync(app, id);

        var scheduled = await ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.ProjectId == id && r.PipelineId == FlowFixturePack.Trends, Ct));
        Assert.Equal(GenericRunStatus.Completed, scheduled.Status);
        var prompt = Assert.Single(analyst.Prompts);
        var one = prompt.IndexOf(Final("one-day"), StringComparison.Ordinal);
        var three = prompt.IndexOf(Final("three-days"), StringComparison.Ordinal);
        var six = prompt.IndexOf(Final("six-days"), StringComparison.Ordinal);
        Assert.True(one >= 0 && one < three && three < six, prompt);
        Assert.Contains($"### {RunsInputRules.Label(oneDay)}\n{Final("one-day")}", prompt, StringComparison.Ordinal);
        foreach (var left in (string[])["eight-days", "failed", "cancelled", "active"])
        {
            Assert.DoesNotContain(left, prompt, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("\"draft\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"item\"", prompt, StringComparison.Ordinal);

        // Two days later the six-day-old run has left the window (the first trends run is another pipeline's).
        clock.Advance(TimeSpan.FromDays(2));
        var manual = await StartAsync(fixture, FlowFixturePack.Trends);
        await FixtureLinearApp.DriveAsync(app, id);

        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, manual)).Status);
        var later = analyst.Prompts[1];
        Assert.Contains(Final("one-day"), later, StringComparison.Ordinal);
        Assert.Contains(Final("three-days"), later, StringComparison.Ordinal);
        Assert.DoesNotContain("six-days", later, StringComparison.Ordinal);
        var report = await ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking().SingleAsync(a => a.PipelineRunId == manual && a.Type == "trend_report", Ct));
        Assert.Equal(("Scores are steady.", ArtifactStatus.Locked), (report.Content, report.Status));
    }

    [Fact]
    public async Task FR357_a_handler_stage_gets_the_list_as_a_fact_without_failed_or_cancelled_runs()
    {
        var clock = new TriggerClock(Six);
        await using var app = Create(factory, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var newer = await SeedQaRunAsync(app, id, TimeSpan.FromHours(2), "newer");
        var older = await SeedQaRunAsync(app, id, TimeSpan.FromDays(4), "older");
        await SeedQaRunAsync(app, id, TimeSpan.FromHours(1), "failed", GenericRunStatus.Failed);
        await SeedQaRunAsync(app, id, TimeSpan.FromHours(1), "cancelled", GenericRunStatus.Cancelled);

        var runId = await StartAsync(fixture, FlowFixturePack.TrendsHandler);
        await FixtureLinearApp.DriveAsync(app, id);

        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        var call = Assert.Single(app.Services.GetRequiredService<TrendLog>().Calls, c => c.RunId == runId);
        var list = Assert.Single(call.Fact!.Lists);
        Assert.Equal(("SCORES_RUNS", FlowFixturePack.Qa, GenericRunStatus.Completed), (list.Placeholder, list.Input.Pipeline, list.Input.Status));
        Assert.Equal(
            new[] { new AggregateItem(RunsInputRules.Label(newer), Final("newer")), new AggregateItem(RunsInputRules.Label(older), Final("older")) },
            list.Items);
    }

    [Fact]
    public async Task FR357_a_follow_up_reading_its_own_pipeline_leaves_out_the_current_run()
    {
        var clock = new TriggerClock(Six);
        var analyst = new ScriptedResponder("trend-analyst", "Report one.", "Report two.");
        var recapper = new RecordingResponder("trend-recapper", "Recap.");
        await using var app = Create(factory, clock, analyst, recapper);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await SeedQaRunAsync(app, id, TimeSpan.FromHours(1), "seed");

        var first = await StartAsync(fixture, FlowFixturePack.TrendsRecap);
        await FixtureLinearApp.DriveAsync(app, id);
        clock.Advance(TimeSpan.FromHours(1));
        var second = await StartAsync(fixture, FlowFixturePack.TrendsRecap);
        await FixtureLinearApp.DriveAsync(app, id);
        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, first)).Status);
        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, second)).Status);

        // Each completed run's recap is due a day later; each sees the other run's report, never its own.
        clock.Advance(TimeSpan.FromDays(1));
        var recaps = await FixtureLinearApp.QueuedFollowUpJobsAsync(app, id);
        var firstRecap = Assert.Single(recaps, j => StageRunPayload.Parse(j.Payload)!.RunId == first);
        var secondRecap = Assert.Single(recaps, j => StageRunPayload.Parse(j.Payload)!.RunId == second);
        await AgentJobs.RunAsync(app, firstRecap);
        await AgentJobs.RunAsync(app, secondRecap);

        Assert.Equal(2, recapper.Prompts.Count);
        Assert.Contains("Report two.", recapper.Prompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Report one.", recapper.Prompts[0], StringComparison.Ordinal);
        Assert.Contains("Report one.", recapper.Prompts[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Report two.", recapper.Prompts[1], StringComparison.Ordinal);
    }

    /// <summary>The final score of a seeded run as it appears in a prompt (its v2).</summary>
    private static string Final(string score) => $"{{\"mean\": \"{score}\"}}";
}
