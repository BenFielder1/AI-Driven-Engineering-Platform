using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-360 end to end on <c>fixture-cv</c> (M71d): the external <c>submit</c> handler waits on an <c>external_action</c>
/// gate holding exactly its payload and sends nothing before an Owner approves; approval sends that stored payload exactly
/// once, and redeliveries never resend; a rejection follows <c>on_reject</c> back to the tailor, and once its cap is reached
/// fails the stage with <c>EXTERNAL_ACTION_REJECTED</c>; a Contributor can't decide; autonomous mode still waits; the
/// 24-hour timer rejects; and a job that ends without succeeding closes its gate.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExternalActionTests(TevrinApiFactory factory)
{
    private const string Submit = "submit";

    /// <summary>A fake time after today (sessions signed in at it stay valid; see <see cref="TriggerTests"/>).</summary>
    private static readonly DateTimeOffset Start = new(2030, 12, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> App(TevrinApiFactory factory, TimeProvider? clock = null)
    {
        var app = FixtureCvApp.Create(factory);
        return clock is null ? app : app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(clock)));
    }

    private static async Task<PipelineRunDto> StartAsync(ProjectFixture fixture, string mode = "advisory")
    {
        var path = $"{fixture.Path}/pipelines/{FixtureCvPack.PipelineId}/artifacts/master_cv/versions";
        var added = await ProjectApi.ReadAsync<PipelineArtifactVersionDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(path, new { content = "CV v1" }, Ct), HttpStatusCode.Created);
        await ProjectApi.ReadAsync<PipelineArtifactVersionDto>(await fixture.Owner.Client.PostAsync($"{path}/{added.Version}/lock", null, Ct));
        return await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs",
                new { pipelineId = FixtureCvPack.PipelineId, approvalMode = mode, inputs = new { job = "Staff engineer, platform team" } },
                Ct),
            HttpStatusCode.Created);
    }

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
        ProjectApi.WithDbAsync(app, db => db.ApprovalGates.AsNoTracking().Where(g => g.PipelineRunId == runId).OrderBy(g => g.CreatedAt).ToListAsync(Ct));

    private static async Task<PipelineRunDto> RunAsync(ProjectFixture fixture, Guid runId) =>
        await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Owner.Client.GetAsync($"/api/pipeline-runs/{runId}", Ct));

    private static string Decide(Guid runId, Guid gateId, string decision) => $"/api/pipeline-runs/{runId}/gates/{gateId}/{decision}";

    private static Task<List<Job>> StageJobsAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == EngineJobTypes.StageRun).ToListAsync(Ct));

    /// <summary>Starts a run and drives it to the pending <c>external_action</c> gate of <c>submit</c>.</summary>
    private static async Task<(PipelineRunDto Run, ApprovalGate Gate)> PendingAsync(WebApplicationFactory<Program> app, ProjectFixture fixture, string mode = "advisory")
    {
        var run = await StartAsync(fixture, mode);
        await DriveDueAsync(app, fixture.Project.Id);
        var gate = Assert.Single(await GatesAsync(app, run.Id));
        Assert.Equal((EngineGates.ExternalAction, GateStatus.Pending), (gate.Type, gate.Status));
        return (run, gate);
    }

    [Fact]
    public async Task FR360_submit_waits_on_external_action_sends_nothing_and_the_gate_payload_is_what_approval_sends_exactly_once()
    {
        await using var app = App(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<SubmissionLog>();

        var (run, gate) = await PendingAsync(app, fixture);

        // Pending: the payload was prepared once and nothing was sent; the run waits on the gate, at submit.
        Assert.Empty(log.Sent);
        var prepared = Assert.Single(log.Prepared);
        var waiting = await RunAsync(fixture, run.Id);
        Assert.Equal((GenericRunStatus.WaitingOnGate, Submit), (waiting.Status, waiting.CurrentStage));
        var payload = ExternalActionRules.ReadGatePayload(gate.Payload)!;
        Assert.Equal((SubmitApplicationHandler.Name, Submit, prepared, (DateTimeOffset?)null), (payload.Handler, payload.Stage, payload.Payload, payload.RanAt));
        var listed = Assert.Single(await ProjectApi.ReadAsync<List<PipelineGateDto>>(await fixture.Owner.Client.GetAsync($"/api/pipeline-runs/{run.Id}/gates", Ct)));
        Assert.Equal((EngineGates.ExternalAction, GateStatus.Pending), (listed.Type, listed.Status));

        // Redelivered while pending (as after a lost lease): nothing is prepared again or sent.
        var job = Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await AgentJobs.RunCoreAsync(app, job));
        }

        Assert.Equal((1, 0), (log.Prepared.Count, log.Sent.Count));

        var approved = await ProjectApi.ReadAsync<PipelineGateDto>(
            await fixture.Owner.Client.PostAsJsonAsync(Decide(run.Id, gate.Id, "approve"), new { comment = "Send it" }, Ct));
        Assert.Equal(GateStatus.Approved, approved.Status);
        Assert.Empty(log.Sent);

        await DriveDueAsync(app, fixture.Project.Id);

        // The stored payload, exactly what the gate showed, was sent once; the claim is on the gate.
        Assert.Equal([prepared], log.Sent);
        Assert.Single(log.Prepared);
        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(fixture, run.Id)).Status);
        var done = ExternalActionRules.ReadGatePayload(Assert.Single(await GatesAsync(app, run.Id)).Payload)!;
        Assert.NotNull(done.RanAt);
        Assert.NotNull(done.SentAt);

        // Redelivering every finished job sends nothing again.
        foreach (var finished in await StageJobsAsync(app, fixture.Project.Id))
        {
            await AgentJobs.RunCoreAsync(app, finished);
        }

        Assert.Equal([prepared], log.Sent);
    }

    [Fact]
    public async Task FR360_a_claimed_send_whose_job_stopped_before_saving_is_never_sent_again()
    {
        await using var app = App(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<SubmissionLog>();
        var (run, gate) = await PendingAsync(app, fixture);
        await fixture.Owner.Client.PostAsJsonAsync(Decide(run.Id, gate.Id, "approve"), new { comment = "Send it" }, Ct);

        // As if an earlier delivery committed its claim and stopped before the stage finished.
        await ProjectApi.WithDbAsync(app, db => db.ApprovalGates.Where(g => g.Id == gate.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Payload, ToolLoopStates.WithRanAt(gate.Payload, DateTimeOffset.UtcNow)), Ct));
        await DriveDueAsync(app, fixture.Project.Id);

        Assert.Empty(log.Sent);
        var failed = Assert.Single(await StageJobsAsync(app, fixture.Project.Id), j => j.Status == JobStatus.Failed);
        Assert.Contains("may have been sent", failed.LastError, StringComparison.Ordinal);
        Assert.Equal(Submit, (await RunAsync(fixture, run.Id)).CurrentStage);
    }

    [Fact]
    public async Task FR360_a_send_recorded_as_sent_whose_transition_did_not_save_only_finishes_the_stage()
    {
        await using var app = App(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<SubmissionLog>();
        var (run, gate) = await PendingAsync(app, fixture);
        await fixture.Owner.Client.PostAsJsonAsync(Decide(run.Id, gate.Id, "approve"), new { comment = "Send it" }, Ct);

        // As if the send completed and its sentAt was saved, then the transition's save conflicted.
        var now = DateTimeOffset.UtcNow;
        var sent = ExternalActionRules.WithSentAt(ToolLoopStates.WithRanAt(gate.Payload, now), now);
        await ProjectApi.WithDbAsync(app, db => db.ApprovalGates.Where(g => g.Id == gate.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.Payload, sent), Ct));
        await DriveDueAsync(app, fixture.Project.Id);

        Assert.Empty(log.Sent);
        Assert.Single(log.Prepared);
        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(fixture, run.Id)).Status);
        Assert.Single(await GatesAsync(app, run.Id));
    }

    [Fact]
    public async Task FR360_a_rejection_follows_on_reject_to_the_tailor_and_past_its_cap_fails_with_EXTERNAL_ACTION_REJECTED_sending_nothing()
    {
        await using var app = App(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<SubmissionLog>();
        var (run, first) = await PendingAsync(app, fixture);

        // First rejection: on_reject goes back to tailor (its second entry, within max_iterations 2), which tailors again,
        // and submit raises a new gate with a newly prepared payload.
        await ProjectApi.ReadAsync<PipelineGateDto>(
            await fixture.Owner.Client.PostAsJsonAsync(Decide(run.Id, first.Id, "reject"), new { comment = "Wrong job" }, Ct));
        await DriveDueAsync(app, fixture.Project.Id);

        var gates = await GatesAsync(app, run.Id);
        Assert.Equal([GateStatus.Rejected, GateStatus.Pending], gates.Select(g => g.Status));
        Assert.Equal((2, 0), (log.Prepared.Count, log.Sent.Count));
        var agentRuns = await ProjectApi.WithDbAsync(app, db => db.AgentRuns.AsNoTracking().Where(r => r.PipelineRunId == run.Id).ToListAsync(Ct));
        Assert.Equal(2, agentRuns.Count(r => r.Stage == "tailor"));
        Assert.Equal(1, await ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().Where(r => r.Id == run.Id).Select(r => r.Revisions).SingleAsync(Ct)));

        // Second rejection: tailor has run twice, its cap, so the stage fails and nothing is sent; the run stays active.
        var rejected = await ProjectApi.ReadAsync<PipelineGateDto>(
            await fixture.Owner.Client.PostAsJsonAsync(Decide(run.Id, gates[1].Id, "reject"), new { comment = "Still wrong" }, Ct));
        await DriveDueAsync(app, fixture.Project.Id);

        Assert.Empty(log.Sent);
        var failed = Assert.Single(await StageJobsAsync(app, fixture.Project.Id), j => j.Status == JobStatus.Failed);
        Assert.StartsWith(ErrorCodes.ExternalActionRejected, failed.LastError, StringComparison.Ordinal);
        Assert.Contains($"denied by {rejected.DecidedByUserId}: Still wrong", failed.LastError, StringComparison.Ordinal);
        var after = await RunAsync(fixture, run.Id);
        Assert.Equal((GenericRunStatus.Active, Submit), (after.Status, after.CurrentStage));
    }

    [Fact]
    public async Task FR360_a_contributor_cannot_decide_an_external_action_gate()
    {
        await using var app = App(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<SubmissionLog>();
        var (run, gate) = await PendingAsync(app, fixture);

        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync(Decide(run.Id, gate.Id, "approve"), new { comment = "Me" }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync(Decide(run.Id, gate.Id, "reject"), new { comment = "Me" }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await DriveDueAsync(app, fixture.Project.Id);

        Assert.Equal(GateStatus.Pending, Assert.Single(await GatesAsync(app, run.Id)).Status);
        Assert.Empty(log.Sent);
    }

    [Fact]
    public async Task FR360_autonomous_mode_still_waits_on_the_external_action_gate()
    {
        await using var app = App(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<SubmissionLog>();

        var (run, _) = await PendingAsync(app, fixture, mode: "autonomous");

        Assert.Empty(log.Sent);
        Assert.Equal(GenericRunStatus.WaitingOnGate, (await RunAsync(fixture, run.Id)).Status);
    }

    [Fact]
    public async Task FR360_no_decision_within_24_hours_is_rejected_by_the_gate_timer_and_nothing_is_sent()
    {
        var clock = new TriggerClock(Start);
        await using var app = App(factory, clock);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<SubmissionLog>();
        var (run, _) = await PendingAsync(app, fixture);

        clock.Advance(TimeSpan.FromHours(23));
        await DriveDueAsync(app, fixture.Project.Id);
        Assert.Equal(GateStatus.Pending, Assert.Single(await GatesAsync(app, run.Id)).Status);

        clock.Advance(TimeSpan.FromHours(1));
        await DriveDueAsync(app, fixture.Project.Id);

        // The timer rejected it as system; on_reject took the run back to the tailor, and submit waits on a new gate.
        var gates = await GatesAsync(app, run.Id);
        Assert.Equal((GateStatus.Rejected, ToolApprovalRules.TimeoutComment, (Guid?)null), (gates[0].Status, gates[0].Comment, gates[0].ReviewerId));
        Assert.Equal(GateStatus.Pending, gates[^1].Status);
        Assert.Empty(log.Sent);
    }

    [Fact]
    public async Task FR360_a_stage_run_job_that_ends_without_succeeding_cancels_the_gate_it_raised()
    {
        await using var app = App(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<SubmissionLog>();
        var (run, gate) = await PendingAsync(app, fixture);
        var waiting = Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
        await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Id == waiting.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Failed).SetProperty(j => j.LastError, "DEFINITION_UNAVAILABLE: gone"), Ct));

        // As the worker does: every job-ended hook adds its changes, then one save.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tevrin.Engine.Runtime.Persistence.IEngineDbContext>();
            var notice = new JobEnded(waiting.Id, EngineJobTypes.StageRun, fixture.Project.Id, DeadLettered: false, "DEFINITION_UNAVAILABLE: gone");
            foreach (var hook in scope.ServiceProvider.GetServices<IJobEndedHook>())
            {
                await hook.OnJobEndedAsync(db, notice, Ct);
            }

            await db.SaveChangesAsync(Ct);
        }

        var after = Assert.Single(await GatesAsync(app, run.Id));
        Assert.Equal((gate.Id, GateStatus.Cancelled), (after.Id, after.Status));
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync(Decide(run.Id, gate.Id, "approve"), new { comment = "Too late" }, Ct), HttpStatusCode.Conflict, ErrorCodes.GateNotPending);
        Assert.Empty(log.Sent);
    }
}
