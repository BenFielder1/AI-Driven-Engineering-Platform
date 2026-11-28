using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Api;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-358 runtime (M71b) end to end on the Fake provider, with <c>fixture-oncall</c>: the investigator's <c>read</c> tool
/// runs and its call records tier <c>read</c>; the executor's destructive <c>restart-pod</c> is offered (its stage sets
/// <c>max_tier: destructive</c>) and waits on a <c>tool_approval</c> gate, never run from the model's call (FR-359,
/// <see cref="ToolApprovalTests"/>).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ToolTierTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> App(TevrinApiFactory factory) =>
        FixtureFlowApp.Create(
                factory,
                new PositionLimit(),
                new RecordingResponder("investigator", "The pod is out of memory."),
                new RecordingResponder("executor", "Restart requested."))
            .WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IFakeToolScript>(new FakeToolScript("investigator", [new FakeToolCall(ReadLogsTool.ToolName, """{"service":"api"}""")]));
                s.AddSingleton<IFakeToolScript>(new FakeToolScript("executor", [new FakeToolCall(RestartPodTool.ToolName, """{"pod":"api-0"}""")]));
            }));

    private static List<(string Tool, string? Tier, bool IsError)> Calls(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => (
            e.GetProperty("tool").GetString()!,
            e.TryGetProperty("tier", out var tier) ? tier.GetString() : null,
            e.GetProperty("isError").GetBoolean()))];
    }

    [Fact]
    public async Task FR358_the_oncall_definition_registers_in_the_api_host()
    {
        await using var app = App(factory);

        var registry = app.Services.GetRequiredService<PipelineRegistry>();

        Assert.True(registry.TryGet(FlowFixturePack.OnCall, out var loaded), string.Join(" | ", registry.Errors.Select(e => e.Error.ToString())));
        Assert.Empty(PipelineDefinitionResolver.MissingNames(registry, loaded.Definition!));
        Assert.Equal(Tevrin.Engine.Domain.Pipelines.ToolTier.Destructive, registry.ToolTierOf(FlowFixturePack.RestartPodTool));
    }

    [Fact]
    public async Task FR358_FR359_read_logs_runs_with_tier_read_and_restart_pod_waits_on_its_gate_and_never_runs()
    {
        await using var app = App(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var log = app.Services.GetRequiredService<OnCallLog>();

        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs",
                new { pipelineId = FlowFixturePack.OnCall, approvalMode = "autonomous", inputs = new { alert = new { service = "api", severity = "page" } } },
                Ct),
            HttpStatusCode.Created);
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var agentRuns = await ProjectApi.WithDbAsync(app, db => db.AgentRuns.AsNoTracking().Where(r => r.PipelineRunId == run.Id).ToListAsync(Ct));
        var investigator = Assert.Single(agentRuns, r => r.Agent == "investigator");
        var executor = Assert.Single(agentRuns, r => r.Agent == "executor");
        Assert.Equal([(ReadLogsTool.ToolName, "read", false)], Calls(investigator.ToolCalls));

        // The destructive call isn't recorded until its gate is decided; the run waits on the gate.
        Assert.Empty(Calls(executor.ToolCalls));
        Assert.NotNull(executor.ToolLoopState);
        var gate = await ProjectApi.WithDbAsync(app, db => db.ApprovalGates.AsNoTracking().SingleAsync(g => g.PipelineRunId == run.Id, Ct));
        Assert.Equal((EngineGates.ToolApproval, GateStatus.Pending), (gate.Type, gate.Status));

        // The destructive tool never ran, even in autonomous mode; only the investigator's read did.
        Assert.Equal([ReadLogsTool.ToolName], log.Ran);
    }
}
