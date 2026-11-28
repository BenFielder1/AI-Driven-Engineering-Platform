using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-344, FR-347, FR-348, §4.62 step 5, §4.66 (M68): the run driver's veto, routes and run-wide revision cap end to end
/// on the Fake provider, with <c>fixture-veto</c> and <c>fixture-editorial</c>.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class FlowDriverTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Approve = """{ "approve": true }""";

    private static async Task<Guid> StartAsync(ProjectFixture fixture, string pipelineId, object inputs)
    {
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs", new { pipelineId, approvalMode = "advisory", inputs }, Ct),
            HttpStatusCode.Created);
        return run.Id;
    }

    private static Task<PipelineRun> RunAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct));

    private static Task<List<StageExecution>> ExecutionsAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.StageExecutions.AsNoTracking()
            .Where(e => e.PipelineRunId == runId).OrderBy(e => e.EnteredAt).ThenBy(e => e.Id).ToListAsync(Ct));

    private static async Task<int> AgentRunsAsync(WebApplicationFactory<Program> app, Guid projectId, Guid runId, string stage, string agentKey) =>
        (await AgentJobs.RunsAsync(app, projectId)).Count(r => r.PipelineRunId == runId && r.Stage == stage && r.Agent == agentKey);

    [Fact]
    public async Task FR344_FR348_a_risk_manager_veto_stores_its_findings_and_routes_back_to_the_proposal()
    {
        var risk = new ScriptedResponder("risk-manager", """{ "findings": ["Too much risk for one name."] }""", Approve);
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), new ScriptedResponder("sizer", """{ "size": 10 }"""), risk);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Veto, new { signal = "Buy ACME." });

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Completed, "order", 1), (run.Status, run.CurrentStage, run.Revisions));
        Assert.Null(run.Outcome);
        var executions = await ExecutionsAsync(app, runId);
        Assert.Equal(["propose", "size", "propose", "size", "order"], executions.Select(e => e.StageId));
        var vetoed = executions[1];
        Assert.Equal(PipelineTransitionKinds.Route, vetoed.Outcome);
        Assert.Equal(["risk-manager: Too much risk for one name."], JsonSerializer.Deserialize<string[]>(vetoed.VetoFindings!)!);
        Assert.Null(executions[3].VetoFindings);
        Assert.Equal(2, risk.Calls);
        Assert.Equal(2, await AgentRunsAsync(app, fixture.Project.Id, runId, "size", "risk-manager"));
        Assert.Equal(2, await AgentRunsAsync(app, fixture.Project.Id, runId, "propose", "trader"));

        // The cascade: going back to propose invalidated the first sizing; the second is locked.
        var positions = await AgentJobs.ArtifactsAsync(app, fixture.Project.Id, "position");
        Assert.Equal([ArtifactStatus.Invalidated, ArtifactStatus.Locked], positions.Where(a => a.PipelineRunId == runId).Select(a => a.Status));
    }

    [Fact]
    public async Task FR344_a_veto_with_end_vetoed_completes_the_run_with_outcome_vetoed()
    {
        await using var app = FixtureFlowApp.Create(
            factory, new PositionLimit { VetoesLeft = 1 }, new ScriptedResponder("sizer", """{ "size": 10 }"""), new ScriptedResponder("risk-manager", Approve));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.VetoEnd, new { signal = "Buy ACME." });

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Completed, "size", "vetoed", 0), (run.Status, run.CurrentStage, run.Outcome, run.Revisions));
        Assert.NotNull(run.CompletedAt);
        var executions = await ExecutionsAsync(app, runId);
        Assert.Equal(["propose", "size"], executions.Select(e => e.StageId));
        Assert.All(executions, e => Assert.NotNull(e.ExitedAt));
        Assert.Equal(
            ["flow.position-limit: The position is over 2% of equity."],
            JsonSerializer.Deserialize<string[]>(executions[1].VetoFindings!)!);
        Assert.Equal(0, await AgentRunsAsync(app, fixture.Project.Id, runId, "order", "executor"));
    }

    [Fact]
    public async Task FR344_a_guard_veto_routes_back_without_a_retry_and_its_reasons_are_the_findings()
    {
        var risk = new ScriptedResponder("risk-manager", Approve);
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit { VetoesLeft = 1 }, new ScriptedResponder("sizer", """{ "size": 10 }"""), risk);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Veto, new { signal = "Buy ACME." });

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Completed, 1), (run.Status, run.Revisions));
        var executions = await ExecutionsAsync(app, runId);
        Assert.Equal(
            ["flow.position-limit: The position is over 2% of equity."],
            JsonSerializer.Deserialize<string[]>(executions.First(e => e.StageId == "size").VetoFindings!)!);
        Assert.Equal(2, await AgentRunsAsync(app, fixture.Project.Id, runId, "size", "sizer"));
    }

    [Fact]
    public async Task FR344_an_unreadable_veto_answer_fails_the_stage_with_VALIDATION()
    {
        await using var app = FixtureFlowApp.Create(
            factory, new PositionLimit(), new ScriptedResponder("sizer", """{ "size": 10 }"""), new ScriptedResponder("risk-manager", "Looks fine."));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Veto, new { signal = "Buy ACME." });

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Active, "size"), (run.Status, run.CurrentStage));
        var failed = await FailedDriverJobAsync(app, fixture.Project.Id);
        Assert.StartsWith("VALIDATION:", failed.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR347_FR348_the_editor_routes_back_to_research_then_write_then_continues()
    {
        var editor = new ScriptedResponder("editor", "# Edit\n\nroute: research\n", "# Edit\n\nroute: write\n", "# Edited\n\nroute: continue\n");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), editor);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Editorial, new { topic = "Tides" });

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Completed, 2), (run.Status, run.Revisions));
        Assert.Equal(
            ["research", "write", "edit", "research", "write", "edit", "write", "edit"],
            (await ExecutionsAsync(app, runId)).Select(e => e.StageId));
        Assert.Equal(3, editor.Calls);
        var drafts = await AgentJobs.ArtifactsAsync(app, fixture.Project.Id, "draft");
        Assert.Equal(3, drafts.Count);

        // The cascade: the route to research invalidated the first draft and edit; the route to write the second edit.
        Assert.Equal(ArtifactStatus.Invalidated, drafts[0].Status);
        var edited = await AgentJobs.ArtifactsAsync(app, fixture.Project.Id, "edited");
        Assert.Equal([ArtifactStatus.Invalidated, ArtifactStatus.Invalidated, ArtifactStatus.Locked], edited.Select(a => a.Status));
        var notes = await AgentJobs.ArtifactsAsync(app, fixture.Project.Id, "notes");
        Assert.Equal(2, notes.Count);
        Assert.Equal(ArtifactStatus.Locked, notes[^1].Status);
    }

    [Fact]
    public async Task FR348_the_fourth_revision_is_refused_and_escalates()
    {
        var editor = new ScriptedResponder("editor", "# Edit\n\nroute: write\n");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), editor);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Editorial, new { topic = "Tides" });

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Active, "edit", 3), (run.Status, run.CurrentStage, run.Revisions));
        Assert.Equal(4, editor.Calls);
        var gate = await ProjectApi.WithDbAsync(app, db => db.ApprovalGates.AsNoTracking()
            .SingleAsync(g => g.ProjectId == fixture.Project.Id && g.PipelineRunId == runId, Ct));
        Assert.Equal((EngineGates.Escalation, GateStatus.Pending), (gate.Type, gate.Status));
    }

    [Fact]
    public async Task FR347_a_route_outside_the_declared_routes_fails_the_stage_with_VALIDATION()
    {
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), new ScriptedResponder("editor", "# Edit\n\nroute: publish\n"));
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Editorial, new { topic = "Tides" });

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Active, "edit", 0), (run.Status, run.CurrentStage, run.Revisions));
        var failed = await FailedDriverJobAsync(app, fixture.Project.Id);
        Assert.StartsWith("VALIDATION:", failed.LastError, StringComparison.Ordinal);
        Assert.Contains("publish", failed.LastError, StringComparison.Ordinal);
    }

    private static Task<Job> FailedDriverJobAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .SingleAsync(j => j.ProjectId == projectId && j.Type == EngineJobTypes.StageRun && j.Status == JobStatus.Failed, Ct));
}
