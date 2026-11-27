using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Runs;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-343, §4.64 (M68): parallel groups in the run driver end to end on the Fake provider, with <c>fixture-parallel</c>
/// (branch 0: bull → bull_review; branch 1: bear) and its failing-bear variants.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed partial class ParallelDriverTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> StartAsync(ProjectFixture fixture, string pipelineId)
    {
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs", new { pipelineId, approvalMode = "advisory", inputs = new { ticker = "ACME" } }, Ct),
            HttpStatusCode.Created);
        return run.Id;
    }

    private static Task<PipelineRun> RunAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct));

    private static Task<List<StageExecution>> ExecutionsAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.StageExecutions.AsNoTracking()
            .Where(e => e.PipelineRunId == runId).OrderBy(e => e.EnteredAt).ThenBy(e => e.StageId).ToListAsync(Ct));

    private static Task<List<string>> LockedAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking()
            .Where(a => a.PipelineRunId == runId && a.Status == ArtifactStatus.Locked && a.Type != "ticker").Select(a => a.Type).OrderBy(t => t).ToListAsync(Ct));

    private static async Task<int> AgentRunsAsync(WebApplicationFactory<Program> app, Guid projectId, Guid runId, string agentKey) =>
        (await AgentJobs.RunsAsync(app, projectId)).Count(r => r.PipelineRunId == runId && r.Agent == agentKey);

    private static Task<List<Job>> FailedDriverJobsAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == EngineJobTypes.StageRun && j.Status == JobStatus.Failed).ToListAsync(Ct));

    private static (string Stage, string? Branch, string? Outcome) Row(StageExecution e) => (e.StageId, e.Branch, e.Outcome);

    /// <summary>Runs the one queued <c>stage.run</c> job (the group's), which queues one job per branch.</summary>
    private static async Task<Dictionary<string, Job>> EnterGroupAsync(WebApplicationFactory<Program> app, Guid projectId, Guid runId)
    {
        await AgentJobs.RunAsync(app, Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, projectId)));
        var executions = await ExecutionsAsync(app, runId);
        return (await FixtureLinearApp.QueuedDriverJobsAsync(app, projectId))
            .ToDictionary(j => executions.Single(e => e.Id == StageRunPayload.Parse(j.Payload)!.StageExecutionId).StageId);
    }

    [Fact]
    public async Task FR343_both_branches_run_at_once_and_the_join_moves_the_run_to_the_decision()
    {
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit());
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Parallel);

        var branchJobs = await EnterGroupAsync(app, fixture.Project.Id, runId);
        Assert.Equal(["bear", "bull"], branchJobs.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("analysts", (await RunAsync(app, runId)).CurrentStage);

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Completed, "decide"), (run.Status, run.CurrentStage));
        var executions = await ExecutionsAsync(app, runId);
        Assert.Equal(
            [
                ("analysts", null, PipelineTransitionKinds.Advance),
                ("bear", "1", BranchOutcomes.Completed),
                ("bull", "0", PipelineTransitionKinds.Advance),
                ("bull_review", "0", BranchOutcomes.Completed),
                ("decide", null, "completed"),
            ],
            executions.Select(Row).OrderBy(r => r.Stage, StringComparer.Ordinal));
        Assert.All(executions, e => Assert.NotNull(e.ExitedAt));
        Assert.Equal(["bear_case", "bull_case", "bull_risks", "decision"], await LockedAsync(app, runId));
        foreach (var agent in (string[])["bull-analyst", "bull-reviewer", "bear-analyst", "portfolio-manager"])
        {
            Assert.Equal(1, await AgentRunsAsync(app, fixture.Project.Id, runId, agent));
        }

        Assert.Empty(await FailedDriverJobsAsync(app, fixture.Project.Id));
    }

    [Fact]
    public async Task FR343_section_4_64_a_failed_branch_holds_back_the_other_branchs_next_stage_until_a_retry()
    {
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit());
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.ParallelFail);
        var branchJobs = await EnterGroupAsync(app, fixture.Project.Id, runId);

        // The bear desk fails first; the bull analyst's stage still finishes, but bull_review doesn't start.
        await AgentJobs.RunAsync(app, branchJobs["bear"]);
        await AgentJobs.RunAsync(app, branchJobs["bull"]);

        Assert.Empty(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Active, "analysts"), (run.Status, run.CurrentStage));
        var failed = Assert.Single(await FailedDriverJobsAsync(app, fixture.Project.Id));
        Assert.StartsWith("VALIDATION:", failed.LastError, StringComparison.Ordinal);
        var held = (await ExecutionsAsync(app, runId)).Single(e => e.StageId == "bull_review");
        Assert.Equal(("0", null), (held.Branch, held.ExitedAt));
        Assert.Equal(0, await AgentRunsAsync(app, fixture.Project.Id, runId, "bull-reviewer"));

        // FR-333: a retry resumes every open branch: bull_review runs, the bear desk fails again, the group holds.
        await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Contributor.Client.PostAsync($"/api/pipeline-runs/{runId}/retry", null, Ct));
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        Assert.Equal(1, await AgentRunsAsync(app, fixture.Project.Id, runId, "bull-reviewer"));
        Assert.Equal(2, (await FailedDriverJobsAsync(app, fixture.Project.Id)).Count);
        run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Active, "analysts"), (run.Status, run.CurrentStage));
        Assert.Equal(0, await AgentRunsAsync(app, fixture.Project.Id, runId, "portfolio-manager"));
    }

    [Fact]
    public async Task FR343_with_continue_the_join_completes_with_the_failed_branch_marked()
    {
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit());
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.ParallelContinue);

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Completed, "decide"), (run.Status, run.CurrentStage));
        var executions = await ExecutionsAsync(app, runId);
        Assert.Equal(("1", BranchOutcomes.Failed), executions.Where(e => e.StageId == "bear").Select(e => (e.Branch, e.Outcome)).Single());
        Assert.Equal(BranchOutcomes.Completed, executions.Single(e => e.StageId == "bull_review").Outcome);
        Assert.Equal(["bull_case", "bull_risks", "decision"], await LockedAsync(app, runId));
        Assert.StartsWith("VALIDATION:", Assert.Single(await FailedDriverJobsAsync(app, fixture.Project.Id)).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR343_cancelling_a_run_in_a_group_cancels_every_branch_job()
    {
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit());
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Parallel);
        var branchJobs = await EnterGroupAsync(app, fixture.Project.Id, runId);

        var cancelled = await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Owner.Client.PostAsync($"/api/pipeline-runs/{runId}/cancel", null, Ct));

        Assert.Equal(GenericRunStatus.Cancelled, cancelled.Status);
        Assert.Empty(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
        var statuses = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == fixture.Project.Id && (j.Id == branchJobs["bull"].Id || j.Id == branchJobs["bear"].Id))
            .Select(j => j.Status).ToListAsync(Ct));
        Assert.Equal([JobStatus.Cancelled, JobStatus.Cancelled], statuses);
        Assert.All(await ExecutionsAsync(app, runId), e => Assert.NotNull(e.ExitedAt));
    }
}
