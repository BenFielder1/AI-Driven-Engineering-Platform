using System.Net;
using Aide.Api.Features.Lifecycle;
using Aide.Domain.Common;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.IntegrationTests.Pipelines;

/// <summary>
/// FR-277 acceptance: an SDLC run keeps deciding with the definition it is pinned to (stored canonical JSON when the build
/// no longer registers it), a new run uses the current one, and a pinned definition this build can't run refuses moves
/// with 409 <c>DEFINITION_UNAVAILABLE</c> while reads keep working. §8: the transition log carries run, kind and hash.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class PipelinePinningTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PipelineDefinition Current => SdlcRunDefinition.Current(factory.Services.GetRequiredService<PipelineRegistryHost>()).Definition!;

    [Fact]
    public async Task FR277_a_run_pinned_to_stored_H1_keeps_using_H1_while_a_new_project_uses_H2()
    {
        // H1: the SDLC definition without execution's plan_approval gate (runnable: every guard is registered).
        var h1 = Current with { Stages = [.. Current.Stages.Select(s => s.Id == "execution" ? s with { Gate = null } : s)] };
        using var pinned = await ProjectApi.FixtureAsync(factory);
        using var fresh = await ProjectApi.FixtureAsync(factory);
        await SeedPlanningAsync(pinned.Project.Id);
        await SeedPlanningAsync(fresh.Project.Id);
        var pinnedRun = await PipelineApiGateTests.RunIdAsync(factory, pinned.Project.Id);
        var hash = await PinAsync(pinnedRun, h1);

        var onH1 = await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(pinned.Contributor.Client, pinned.Project.Id));
        var onH2 = await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fresh.Contributor.Client, fresh.Project.Id));

        // H1 has no plan_approval gate in strict mode, so the advance applies; H2 (current) asks for the gate.
        Assert.Null(onH1.Gate);
        Assert.Equal(Phase.Execution, (await LifecycleSeed.ProjectAsync(factory, pinned.Project.Id)).CurrentPhase);
        Assert.Equal(SdlcGates.PlanApproval, onH2.Gate!.Type);
        Assert.Equal(Phase.Planning, (await LifecycleSeed.ProjectAsync(factory, fresh.Project.Id)).CurrentPhase);

        var (pinnedHash, freshHash) = await ProjectApi.WithDbAsync(factory, async db => (
            await db.PipelineRuns.AsNoTracking().Where(r => r.Id == pinnedRun).Select(r => r.DefinitionHash).SingleAsync(Ct),
            await db.PipelineRuns.AsNoTracking().Where(r => r.ProjectId == fresh.Project.Id).Select(r => r.DefinitionHash).SingleAsync(Ct)));
        Assert.Equal(hash, pinnedHash);
        Assert.Equal(DefinitionHash.Compute(Current), freshHash);

        // §8: the transition log carries the run, the kind and the pinned hash as structured fields.
        var log = Assert.Single(factory.Logs.Entries, e =>
            e.Category == typeof(LifecycleService).FullName && e.State.Any(kv => kv.Key == "DefinitionHash" && Equals(kv.Value, hash)));
        Assert.Contains(log.State, kv => kv.Key == "PipelineRunId" && Equals(kv.Value, pinnedRun));
        Assert.Contains(log.State, kv => kv.Key == "TransitionKind" && Equals(kv.Value, "advance"));
    }

    [Fact]
    public async Task FR277_a_run_pinned_to_a_definition_naming_an_unregistered_guard_refuses_moves_but_reads_work()
    {
        var unrunnable = Current with
        {
            Stages = [.. Current.Stages.Select((s, i) => i == 0 ? s with { Validate = [.. s.Validate, new StageValidation("sdlc.not-registered")] } : s)],
        };
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.AddArtifactAsync(factory, id, SdlcArtifacts.ProjectRequest);
        var runId = await PipelineApiGateTests.RunIdAsync(factory, id);
        var hash = await PinAsync(runId, unrunnable);
        var before = await LifecycleSeed.ProjectAsync(factory, id);

        await Problems.AssertAsync(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id), HttpStatusCode.Conflict, ErrorCodes.DefinitionUnavailable);

        var after = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((before.CurrentPhase, before.Checkpoint), (after.CurrentPhase, after.Checkpoint));
        Assert.Contains(factory.Logs.Entries, e => e.Message.Contains("DEFINITION_UNAVAILABLE", StringComparison.Ordinal)
            && e.Message.Contains(hash, StringComparison.Ordinal)
            && e.Message.Contains("guard:sdlc.not-registered", StringComparison.Ordinal));

        // Reads keep working: the project state, its run and the stored definition.
        Assert.Equal(HttpStatusCode.OK, (await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{id}/state", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Viewer.Client.GetAsync(new Uri($"/api/pipeline-runs/{runId}", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await fixture.Viewer.Client.GetAsync(new Uri($"/api/pipelines/{unrunnable.Pipeline}/definitions/{hash}", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task FR277_a_gate_decision_on_the_project_route_is_DEFINITION_UNAVAILABLE_too()
    {
        var unrunnable = Current with
        {
            Stages = [.. Current.Stages.Select((s, i) => i == 0 ? s with { Validate = [.. s.Validate, new StageValidation("sdlc.not-registered")] } : s)],
        };
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, SdlcArtifacts.TechnicalSpecification, projectVersionAtCreation: 2);
        var gate = (await ProjectApi.ReadAsync<AdvanceResultDto>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id))).Gate!;
        await PinAsync(await PipelineApiGateTests.RunIdAsync(factory, id), unrunnable);

        await Problems.AssertAsync(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve"), HttpStatusCode.Conflict, ErrorCodes.DefinitionUnavailable);
        await Problems.AssertAsync(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "reject"), HttpStatusCode.Conflict, ErrorCodes.DefinitionUnavailable);
        Assert.Equal(GateStatus.Pending, (await LifecycleSeed.GatesAsync(factory, id)).Single(g => g.Id == gate.Id).Status);
    }

    /// <summary>Stores <paramref name="definition"/> as canonical JSON (not registered) and pins run <paramref name="runId"/> to it.</summary>
    private async Task<string> PinAsync(Guid runId, PipelineDefinition definition)
    {
        var hash = DefinitionHash.Compute(definition);
        var json = DefinitionHash.CanonicalJson(definition);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO pipeline_definitions (hash, pipeline_id, version, pack_id, canonical_json, created_at) VALUES ({hash}, {definition.Pipeline}, {definition.Version}, {SdlcPack.PackId}, CAST({json} AS jsonb), now()) ON CONFLICT DO NOTHING",
                Ct);
            await db.Database.ExecuteSqlAsync($"UPDATE pipeline_runs SET definition_hash = {hash} WHERE id = {runId}", Ct);
        });
        return hash;
    }

    /// <summary>Planning at checkpoint 3 (strict) with a locked spec and plan, two steps and a prepared working copy.</summary>
    private async Task SeedPlanningAsync(Guid id)
    {
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Strict);
        await LifecycleSeed.AddArtifactAsync(factory, id, SdlcArtifacts.TechnicalSpecification, projectVersionAtCreation: 2);
        var plan = await LifecycleSeed.AddArtifactAsync(factory, id, SdlcArtifacts.ImplementationPlan, projectVersionAtCreation: 3);
        await LifecycleSeed.AddStepsAsync(factory, id, plan.Id, 2);
        await LifecycleSeed.PrepareWorkingCopyAsync(factory, id);
    }
}
