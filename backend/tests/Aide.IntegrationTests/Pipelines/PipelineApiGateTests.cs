using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Gates;
using Aide.Api.Features.Lifecycle;
using Aide.Domain.Common;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.IntegrationTests.Pipelines;

/// <summary>FR-301, FR-302: gate decisions through the pipeline-run endpoints, and the gate inbox.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PipelineApiGateTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR301_approving_plan_approval_through_the_run_matches_the_project_endpoint()
    {
        using var viaProject = await ProjectApi.FixtureAsync(factory);
        using var viaRun = await ProjectApi.FixtureAsync(factory);
        var projectGate = await PendingPlanGateAsync(viaProject);
        var runGate = await PendingPlanGateAsync(viaRun);

        var decidedOnProject = await ProjectApi.ReadAsync<GateDto>(
            await LifecycleSeed.DecideAsync(viaProject.Owner.Client, viaProject.Project.Id, projectGate.Id, "approve", new { comment = "Go" }));
        var runId = await RunIdAsync(viaRun.Project.Id);
        var decidedOnRun = await ProjectApi.ReadAsync<PipelineGateDto>(
            await viaRun.Owner.Client.PostAsJsonAsync($"/api/pipeline-runs/{runId}/gates/{runGate.Id}/approve", new { comment = "Go" }, Ct));

        Assert.Equal((GateStatus.Approved, "Go"), (decidedOnProject.Status, decidedOnProject.Comment));
        Assert.Equal((GateStatus.Approved, "Go", runId, "execution"), (decidedOnRun.Status, decidedOnRun.Comment, decidedOnRun.RunId, decidedOnRun.StageId));

        var a = await LifecycleSeed.ProjectAsync(factory, viaProject.Project.Id);
        var b = await LifecycleSeed.ProjectAsync(factory, viaRun.Project.Id);
        Assert.Equal((a.CurrentPhase, a.Status, a.Checkpoint), (b.CurrentPhase, b.Status, b.Checkpoint));
        Assert.Equal(Phase.Execution, b.CurrentPhase);

        var ta = (await LifecycleSeed.TransitionsAsync(factory, viaProject.Project.Id))[^1];
        var tb = (await LifecycleSeed.TransitionsAsync(factory, viaRun.Project.Id))[^1];
        Assert.Equal((ta.FromPhase, ta.ToPhase, ta.Kind, ta.Checkpoint, ta.Reason), (tb.FromPhase, tb.ToPhase, tb.Kind, tb.Checkpoint, tb.Reason));

        Assert.Equal(await EventTypesAsync(viaProject.Project.Id), await EventTypesAsync(viaRun.Project.Id));
        Assert.Equal(
            (await LifecycleSeed.JobsAsync(factory, viaProject.Project.Id)).Select(j => j.Type),
            (await LifecycleSeed.JobsAsync(factory, viaRun.Project.Id)).Select(j => j.Type));
        Assert.Equal(
            (await LifecycleSeed.NotificationJobsAsync(factory, viaProject.Project.Id)).Count,
            (await LifecycleSeed.NotificationJobsAsync(factory, viaRun.Project.Id)).Count);
    }

    [Fact]
    public async Task FR301_a_viewer_is_forbidden_a_decided_gate_is_not_pending_and_an_outsider_gets_not_found()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var gate = await PendingPlanGateAsync(fixture);
        var runId = await RunIdAsync(fixture.Project.Id);
        var approve = $"/api/pipeline-runs/{runId}/gates/{gate.Id}/approve";

        await Problems.AssertAsync(await fixture.Viewer.Client.PostAsJsonAsync(approve, new { }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Outsider.Client.PostAsJsonAsync(approve, new { }, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync($"/api/pipeline-runs/{runId}/gates/{Guid.NewGuid()}/approve", new { }, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        await ProjectApi.ReadAsync<PipelineGateDto>(await fixture.Owner.Client.PostAsJsonAsync(approve, new { }, Ct));
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync(approve, new { }, Ct), HttpStatusCode.Conflict, ErrorCodes.GateNotPending);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync($"/api/pipeline-runs/{runId}/gates/{gate.Id}/reject", new { }, Ct), HttpStatusCode.Conflict, ErrorCodes.GateNotPending);
    }

    [Fact]
    public async Task FR301_a_run_pinned_to_an_unknown_definition_is_DEFINITION_UNAVAILABLE()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var gate = await PendingPlanGateAsync(fixture);
        var runId = await RunIdAsync(fixture.Project.Id);
        var hash = new string('f', 64);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO pipeline_definitions (hash, pipeline_id, version, pack_id, canonical_json, created_at) VALUES ({hash}, 'sdlc', 0, 'sdlc', CAST('{{}}' AS jsonb), now()) ON CONFLICT DO NOTHING",
                Ct);
            await db.Database.ExecuteSqlAsync($"UPDATE pipeline_runs SET definition_hash = {hash} WHERE id = {runId}", Ct);
        });

        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync($"/api/pipeline-runs/{runId}/gates/{gate.Id}/approve", new { }, Ct),
            HttpStatusCode.Conflict,
            ErrorCodes.DefinitionUnavailable);
        Assert.Equal(GateStatus.Pending, (await LifecycleSeed.GatesAsync(factory, fixture.Project.Id)).Single(g => g.Id == gate.Id).Status);
    }

    [Fact]
    public async Task FR301_a_stored_definition_naming_an_unregistered_guard_is_DEFINITION_UNAVAILABLE()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var gate = await PendingPlanGateAsync(fixture);
        var runId = await RunIdAsync(fixture.Project.Id);
        var current = SdlcRunDefinition.Current(factory.Services.GetRequiredService<PipelineRegistryHost>()).Definition!;
        var edited = current with
        {
            Stages = [.. current.Stages.Select((s, i) => i == 0 ? s with { Validate = [.. s.Validate, new StageValidation("sdlc.not-registered")] } : s)],
        };
        var hash = DefinitionHash.Compute(edited);
        var json = DefinitionHash.CanonicalJson(edited);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO pipeline_definitions (hash, pipeline_id, version, pack_id, canonical_json, created_at) VALUES ({hash}, {edited.Pipeline}, {edited.Version}, {SdlcPack.PackId}, CAST({json} AS jsonb), now()) ON CONFLICT DO NOTHING",
                Ct);
            await db.Database.ExecuteSqlAsync($"UPDATE pipeline_runs SET definition_hash = {hash} WHERE id = {runId}", Ct);
        });

        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync($"/api/pipeline-runs/{runId}/gates/{gate.Id}/approve", new { }, Ct),
            HttpStatusCode.Conflict,
            ErrorCodes.DefinitionUnavailable);
        Assert.Equal(GateStatus.Pending, (await LifecycleSeed.GatesAsync(factory, fixture.Project.Id)).Single(g => g.Id == gate.Id).Status);

        // The definition itself still reads: it is stored and matches its hash.
        var dto = await ProjectApi.ReadAsync<PipelineDefinitionDto>(
            await fixture.Viewer.Client.GetAsync(new Uri($"/api/pipelines/{edited.Pipeline}/definitions/{hash}", UriKind.Relative), Ct));
        Assert.Equal(hash, dto.Hash);
    }

    [Fact]
    public async Task FR302_the_inbox_lists_only_pending_gates_on_projects_the_caller_can_see()
    {
        using var mine = await ProjectApi.FixtureAsync(factory);
        using var other = await ProjectApi.FixtureAsync(factory);
        var visible = await PendingPlanGateAsync(mine);
        var hidden = await PendingPlanGateAsync(other);

        var page = await ProjectApi.ReadAsync<PipelineGateInboxPageDto>(
            await mine.Viewer.Client.GetAsync(new Uri("/api/pipeline-gates?status=pending&gateType=plan_approval&limit=200", UriKind.Relative), Ct));

        Assert.Contains(page.Items, i => i.Gate.Id == visible.Id && i.ProjectName == mine.Project.Name && i.PipelineId == SdlcPack.PipelineId);
        Assert.DoesNotContain(page.Items, i => i.Gate.Id == hidden.Id);
        Assert.All(page.Items, i => Assert.Equal(GateStatus.Pending, i.Gate.Status));
        Assert.DoesNotContain((await ProjectApi.ReadAsync<PipelineGateInboxPageDto>(
            await mine.Outsider.Client.GetAsync(new Uri("/api/pipeline-gates", UriKind.Relative), Ct))).Items, i => i.Gate.Id == visible.Id);
        await Problems.AssertAsync(
            await mine.Viewer.Client.GetAsync(new Uri("/api/pipeline-gates?status=nope", UriKind.Relative), Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(
            await mine.Viewer.Client.GetAsync(new Uri("/api/pipeline-gates?cursor=nope", UriKind.Relative), Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR301_the_runs_gates_list_its_gates()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var gate = await PendingPlanGateAsync(fixture);
        var runId = await RunIdAsync(fixture.Project.Id);

        var gates = await ProjectApi.ReadAsync<List<PipelineGateDto>>(
            await fixture.Viewer.Client.GetAsync(new Uri($"/api/pipeline-runs/{runId}/gates", UriKind.Relative), Ct));

        var listed = Assert.Single(gates, g => g.Id == gate.Id);
        Assert.Equal((SdlcGates.PlanApproval, GateStatus.Pending, SdlcArtifacts.ImplementationPlan), (listed.Type, listed.Status, listed.ArtifactKey));
    }

    internal static Task<Guid> RunIdAsync(AideApiFactory factory, Guid projectId) =>
        ProjectApi.WithDbAsync(factory, db => db.PipelineRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Status == GenericRunStatus.Active)
            .Select(r => r.Id)
            .SingleAsync(Ct));

    private Task<Guid> RunIdAsync(Guid projectId) => RunIdAsync(factory, projectId);

    /// <summary>The project's event types (sorted: events written in one save share a timestamp).</summary>
    private async Task<List<string>> EventTypesAsync(Guid projectId) =>
        await ProjectApi.WithDbAsync(factory, db => db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .Select(e => e.Type)
            .OrderBy(t => t)
            .ToListAsync(Ct));

    /// <summary>Planning at checkpoint 3 (strict) with a spec, a plan and its steps, and the pending plan_approval gate from advancing.</summary>
    private async Task<GateDto> PendingPlanGateAsync(ProjectFixture fixture)
    {
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Strict);
        await LifecycleSeed.AddArtifactAsync(factory, id, SdlcArtifacts.TechnicalSpecification, projectVersionAtCreation: 2);
        var plan = await LifecycleSeed.AddArtifactAsync(factory, id, SdlcArtifacts.ImplementationPlan, projectVersionAtCreation: 3);
        await LifecycleSeed.AddStepsAsync(factory, id, plan.Id, 2);
        await LifecycleSeed.PrepareWorkingCopyAsync(factory, id);
        var gate = (await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id))).Gate!;
        Assert.Equal(SdlcGates.PlanApproval, gate.Type);
        return gate;
    }
}
