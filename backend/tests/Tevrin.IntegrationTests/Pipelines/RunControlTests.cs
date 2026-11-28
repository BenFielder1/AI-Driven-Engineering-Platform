using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Support;
using Tevrin.Pack.Sdlc;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-331–FR-335 (M66): starting, driving, cancelling and feeding engine-controlled runs through the API, on
/// <c>fixture-linear</c> and the Fake provider.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RunControlTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Start(object? inputs = null, string mode = "advisory") =>
        new { pipelineId = FixtureLinearPack.PipelineId, approvalMode = mode, inputs = inputs ?? new { brief = new { topic = "Tides" } } };

    [Fact]
    public async Task FR331_FR332_fixture_linear_runs_both_agents_stops_at_the_gate_and_completes_after_approval()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", Start(), Ct), HttpStatusCode.Created);
        Assert.Equal((FixtureLinearPack.PipelineId, "draft", 1), (run.PipelineId, run.CurrentStage, run.Checkpoint));

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);
        var waiting = await GetRunAsync(fixture.Owner.Client, run.Id);
        Assert.Equal(("approve", GenericRunStatus.WaitingOnGate), (waiting.CurrentStage, waiting.Status));
        var gate = Assert.Single(await ProjectApi.ReadAsync<List<PipelineGateDto>>(await fixture.Owner.Client.GetAsync($"/api/pipeline-runs/{run.Id}/gates", Ct)));
        Assert.Equal(GateStatus.Pending, gate.Status);

        await ProjectApi.ReadAsync<PipelineGateDto>(await fixture.Owner.Client.PostAsJsonAsync($"/api/pipeline-runs/{run.Id}/gates/{gate.Id}/approve", new { comment = "Ship it" }, Ct));
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var done = await GetRunAsync(fixture.Owner.Client, run.Id);
        Assert.Equal((GenericRunStatus.Completed, "published", 2), (done.Status, done.PackStatus, done.AgentRuns));
        Assert.Contains(run.Id, app.Services.GetRequiredService<PublishLog>().Runs);

        var executions = await ProjectApi.WithDbAsync(app, db => db.StageExecutions.AsNoTracking().Where(e => e.PipelineRunId == run.Id).OrderBy(e => e.EnteredAt).ToListAsync(Ct));
        Assert.Equal(["draft", "check", "approve", "publish"], executions.Select(e => e.StageId));
        Assert.All(executions, e => Assert.NotNull(e.ExitedAt));
        Assert.Single(await ProjectApi.WithDbAsync(app, db => db.ApprovalGates.AsNoTracking().Where(g => g.PipelineRunId == run.Id).ToListAsync(Ct)));

        var stages = await ProjectApi.ReadAsync<List<PipelineStageDto>>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{run.Id}/stages", Ct));
        Assert.Equal(["draft", "check", "approve", "publish"], stages.Select(s => s.Id));
    }

    [Fact]
    public async Task FR330_FR331_starting_the_sdlc_is_RUN_CONTROLLED_BY_HOST_and_an_unknown_pipeline_is_NOT_FOUND()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", new { pipelineId = SdlcPack.PipelineId }, Ct),
            HttpStatusCode.Conflict, ErrorCodes.RunControlledByHost);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", new { pipelineId = "no-such-pipeline" }, Ct),
            HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await fixture.Viewer.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", Start(), Ct),
            HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task FR332_a_redelivered_stage_run_job_runs_no_agent_again()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", Start(), Ct), HttpStatusCode.Created);
        var first = Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
        await AgentJobs.RunAsync(app, first);
        Assert.Equal(1, await AgentRunCountAsync(app, run.Id));

        await AgentJobs.RunAsync(app, first);

        Assert.Equal(1, await AgentRunCountAsync(app, run.Id));
        Assert.Equal("check", (await GetRunAsync(fixture.Owner.Client, run.Id)).CurrentStage);
    }

    [Fact]
    public async Task FR333_an_owner_cancels_a_run_its_jobs_are_cancelled_and_a_second_cancel_is_RUN_NOT_ACTIVE()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", Start(), Ct), HttpStatusCode.Created);
        var cancel = $"/api/pipeline-runs/{run.Id}/cancel";

        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(cancel, null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        var cancelled = await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Owner.Client.PostAsync(cancel, null, Ct));
        Assert.Equal(GenericRunStatus.Cancelled, cancelled.Status);
        Assert.Empty(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
        Assert.Contains(
            JobStatus.Cancelled,
            await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().Where(j => j.ProjectId == fixture.Project.Id && j.Type == EngineJobTypes.StageRun).Select(j => j.Status).ToListAsync(Ct)));

        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(cancel, null, Ct), HttpStatusCode.Conflict, ErrorCodes.RunNotActive);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync($"/api/pipeline-runs/{run.Id}/retry", null, Ct), HttpStatusCode.Conflict, ErrorCodes.RunNotActive);
    }

    [Fact]
    public async Task FR333_rollback_to_an_invalid_checkpoint_is_INVALID_TARGET_VERSION_and_a_valid_one_re_enters_its_stage()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", Start(), Ct), HttpStatusCode.Created);
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);
        var rollback = $"/api/pipeline-runs/{run.Id}/rollback";

        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync(rollback, new { targetCheckpoint = 9, reason = "Too far" }, Ct), HttpStatusCode.BadRequest, ErrorCodes.InvalidTargetVersion);
        var back = await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Owner.Client.PostAsJsonAsync(rollback, new { targetCheckpoint = 1, reason = "Redo" }, Ct));
        Assert.Equal(("draft", 1), (back.CurrentStage, back.Checkpoint));
        Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
    }

    [Fact]
    public async Task FR335_a_run_without_its_input_waits_on_input_and_continues_when_it_is_submitted()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", new { pipelineId = FixtureLinearPack.PipelineId, approvalMode = "advisory" }, Ct),
            HttpStatusCode.Created);
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);
        var waiting = await GetRunAsync(fixture.Owner.Client, run.Id);
        Assert.Equal(("draft", GenericRunStatus.WaitingOnInput), (waiting.CurrentStage, waiting.Status));
        Assert.Equal(0, await AgentRunCountAsync(app, run.Id));

        var invalid = await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync($"/api/pipeline-runs/{run.Id}/inputs/brief", new { words = 0 }, Ct),
            HttpStatusCode.BadRequest, ErrorCodes.InputInvalid);
        Assert.Contains(invalid.GetProperty("details").EnumerateArray(), d => d.GetString()!.StartsWith("/words:", StringComparison.Ordinal));
        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync($"/api/pipeline-runs/{run.Id}/inputs/draft", new { topic = "x" }, Ct),
            HttpStatusCode.NotFound, ErrorCodes.NotFound);

        var version = await ProjectApi.ReadAsync<PipelineArtifactVersionDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"/api/pipeline-runs/{run.Id}/inputs/brief", new { topic = "Tides" }, Ct), HttpStatusCode.Created);
        Assert.Equal(1, version.Version);
        Assert.Equal(GenericRunStatus.Active, (await ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id, Ct))).Status);

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);
        var moved = await GetRunAsync(fixture.Owner.Client, run.Id);
        Assert.Equal(("approve", 2), (moved.CurrentStage, moved.AgentRuns));
    }

    [Fact]
    public async Task FR331_an_input_that_fails_its_schema_is_INPUT_INVALID_with_the_paths_and_an_undeclared_key_is_VALIDATION()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var invalid = await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", Start(new { brief = new { words = "many" } }), Ct),
            HttpStatusCode.BadRequest, ErrorCodes.InputInvalid);
        Assert.Contains(invalid.GetProperty("details").EnumerateArray(), d => d.GetString()!.StartsWith("/words:", StringComparison.Ordinal));
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", Start(new { notes = "x" }), Ct),
            HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.Empty(await ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().Where(r => r.ProjectId == fixture.Project.Id && r.PipelineId == FixtureLinearPack.PipelineId).ToListAsync(Ct)));
    }

    [Fact]
    public async Task FR331_D_0_8_0_18_two_concurrent_starts_give_one_run_and_RUN_ALREADY_ACTIVE()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var url = $"{fixture.Path}/pipeline-runs";

        var responses = await Task.WhenAll(
            fixture.Owner.Client.PostAsJsonAsync(url, Start(), Ct),
            fixture.Contributor.Client.PostAsJsonAsync(url, Start(), Ct));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        await Problems.AssertAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, ErrorCodes.RunAlreadyActive);
        Assert.Single(await ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().Where(r => r.ProjectId == fixture.Project.Id && r.PipelineId == FixtureLinearPack.PipelineId).ToListAsync(Ct)));
    }

    [Fact]
    public async Task FR333_retry_is_CONFLICT_while_the_run_waits_on_a_gate()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", Start(), Ct), HttpStatusCode.Created);
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync($"/api/pipeline-runs/{run.Id}/retry", null, Ct), HttpStatusCode.Conflict, ErrorCodes.Conflict);
        Assert.Empty(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
    }

    [Fact]
    public async Task FR335_an_input_submitted_while_the_run_is_active_keeps_one_driver_job_and_the_run_continues()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", new { pipelineId = FixtureLinearPack.PipelineId, approvalMode = "advisory" }, Ct),
            HttpStatusCode.Created);

        await ProjectApi.ReadAsync<PipelineArtifactVersionDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"/api/pipeline-runs/{run.Id}/inputs/brief", new { topic = "Tides" }, Ct), HttpStatusCode.Created);

        Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);
        Assert.Equal("approve", (await GetRunAsync(fixture.Owner.Client, run.Id)).CurrentStage);
    }

    [Fact]
    public async Task FR332_an_engine_run_is_not_running_because_of_another_runs_jobs()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", Start(), Ct), HttpStatusCode.Created);
        Assert.Equal(GenericRunStatus.Running, (await GetRunAsync(fixture.Owner.Client, run.Id)).Status);

        // Its own driver job is cancelled; a stage.run job naming another run and an SDLC job don't make it running.
        await ProjectApi.WithDbAsync(app, async db =>
        {
            await db.Jobs.Where(j => j.ProjectId == fixture.Project.Id && j.Type == EngineJobTypes.StageRun)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled), Ct);
            db.Jobs.Add(new Job { ProjectId = fixture.Project.Id, Type = EngineJobTypes.StageRun, Payload = $$"""{"runId":"{{Guid.NewGuid()}}","stageExecutionId":"{{Guid.NewGuid()}}"}""", RunAfter = DateTimeOffset.UtcNow.AddHours(1), CreatedAt = DateTimeOffset.UtcNow });
            db.Jobs.Add(new Job { ProjectId = fixture.Project.Id, Type = SdlcJobTypes.Review, Payload = "{}", RunAfter = DateTimeOffset.UtcNow.AddHours(1), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(GenericRunStatus.Active, (await GetRunAsync(fixture.Owner.Client, run.Id)).Status);
    }

    private static async Task<PipelineRunDto> GetRunAsync(HttpClient client, Guid runId) =>
        await ProjectApi.ReadAsync<PipelineRunDto>(await client.GetAsync($"/api/pipeline-runs/{runId}", Ct));

    private static Task<int> AgentRunCountAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.AgentRuns.AsNoTracking().CountAsync(r => r.PipelineRunId == runId, Ct));
}
