using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Api.Jobs;
using Tevrin.Domain.Lifecycle;
using Tevrin.Infrastructure.Persistence.Conversion;
using Tevrin.IntegrationTests.Support;
using Tevrin.Pack.Sdlc;

namespace Tevrin.IntegrationTests.Persistence;

/// <summary>
/// FR-274 acceptance (with FR-269–FR-273): rows a 0.6.1 build wrote, seeded with raw SQL in the 0.6.1 shapes and values
/// (enum wire strings for agents, artifact types, gate types and statuses; job types; a <c>phase_entered</c> payload), read
/// back through the DbContext and the API with the same keys, and the queued jobs run. The users, the project and its
/// release version come from the API (their columns didn't change in M56); everything M56 re-keyed is raw SQL.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class LegacyRowsTests(TevrinApiFactory factory)
{
    private const string ProjectRequest = "# Demo: Todo API\n\n## Summary\nA todo list API.";
    private const string RejectedSpec = "# Technical Specification v1\n\nAn early draft.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR274_rows_written_by_0_6_1_read_back_and_queued_jobs_run()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var versionId = await ProjectApi.WithDbAsync(factory, db => db.Projects.AsNoTracking().Where(p => p.Id == id).Select(p => p.CurrentReleaseVersionId!.Value).SingleAsync(Ct));
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);

        // 0.6.1 rows before the phase change: the refinement run, its locked project request, then entering specification.
        var refinementRun = Guid.CreateVersion7();
        var requestId = Guid.CreateVersion7();
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            await InsertRunAsync(db, refinementRun, id, versionId, "idea_refinement", "refinement", "succeeded", null, t0);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO artifacts (id, project_id, release_version_id, type, version, content, content_hash, status, agent_run_id,
                                       checkpoint_at_creation, approved_at, created_at)
                VALUES ({requestId}, {id}, {versionId}, 'project_request', 1, {ProjectRequest}, {ContentHash.Compute(ProjectRequest)}, 'locked',
                        {refinementRun}, 1, {t0}, {t0})
                """,
                Ct);
        });
        await ProjectApi.ReadAsync<JsonElement>(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        // The rest in the 0.6.1 shapes: a failed spec run and its rejected draft, two gates, a dead execution job, and the
        // queued jobs with 0.6.1 payloads (trigger phase_entered) in place of the ones this build just queued.
        var specRun = Guid.CreateVersion7();
        var specV1 = Guid.CreateVersion7();
        var specGate = Guid.CreateVersion7();
        var escalation = Guid.CreateVersion7();
        var dead = Guid.CreateVersion7();
        var specJob = Guid.CreateVersion7();
        var staleRefinementJob = Guid.CreateVersion7();
        var legacyPayload = $$"""{"projectId":"{{id}}","projectVersion":2,"trigger":"phase_entered","feedback":null,"gateId":null,"artifactId":null,"answers":null,"checksWaitSince":null,"previewWaitSince":null}""";
        var deadPayload = $$"""{"projectId":"{{id}}","trigger":"start"}""";
        var t1 = t0.AddMinutes(30);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM jobs WHERE project_id = {id} AND type = 'spec_generation' AND status = 'queued'", Ct);
            await InsertRunAsync(db, specRun, id, versionId, "spec_generator", "specification", "failed", "MISSING_SECTIONS: The specification is missing 1 required section(s): Testing Strategy.", t1);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO artifacts (id, project_id, release_version_id, type, version, content, content_hash, status, agent_run_id,
                                       checkpoint_at_creation, rejection_comment, created_at)
                VALUES ({specV1}, {id}, {versionId}, 'technical_specification', 1, {RejectedSpec}, {ContentHash.Compute(RejectedSpec)}, 'rejected',
                        {specRun}, 2, 'Add auth', {t1})
                """,
                Ct);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO approval_gates (id, project_id, release_version_id, type, status, artifact_id, target_checkpoint, decision, comment,
                                            overdue, created_at, decided_at)
                VALUES ({specGate}, {id}, {versionId}, 'spec_approval', 'cancelled', {specV1}, 2, NULL, NULL, false, {t1}, {t1}),
                       ({escalation}, {id}, {versionId}, 'escalation', 'cancelled', NULL, NULL, NULL, 'Superseded', false, {t1}, {t1})
                """,
                Ct);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO jobs (id, type, payload, project_id, status, attempts, run_after, last_error, created_at, completed_at)
                VALUES ({dead}, 'execution', {deadPayload}::jsonb, {id}, 'dead', 4, {t1}, 'GIT_ERROR: push rejected', {t1}, {t1}),
                       ({specJob}, 'spec_generation', {legacyPayload}::jsonb, {id}, 'queued', 0, {t1}, NULL, {t1}, NULL),
                       ({staleRefinementJob}, 'idea_refinement', {legacyPayload}::jsonb, {id}, 'queued', 0, {t1}, NULL, {t1.AddSeconds(1)}, NULL)
                """,
                Ct);
        });

        // Every row reads back through the DbContext with its 0.6.1 key.
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var artifacts = await db.Artifacts.AsNoTracking().Where(a => a.ProjectId == id).OrderBy(a => a.CreatedAt).ToListAsync(Ct);
            Assert.Equal(
                [(SdlcArtifacts.ProjectRequest, ArtifactStatus.Locked, (Guid?)refinementRun), (SdlcArtifacts.TechnicalSpecification, ArtifactStatus.Rejected, specRun)],
                artifacts.Select(a => (a.Type, a.Status, a.AgentRunId)));
            var gates = await db.ApprovalGates.AsNoTracking().Where(g => g.ProjectId == id).OrderBy(g => g.Type).ToListAsync(Ct);
            Assert.Equal([(EngineGates.Escalation, GateStatus.Cancelled), (SdlcGates.SpecApproval, GateStatus.Cancelled)], gates.Select(g => (g.Type, g.Status)));
            var runs = await db.AgentRuns.AsNoTracking().Where(r => r.ProjectId == id).OrderBy(r => r.CreatedAt).ToListAsync(Ct);
            Assert.Equal(
                [(SdlcAgents.IdeaRefinement, Phase.Refinement.ToWire(), AgentRunStatus.Succeeded), (SdlcAgents.SpecGenerator, Phase.Specification.ToWire(), AgentRunStatus.Failed)],
                runs.Select(r => (r.Agent, r.Stage, r.Status)));
            var deadJob = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == dead, Ct);
            var deadTrigger = LifecycleSeed.Payload(deadJob.Payload).GetProperty("trigger").GetString(); // a non-generic trigger, unchanged
            Assert.Equal((SdlcJobTypes.Execution, JobStatus.Dead, "start"), (deadJob.Type, deadJob.Status, deadTrigger));

            // FR-272: the 0.6.1 job types are registered, execution one per project (so retrying this dead letter keeps its rule).
            var registry = factory.Services.GetRequiredService<JobTypeRegistry>();
            Assert.True(registry.IsOnePerProject(deadJob.Type));
            Assert.All([SdlcJobTypes.SpecGeneration, SdlcJobTypes.IdeaRefinement], t => Assert.True(registry.IsRegistered(t), t));

            // FR-273: the 0.6.1 payload reads with the generic trigger.
            var queued = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == specJob, Ct);
            Assert.Equal(JobTriggers.StageEntered, TevrinJson.Deserialize<LifecycleJobPayload>(queued.Payload).Trigger);
        });

        // ... and through the API, with the same wire values.
        var listed = await ProjectApi.ReadAsync<JsonElement>(await fixture.Viewer.Client.GetAsync($"{fixture.Path}/artifacts", Ct));
        Assert.Equal(
            [("project_request", "locked"), ("technical_specification", "rejected")],
            listed.EnumerateArray().Select(a => (a.GetProperty("type").GetString(), a.GetProperty("status").GetString())).Order());
        var gateList = await ProjectApi.ReadAsync<JsonElement>(await fixture.Viewer.Client.GetAsync($"{fixture.Path}/gates", Ct));
        Assert.Equal(["escalation", "spec_approval"], gateList.EnumerateArray().Select(g => g.GetProperty("type").GetString()!).Order(StringComparer.Ordinal));
        var runPage = await ProjectApi.ReadAsync<JsonElement>(await fixture.Viewer.Client.GetAsync($"{fixture.Path}/runs", Ct));
        Assert.Equal(["idea_refinement", "spec_generator"], runPage.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("agent").GetString()!).Order(StringComparer.Ordinal));

        // The queued 0.6.1 jobs run. The spec job writes its draft; the stale refinement job reads its trigger as
        // stage_entered, so (the project being past refinement) it does nothing, as phase_entered did in 0.6.1.
        var jobs = await AgentJobs.RunQueuedAsync(factory, id);

        Assert.Equal(JobStatus.Succeeded, jobs.Single(j => j.Id == specJob).Status);
        Assert.Equal(JobStatus.Succeeded, jobs.Single(j => j.Id == staleRefinementJob).Status);
        Assert.Equal(JobStatus.Dead, jobs.Single(j => j.Id == dead).Status);
        var specs = await AgentJobs.ArtifactsAsync(factory, id, SdlcArtifacts.TechnicalSpecification);
        Assert.Equal([(1, ArtifactStatus.Rejected), (2, ArtifactStatus.Draft)], specs.Select(s => (s.Version, s.Status)));
        Assert.Equal((Guid?)specJob, specs[1].AgentRunId);
        var after = await AgentJobs.RunsAsync(factory, id);
        Assert.DoesNotContain(after, r => r.Id == staleRefinementJob);
        Assert.Single(await AgentJobs.ArtifactsAsync(factory, id, SdlcArtifacts.ProjectRequest));
        Assert.Equal(Phase.Specification, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);
    }

    private static Task<int> InsertRunAsync(
        Tevrin.Infrastructure.Persistence.TevrinDbContext db, Guid runId, Guid projectId, Guid versionId, string agent, string phase, string status, string? error, DateTimeOffset at) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO agent_runs (id, project_id, release_version_id, agent, phase, provider, model, prompt_version, input_artifacts, status,
                                    attempts, input_tokens, output_tokens, cost_estimate, duration_ms, error, created_at, completed_at)
            VALUES ({runId}, {projectId}, {versionId}, {agent}, {phase}, 'fake', 'fake-model', 'v1', '[]'::jsonb, {status},
                    1, 120, 80, 0, 1500, NULLIF({error ?? string.Empty}, ''), {at}, {at})
            """,
            Ct);
}
