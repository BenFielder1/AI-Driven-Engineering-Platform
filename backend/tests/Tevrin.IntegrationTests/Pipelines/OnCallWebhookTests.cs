using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Domain.Triggers;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-361 end to end on <c>fixture-oncall</c> and the Fake provider: a signed alert webhook (FR-337) starts the run with
/// the alert as its input; the investigator reads logs with a <c>read</c> tool (FR-358); the executor's destructive
/// <c>restart-pod</c> waits on a <c>tool_approval</c> gate (FR-359) and runs once after an Owner approves. Bad signatures,
/// replays and rotation are covered generically by <see cref="TriggerTests"/>; gate decisions, rejection and the timer by
/// <see cref="ToolApprovalTests"/>.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class OnCallWebhookTests(TevrinApiFactory factory)
{
    private const string Investigator = "investigator";
    private const string Executor = "executor";
    private const string AlertBody = """{"service":"api","severity":"page"}""";
    private const string RestartArguments = """{"pod":"api-0","reason":"api-0 is out of memory"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> App(TevrinApiFactory factory, FakeTurnLog turns) =>
        FixtureFlowApp.Create(
                factory,
                new PositionLimit(),
                new RecordingResponder(Investigator, "The pod is out of memory."),
                new RecordingResponder(Executor, "Pod restarted."))
            .WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IFakeToolScript>(new FakeToolScript(Investigator, [new FakeToolCall(ReadLogsTool.ToolName, """{"service":"api"}""")]));
                s.AddSingleton<IFakeToolScript>(new FakeToolScript(Executor,
                [
                    new FakeToolCall(ReadLogsTool.ToolName, """{"service":"api"}"""),
                    new FakeToolCall(RestartPodTool.ToolName, RestartArguments),
                ]));
                s.AddSingleton<IFakeLlmObserver>(turns);
            }));

    [Fact]
    public async Task FR361_a_signed_alert_webhook_starts_the_on_call_run_and_the_executor_waits_for_approval_before_restart_pod()
    {
        var turns = new FakeTurnLog();
        await using var app = App(factory, turns);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<OnCallLog>();
        var clock = app.Services.GetRequiredService<TimeProvider>();

        // The Owner creates the webhook for fixture-oncall; a correctly signed alert starts a run.
        var webhook = await ProjectApi.ReadAsync<CreatedPipelineWebhookDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-webhooks", new { pipelineId = FlowFixturePack.OnCall }, Ct),
            HttpStatusCode.Created);
        using var anonymous = app.CreateDefaultClient(TevrinApiFactory.BaseUri);
        var delivery = Guid.NewGuid();
        var signature = PipelineWebhookSignature.Header(webhook.Secret, clock.GetUtcNow().ToUnixTimeSeconds(), Encoding.UTF8.GetBytes(AlertBody));
        var accepted = await ProjectApi.ReadAsync<PipelineWebhookAcceptedDto>(
            await PostHookAsync(anonymous, webhook.Id, AlertBody, signature, delivery), HttpStatusCode.Accepted);

        var run = await ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking()
            .SingleAsync(r => r.ProjectId == fixture.Project.Id && r.PipelineId == FlowFixturePack.OnCall, Ct));
        Assert.Equal(accepted.RunId, run.Id);
        var alert = await ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking().SingleAsync(a => a.PipelineRunId == run.Id && a.Type == "alert", Ct));
        using (var content = JsonDocument.Parse(alert.Content))
        {
            Assert.Equal(("api", "page"), (content.RootElement.GetProperty("service").GetString(), content.RootElement.GetProperty("severity").GetString()));
        }

        var recorded = await ProjectApi.WithDbAsync(app, db => db.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.WebhookId == webhook.Id, Ct));
        Assert.Equal((delivery, WebhookDeliveryStatuses.Started, (Guid?)run.Id), (recorded.DeliveryId, recorded.Status, recorded.RunId));

        // The investigator reads logs (tier read); the executor reads too, then asks to restart the pod: a gate, no restart.
        await DriveDueAsync(app, fixture.Project.Id);
        var investigator = await AgentRunAsync(app, run.Id, Investigator);
        Assert.Equal([(ReadLogsTool.ToolName, "read", false)], Calls(investigator.ToolCalls));
        Assert.Equal([ReadLogsTool.ToolName, ReadLogsTool.ToolName], log.Ran);

        var gate = Assert.Single(await ProjectApi.WithDbAsync(app, db => db.ApprovalGates.AsNoTracking().Where(g => g.PipelineRunId == run.Id).ToListAsync(Ct)));
        Assert.Equal((EngineGates.ToolApproval, GateStatus.Pending), (gate.Type, gate.Status));
        using (var payload = JsonDocument.Parse(gate.Payload!))
        {
            Assert.Equal(FlowFixturePack.RestartPodTool, payload.RootElement.GetProperty("tool").GetString());
            Assert.Equal("destructive", payload.RootElement.GetProperty("tier").GetString());
            Assert.Equal("api-0 is out of memory", payload.RootElement.GetProperty("reason").GetString());
        }

        Assert.Equal(GenericRunStatus.WaitingOnGate, (await RunAsync(fixture, run.Id)).Status);
        Assert.NotNull((await AgentRunAsync(app, run.Id, Executor)).ToolLoopState);

        // An Owner approves: restart-pod runs once, the loop resumes without repeating earlier turns, and the run completes.
        var approved = await ProjectApi.ReadAsync<PipelineGateDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"/api/pipeline-runs/{run.Id}/gates/{gate.Id}/approve", new { comment = "Restart it" }, Ct));
        Assert.Equal(GateStatus.Approved, approved.Status);
        Assert.DoesNotContain(RestartPodTool.ToolName, log.Ran);

        await DriveDueAsync(app, fixture.Project.Id);

        Assert.Single(log.Ran, t => t == RestartPodTool.ToolName);
        Assert.Equal(3, turns.Of(Executor).Count);
        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(fixture, run.Id)).Status);
        var executor = await AgentRunAsync(app, run.Id, Executor);
        Assert.Equal((AgentRunStatus.Succeeded, (string?)null), (executor.Status, executor.ToolLoopState));
        Assert.Contains((RestartPodTool.ToolName, "destructive", false), Calls(executor.ToolCalls));
    }

    private static async Task<HttpResponseMessage> PostHookAsync(HttpClient client, Guid webhookId, string body, string signature, Guid delivery)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/hooks/pipelines/{webhookId}")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) { Headers = { { "Content-Type", "application/json" } } },
        };
        request.Headers.Add(PipelineWebhookSignature.SignatureHeader, signature);
        request.Headers.Add(PipelineWebhookSignature.DeliveryHeader, delivery.ToString());
        return await client.SendAsync(request, Ct);
    }

    /// <summary>Runs the project's due <c>stage.run</c> jobs (run-after reached) until none is due.</summary>
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

    private static Task<AgentRun> AgentRunAsync(WebApplicationFactory<Program> app, Guid runId, string agent) =>
        ProjectApi.WithDbAsync(app, db => db.AgentRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == runId && r.Agent == agent, Ct));

    private static async Task<PipelineRunDto> RunAsync(ProjectFixture fixture, Guid runId) =>
        await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Owner.Client.GetAsync($"/api/pipeline-runs/{runId}", Ct));

    private static List<(string Tool, string? Tier, bool IsError)> Calls(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => (
            e.GetProperty("tool").GetString()!,
            e.TryGetProperty("tier", out var tier) ? tier.GetString() : null,
            e.GetProperty("isError").GetBoolean()))];
    }
}
