using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>Every tool-loop turn the Fake provider was asked for, by agent (FR-359: counts model calls, shows tool results).</summary>
public sealed class FakeTurnLog : IFakeLlmObserver
{
    private readonly ConcurrentQueue<LlmToolRequest> _turns = new();

    public IReadOnlyList<LlmToolRequest> Of(string agent) => [.. _turns.Where(t => t.Request.Agent == agent)];

    public void OnCompletion(LlmRequest request)
    {
    }

    public void OnTurn(LlmToolRequest toolRequest) => _turns.Enqueue(toolRequest);
}

/// <summary>
/// FR-359, §4.73 end to end on <c>fixture-oncall</c> and the Fake provider: the executor's destructive <c>restart-pod</c>
/// raises a <c>tool_approval</c> gate and nothing runs; an Owner's approval runs it exactly once and the loop resumes with
/// no repeated model call; a rejection or the 24-hour timer sends the denial; a Contributor can't decide; autonomous mode
/// still waits; a redelivery while pending calls no model; an oversized loop fails with <c>TOOL_STATE_TOO_LARGE</c>; and a
/// cancel while pending closes the gate.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ToolApprovalTests(TevrinApiFactory factory)
{
    private const string Executor = "executor";
    private const string RestartArguments = """{"pod":"api-0","reason":"api-0 is out of memory"}""";

    /// <summary>A fake time after today (sessions signed in at it stay valid; see <see cref="TriggerTests"/>).</summary>
    private static readonly DateTimeOffset Start = new(2030, 12, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> App(TevrinApiFactory factory, FakeTurnLog turns, TimeProvider? clock = null, string? readArguments = null) =>
        FixtureFlowApp.Create(
                factory,
                new PositionLimit(),
                new RecordingResponder("investigator", "The pod is out of memory."),
                new RecordingResponder(Executor, "Pod restarted."))
            .WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IFakeToolScript>(new FakeToolScript("investigator", [new FakeToolCall(ReadLogsTool.ToolName, """{"service":"api"}""")]));
                s.AddSingleton<IFakeToolScript>(new FakeToolScript(Executor,
                [
                    new FakeToolCall(ReadLogsTool.ToolName, readArguments ?? """{"service":"api"}"""),
                    new FakeToolCall(RestartPodTool.ToolName, RestartArguments),
                ]));
                s.AddSingleton<IFakeLlmObserver>(turns);
                if (clock is not null)
                {
                    s.AddSingleton(clock);
                }
            }));

    private static async Task<PipelineRunDto> StartAsync(ProjectFixture fixture, string mode = "advisory") =>
        await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs",
                new { pipelineId = FlowFixturePack.OnCall, approvalMode = mode, inputs = new { alert = new { service = "api", severity = "page" } } },
                Ct),
            HttpStatusCode.Created);

    /// <summary>Runs the project's due <c>stage.run</c> jobs (run-after reached on the app's clock) until none is due.</summary>
    private static async Task DriveDueAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        var time = app.Services.GetRequiredService<TimeProvider>();
        for (var round = 0; round < 20; round++)
        {
            var due = (await FixtureLinearApp.QueuedDriverJobsAsync(app, projectId)).Where(j => j.RunAfter <= time.GetUtcNow()).ToList();
            if (due.Count == 0)
            {
                return;
            }

            foreach (var job in due)
            {
                await AgentJobs.RunCoreAsync(app, job);
            }
        }
    }

    private static Task<List<ApprovalGate>> GatesAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.ApprovalGates.AsNoTracking().Where(g => g.PipelineRunId == runId).ToListAsync(Ct));

    private static Task<AgentRun> ExecutorRunAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.AgentRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == runId && r.Agent == Executor, Ct));

    private static async Task<PipelineRunDto> RunAsync(ProjectFixture fixture, Guid runId) =>
        await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Owner.Client.GetAsync($"/api/pipeline-runs/{runId}", Ct));

    private static string Decide(Guid runId, Guid gateId, string decision) => $"/api/pipeline-runs/{runId}/gates/{gateId}/{decision}";

    private static int Restarts(OnCallLog log) => log.Ran.Count(t => t == RestartPodTool.ToolName);

    /// <summary>Starts a run and drives it to the pending <c>tool_approval</c> gate.</summary>
    private static async Task<(PipelineRunDto Run, ApprovalGate Gate)> PendingAsync(WebApplicationFactory<Program> app, ProjectFixture fixture, string mode = "advisory")
    {
        var run = await StartAsync(fixture, mode);
        await DriveDueAsync(app, fixture.Project.Id);
        var gate = Assert.Single(await GatesAsync(app, run.Id));
        Assert.Equal((EngineGates.ToolApproval, GateStatus.Pending), (gate.Type, gate.Status));
        return (run, gate);
    }

    [Fact]
    public async Task FR359_restart_pod_raises_tool_approval_and_an_owners_approval_runs_it_exactly_once_without_repeating_earlier_turns()
    {
        var turns = new FakeTurnLog();
        await using var app = App(factory, turns);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<OnCallLog>();

        var (run, gate) = await PendingAsync(app, fixture);

        // Pending: nothing destructive ran, the run waits on the gate, and the gate shows the call.
        Assert.Equal([ReadLogsTool.ToolName, ReadLogsTool.ToolName], log.Ran);
        Assert.Equal(GenericRunStatus.WaitingOnGate, (await RunAsync(fixture, run.Id)).Status);
        Assert.Equal(2, turns.Of(Executor).Count);
        using (var payload = JsonDocument.Parse(gate.Payload!))
        {
            Assert.Equal(FlowFixturePack.RestartPodTool, payload.RootElement.GetProperty("tool").GetString());
            Assert.Equal("destructive", payload.RootElement.GetProperty("tier").GetString());
            Assert.Equal(RestartArguments, payload.RootElement.GetProperty("arguments").GetString());
            Assert.Equal("api-0 is out of memory", payload.RootElement.GetProperty("reason").GetString());
        }

        var listed = Assert.Single(await ProjectApi.ReadAsync<List<PipelineGateDto>>(await fixture.Owner.Client.GetAsync($"/api/pipeline-runs/{run.Id}/gates", Ct)));
        Assert.Equal((EngineGates.ToolApproval, GateStatus.Pending), (listed.Type, listed.Status));

        var approved = await ProjectApi.ReadAsync<PipelineGateDto>(
            await fixture.Owner.Client.PostAsJsonAsync(Decide(run.Id, gate.Id, "approve"), new { comment = "Go ahead" }, Ct));
        Assert.Equal(GateStatus.Approved, approved.Status);
        Assert.Equal(0, Restarts(log));

        await DriveDueAsync(app, fixture.Project.Id);

        Assert.Equal(1, Restarts(log));
        var executorTurns = turns.Of(Executor);
        Assert.Equal(3, executorTurns.Count);
        var resumed = executorTurns[^1];
        Assert.Equal(2, resumed.Exchanges.Count);
        Assert.Equal(("ran " + RestartPodTool.ToolName, false), (resumed.Exchanges[^1].Results[^1].Content, resumed.Exchanges[^1].Results[^1].IsError));
        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(fixture, run.Id)).Status);
        var executorRun = await ExecutorRunAsync(app, run.Id);
        Assert.Equal((AgentRunStatus.Succeeded, (string?)null), (executorRun.Status, executorRun.ToolLoopState));

        // Redelivering the finished job runs nothing again.
        var jobs = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == fixture.Project.Id && j.Type == EngineJobTypes.StageRun).ToListAsync(Ct));
        foreach (var job in jobs)
        {
            await AgentJobs.RunCoreAsync(app, job);
        }

        Assert.Equal(1, Restarts(log));
        Assert.Equal(3, turns.Of(Executor).Count);
    }

    [Fact]
    public async Task FR359_a_rejection_sends_the_model_denied_by_the_user_and_the_tool_never_runs()
    {
        var turns = new FakeTurnLog();
        await using var app = App(factory, turns);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<OnCallLog>();
        var (run, gate) = await PendingAsync(app, fixture);

        var rejected = await ProjectApi.ReadAsync<PipelineGateDto>(
            await fixture.Owner.Client.PostAsJsonAsync(Decide(run.Id, gate.Id, "reject"), new { comment = "Not during the release" }, Ct));
        await DriveDueAsync(app, fixture.Project.Id);

        Assert.Equal(0, Restarts(log));
        var denial = turns.Of(Executor)[^1].Exchanges[^1].Results[^1];
        Assert.Equal(($"denied by {rejected.DecidedByUserId}: Not during the release", true), (denial.Content, denial.IsError));
        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(fixture, run.Id)).Status);
    }

    [Fact]
    public async Task FR359_a_contributor_cannot_decide_a_tool_approval_gate()
    {
        var turns = new FakeTurnLog();
        await using var app = App(factory, turns);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<OnCallLog>();
        var (run, gate) = await PendingAsync(app, fixture);

        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync(Decide(run.Id, gate.Id, "approve"), new { comment = "Me" }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync(Decide(run.Id, gate.Id, "reject"), new { comment = "Me" }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await DriveDueAsync(app, fixture.Project.Id);

        Assert.Equal(GateStatus.Pending, Assert.Single(await GatesAsync(app, run.Id)).Status);
        Assert.Equal(0, Restarts(log));
    }

    [Fact]
    public async Task FR359_no_decision_within_24_hours_is_rejected_by_the_gate_timer()
    {
        var clock = new TriggerClock(Start);
        var turns = new FakeTurnLog();
        await using var app = App(factory, turns, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<OnCallLog>();
        var (run, _) = await PendingAsync(app, fixture);

        clock.Advance(TimeSpan.FromHours(23));
        await DriveDueAsync(app, fixture.Project.Id);
        Assert.Equal(GateStatus.Pending, Assert.Single(await GatesAsync(app, run.Id)).Status);

        clock.Advance(TimeSpan.FromHours(1));
        await DriveDueAsync(app, fixture.Project.Id);

        var gate = Assert.Single(await GatesAsync(app, run.Id));
        Assert.Equal((GateStatus.Rejected, ToolApprovalRules.TimeoutComment, (Guid?)null), (gate.Status, gate.Comment, gate.ReviewerId));
        Assert.Equal(0, Restarts(log));
        Assert.Equal("denied by system: no decision within 24 hours", turns.Of(Executor)[^1].Exchanges[^1].Results[^1].Content);
    }

    [Fact]
    public async Task FR359_autonomous_mode_still_raises_the_gate_and_a_redelivery_while_pending_calls_no_model()
    {
        var turns = new FakeTurnLog();
        await using var app = App(factory, turns);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<OnCallLog>();
        var (run, _) = await PendingAsync(app, fixture, mode: "autonomous");
        var waiting = Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));

        // Redelivered three times (as after a lost lease): each re-defers with no model call.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await AgentJobs.RunCoreAsync(app, waiting));
        }

        Assert.Equal(2, turns.Of(Executor).Count);
        Assert.Equal(0, Restarts(log));
        Assert.Equal(GateStatus.Pending, Assert.Single(await GatesAsync(app, run.Id)).Status);
        Assert.Equal(GenericRunStatus.WaitingOnGate, (await RunAsync(fixture, run.Id)).Status);
    }

    [Fact]
    public async Task FR359_a_tool_loop_over_1_MB_fails_with_TOOL_STATE_TOO_LARGE_and_the_tool_never_runs()
    {
        var turns = new FakeTurnLog();
        var huge = $$"""{"service":"{{new string('a', ToolApprovalRules.MaxStateBytes + 10)}}"}""";
        await using var app = App(factory, turns, readArguments: huge);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<OnCallLog>();

        var run = await StartAsync(fixture);
        await DriveDueAsync(app, fixture.Project.Id);

        Assert.Empty(await GatesAsync(app, run.Id));
        Assert.Equal(0, Restarts(log));
        var executorRun = await ExecutorRunAsync(app, run.Id);
        Assert.Equal(AgentRunStatus.Failed, executorRun.Status);
        Assert.StartsWith(ErrorCodes.ToolStateTooLarge, executorRun.Error, StringComparison.Ordinal);
        Assert.Null(executorRun.ToolLoopState);
        var failed = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == fixture.Project.Id && j.Type == EngineJobTypes.StageRun && j.Status == JobStatus.Failed).ToListAsync(Ct));
        Assert.Contains(failed, j => j.LastError!.StartsWith(ErrorCodes.ToolStateTooLarge, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FR359_a_stage_run_job_that_ends_without_succeeding_closes_only_the_call_it_saved(bool savedByTheJob)
    {
        var turns = new FakeTurnLog();
        await using var app = App(factory, turns);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<OnCallLog>();
        var (run, gate) = await PendingAsync(app, fixture);
        var waiting = Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));

        // The other case ends the run's earlier (investigate) job: same run, but it didn't save the call.
        var ended = savedByTheJob
            ? waiting
            : await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
                .SingleAsync(j => j.ProjectId == fixture.Project.Id && j.Type == EngineJobTypes.StageRun && j.Id != waiting.Id, Ct));
        await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Id == ended.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Failed).SetProperty(j => j.LastError, "DEFINITION_UNAVAILABLE: gone"), Ct));

        // As the worker does: every job-ended hook adds its changes, then one save.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tevrin.Engine.Runtime.Persistence.IEngineDbContext>();
            var notice = new JobEnded(ended.Id, EngineJobTypes.StageRun, fixture.Project.Id, DeadLettered: false, "DEFINITION_UNAVAILABLE: gone");
            foreach (var hook in scope.ServiceProvider.GetServices<IJobEndedHook>())
            {
                await hook.OnJobEndedAsync(db, notice, Ct);
            }

            await db.SaveChangesAsync(Ct);
        }

        var after = Assert.Single(await GatesAsync(app, run.Id));
        var executorRun = await ExecutorRunAsync(app, run.Id);
        if (savedByTheJob)
        {
            Assert.Equal(GateStatus.Cancelled, after.Status);
            Assert.Equal((AgentRunStatus.Failed, (string?)null), (executorRun.Status, executorRun.ToolLoopState));
        }
        else
        {
            Assert.Equal(GateStatus.Pending, after.Status);
            Assert.Equal(AgentRunStatus.Running, executorRun.Status);
            Assert.NotNull(executorRun.ToolLoopState);
        }

        Assert.Equal(gate.Id, after.Id);
        Assert.Equal(0, Restarts(log));
    }

    [Fact]
    public async Task FR359_cancelling_the_run_while_pending_closes_the_gate_and_the_tool_never_runs()
    {
        var turns = new FakeTurnLog();
        await using var app = App(factory, turns);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<OnCallLog>();
        var (run, gate) = await PendingAsync(app, fixture);

        await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Owner.Client.PostAsync($"/api/pipeline-runs/{run.Id}/cancel", null, Ct));
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync(Decide(run.Id, gate.Id, "approve"), new { comment = "Too late" }, Ct), HttpStatusCode.Conflict, ErrorCodes.RunNotActive);
        await DriveDueAsync(app, fixture.Project.Id);

        Assert.Equal(GateStatus.Cancelled, Assert.Single(await GatesAsync(app, run.Id)).Status);
        Assert.Empty(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
        var executorRun = await ExecutorRunAsync(app, run.Id);
        Assert.Equal((AgentRunStatus.Cancelled, (string?)null), (executorRun.Status, executorRun.ToolLoopState));
        Assert.Equal(0, Restarts(log));
        Assert.Equal(2, turns.Of(Executor).Count);
    }
}
