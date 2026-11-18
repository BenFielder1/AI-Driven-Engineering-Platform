using System.Net.Http.Json;
using System.Text.Json;
using Aide.Domain.Entities;
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
/// FR-277, FR-292 (data side), FR-294, D-0.7.0-28: SDLC pipeline runs: the backfill of 0.6.1 data, the dual write that keeps
/// the run equal to the project, and the stored definition the runs are pinned to.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class PipelineRunTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LoadedPipelineDefinition Sdlc => SdlcRunDefinition.Current(factory.Services.GetRequiredService<PipelineRegistryHost>());

    [Fact]
    public async Task FR294_a_0_6_1_project_in_review_on_its_second_version_gets_two_runs_and_its_rows_are_linked()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "0.2.0");
        var id = fixture.Project.Id;
        var (v1, v2) = await SeedSecondVersionInReviewAsync(id);
        await MakeLegacyAsync(id);

        var outcome = await ProjectApi.WithDbAsync(factory, db => PipelineRunBackfill.RunAsync(db, Sdlc, null, DateTimeOffset.UtcNow, Ct));

        Assert.True(outcome.RunsCreated >= 2);
        var runs = await RunsAsync(id);
        Assert.Equal([v1, v2], runs.Select(r => r.SubjectId!.Value));
        var (first, second) = (runs[0], runs[1]);
        Assert.Equal(("release", "released", GenericRunStatus.Completed, 6, Sdlc.Hash), (first.CurrentStage, first.PackStatus, first.Status, first.Checkpoint, first.DefinitionHash));
        Assert.NotNull(first.CompletedAt);
        Assert.Equal(("review", "reviewing", GenericRunStatus.Active, 4, Sdlc.Hash), (second.CurrentStage, second.PackStatus, second.Status, second.Checkpoint, second.DefinitionHash));
        Assert.Equal((ApprovalMode.Advisory, (DateTimeOffset?)null, 1), (second.ApprovalMode, second.CompletedAt, second.Iterations["review"]));

        var links = await LinksAsync(id);
        Assert.Equal(4, links.Select(l => l.Table).Distinct().Count());
        Assert.All(links, l => Assert.Equal(l.ReleaseVersionId == v1 ? first.Id : second.Id, l.RunId));
        Assert.Contains(links, l => l.ReleaseVersionId == v1);
    }

    [Fact]
    public async Task D_0_7_0_28_the_backfill_is_idempotent_and_safe_to_run_concurrently()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "0.2.0");
        var id = fixture.Project.Id;
        await SeedSecondVersionInReviewAsync(id);
        await MakeLegacyAsync(id);

        // Two replicas starting at once.
        await Task.WhenAll(BackfillAsync(id), BackfillAsync(id));
        var runs = await RunsAsync(id);
        var links = await LinksAsync(id);

        var again = await BackfillAsync(id);

        Assert.Equal(new PipelineRunBackfill.Outcome(0, 0, 0), again);
        Assert.Equal(2, runs.Count);
        Assert.Equal(runs.Select(r => (r.Id, r.Status, r.Checkpoint)), (await RunsAsync(id)).Select(r => (r.Id, r.Status, r.Checkpoint)));
        Assert.All(links, l => Assert.NotNull(l.RunId));
        Assert.Equal(links, await LinksAsync(id));
    }

    [Fact]
    public async Task D_0_7_0_28_a_project_without_runs_gets_them_on_its_next_lifecycle_write()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await MakeLegacyAsync(id);
        await LifecycleSeed.AddArtifactAsync(factory, id, SdlcArtifacts.ProjectRequest);

        Ok(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));

        // The run is created and follows the project; rows written before it existed are linked by the start-up pass.
        var project = await LifecycleSeed.ProjectAsync(factory, id);
        var run = Assert.Single(await RunsAsync(id));
        Assert.Equal(("specification", 2, project.CurrentReleaseVersionId), (run.CurrentStage, run.Checkpoint, run.SubjectId));
        Assert.Equal(run.Id, (await LifecycleSeed.TransitionsAsync(factory, id))[^1].PipelineRunId);
    }

    [Fact]
    public async Task D_0_7_0_28_dual_write_never_conflicts_on_a_stale_run()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var versionId = (await LifecycleSeed.ProjectAsync(factory, id)).CurrentReleaseVersionId;

        // Scope A (a job) saves an agent run, which reads its run for the link.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        db.AgentRuns.Add(new AgentRun
        {
            ProjectId = id,
            ReleaseVersionId = versionId,
            Agent = SdlcAgents.IdeaRefinement,
            Phase = Phase.Refinement,
            Provider = LlmProvider.Fake,
            Model = "fake",
            PromptVersion = "1",
            Status = AgentRunStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);

        // Another scope (a user) changes the project, so its run's xmin moves on.
        Ok(await ProjectApi.PatchAsync(fixture.Owner.Client, fixture.Path, new { approvalMode = "strict" }));

        // Scope A then loads and saves the project: the dual write loads the run fresh and doesn't conflict.
        var project = await db.Projects.SingleAsync(p => p.Id == id, Ct);
        project.ReviewCycle = 1;
        await db.SaveChangesAsync(Ct);
        project.ExtraReviewCycles = 1;
        await db.SaveChangesAsync(Ct);

        var run = await AssertInSyncAsync(id);
        Assert.Equal((ApprovalMode.Strict, 1), (run.ApprovalMode, run.Iterations["review"]));
    }

    [Fact]
    public async Task D_0_7_0_28_the_start_up_pass_catches_up_after_a_0_6_1_build_started_a_new_version()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.0.0");
        var id = fixture.Project.Id;
        var v1 = (await LifecycleSeed.ProjectAsync(factory, id)).CurrentReleaseVersionId!.Value;

        // A 0.6.1 replica released 1.0.0 and started 1.1.0 (no run writes), and wrote a transition with no run.
        var v2 = Guid.CreateVersion7();
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlAsync(
                $"UPDATE release_versions SET status = 'released', tag = 'v1.0.0', released_at = {now} WHERE id = {v1}", Ct);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO release_versions (id, project_id, version, status, tag_prefix, request, stack_source, created_at)
                VALUES ({v2}, {id}, '1.1.0', 'in_development', 'v', 'More', 'manual', {now.AddSeconds(1)})
                """,
                Ct);
            await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE projects SET current_release_version_id = {v2}, current_phase = 'refinement', status = 'active', checkpoint = 1,
                    review_cycle = 0, released_version = '1.0.0', development_version = '1.1.0'
                WHERE id = {id}
                """,
                Ct);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO state_transitions (id, project_id, release_version_id, from_phase, to_phase, actor, reason, checkpoint, created_at)
                VALUES ({Guid.CreateVersion7()}, {id}, {v2}, 'release', 'refinement', 'seed', 'Started version 1.1.0', 1, {now.AddSeconds(2)})
                """,
                Ct);
        });

        await BackfillAsync(id);

        var runs = await RunsAsync(id);
        Assert.Equal([v1, v2], runs.Select(r => r.SubjectId!.Value));
        Assert.Equal(("release", "released", GenericRunStatus.Completed), (runs[0].CurrentStage, runs[0].PackStatus, runs[0].Status));
        Assert.NotNull(runs[0].CompletedAt);
        await AssertInSyncAsync(id);
    }

    [Fact]
    public async Task FR292_the_run_equals_the_project_after_create_advance_gate_rollback_and_new_version()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.0.0");
        var id = fixture.Project.Id;
        var created = await AssertInSyncAsync(id);
        Assert.Equal(("refinement", "active", GenericRunStatus.Active, 1, Sdlc.Hash), (created.CurrentStage, created.PackStatus, created.Status, created.Checkpoint, created.DefinitionHash));
        Assert.Equal(PipelineTransitionKinds.Start, Assert.Single(await LifecycleSeed.TransitionsAsync(factory, id)).Kind);

        // Advance: refinement → specification.
        await LifecycleSeed.AddArtifactAsync(factory, id, SdlcArtifacts.ProjectRequest);
        Ok(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));
        var advanced = await AssertInSyncAsync(id);
        Assert.Equal(("specification", 2), (advanced.CurrentStage, advanced.Checkpoint));
        Assert.Equal(PipelineTransitionKinds.Advance, (await LifecycleSeed.TransitionsAsync(factory, id))[^1].Kind);

        // Advisory mode: the advance needs a spec_approval gate, then approving it enters planning.
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 2, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, SdlcArtifacts.TechnicalSpecification, projectVersionAtCreation: 2);
        Ok(await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id));
        var gate = Assert.Single(await LifecycleSeed.GatesAsync(factory, id));
        Assert.Equal(ApprovalMode.Advisory, (await AssertInSyncAsync(id)).ApprovalMode);
        Ok(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve"));
        var planning = await AssertInSyncAsync(id);
        Assert.Equal(("planning", 3), (planning.CurrentStage, planning.Checkpoint));

        // Rollback to checkpoint 2.
        Ok(await fixture.Owner.Client.PostAsJsonAsync($"/api/projects/{id}/rollback", new { targetCheckpoint = 2, reason = "Back to the spec" }, Ct));
        var rolledBack = await AssertInSyncAsync(id);
        Assert.Equal(("specification", "rolled_back", 2), (rolledBack.CurrentStage, rolledBack.PackStatus, rolledBack.Checkpoint));
        Assert.Equal(PipelineTransitionKinds.Rollback, (await LifecycleSeed.TransitionsAsync(factory, id))[^1].Kind);

        // Released (written through the context, like the release job), then a new version starts a new run.
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var version = await db.ReleaseVersions.SingleAsync(v => v.ProjectId == id, Ct);
            version.Status = ReleaseVersionStatus.Released;
            version.Tag = "v1.0.0";
            version.ReleasedAt = DateTimeOffset.UtcNow;
            var project = await db.Projects.SingleAsync(p => p.Id == id, Ct);
            project.CurrentPhase = Phase.Release;
            project.Status = ProjectStatus.Released;
            project.ReleasedVersion = "1.0.0";
            project.DevelopmentVersion = null;
            await db.SaveChangesAsync(Ct);
        });
        var released = await AssertInSyncAsync(id);
        Assert.Equal((GenericRunStatus.Completed, true), (released.Status, released.CompletedAt is not null));

        Ok(await fixture.Owner.Client.PostAsJsonAsync($"/api/projects/{id}/versions", new { version = "1.1.0", request = "Add CSV export" }, Ct));

        var current = await AssertInSyncAsync(id);
        var runs = await RunsAsync(id);
        Assert.Equal(2, runs.Count);
        Assert.Equal((released.Id, GenericRunStatus.Completed), (runs[0].Id, runs[0].Status));
        Assert.Equal((runs[1].Id, "refinement", "active", GenericRunStatus.Active, 1, 0, Sdlc.Hash),
            (current.Id, current.CurrentStage, current.PackStatus, current.Status, current.Checkpoint, current.Iterations["review"], current.DefinitionHash));
        var start = (await LifecycleSeed.TransitionsAsync(factory, id))[^1];
        Assert.Equal((PipelineTransitionKinds.Start, (Guid?)current.Id), (start.Kind, start.PipelineRunId));
    }

    [Fact]
    public async Task FR277_the_definition_is_stored_once_and_its_canonical_JSON_reproduces_the_hash()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var hash = Sdlc.Hash!;

        var (stored, run) = await ProjectApi.WithDbAsync(factory, async db =>
        {
            var record = await db.PipelineDefinitions.AsNoTracking().SingleAsync(d => d.Hash == hash, Ct);
            var created = await db.PipelineRuns.AsNoTracking().SingleAsync(r => r.ProjectId == fixture.Project.Id, Ct);
            return (record, created);
        });
        Assert.Equal((SdlcPack.PipelineId, SdlcPack.PackId, Sdlc.Definition!.Version, hash), (stored.PipelineId, stored.PackId, stored.Version, run.DefinitionHash));

        // jsonb hands back the same JSON, not the same bytes: read it with a JSON reader and re-canonicalise.
        Assert.Equal(hash, StoredDefinitions.HashOf(stored.CanonicalJson));
        using (var json = JsonDocument.Parse(stored.CanonicalJson))
        {
            Assert.Equal(SdlcPack.PipelineId, json.RootElement.GetProperty("pipeline").GetString());
        }

        // A second first use (another replica) changes nothing: one row, never updated.
        await ProjectApi.WithDbAsync(factory, db => StoredDefinitions.StoreAsync(db, Sdlc, DateTimeOffset.UtcNow.AddDays(1), Ct));
        using var second = await ProjectApi.FixtureAsync(factory);
        var rows = await ProjectApi.WithDbAsync(factory, db => db.PipelineDefinitions.AsNoTracking().Where(d => d.Hash == hash).ToListAsync(Ct));
        Assert.Equal(stored.CreatedAt, Assert.Single(rows).CreatedAt);
    }

    // ---------- helpers ----------

    private static void Ok(HttpResponseMessage response) =>
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {response.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");

    private async Task<PipelineRunBackfill.Outcome> BackfillAsync(Guid projectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        return await PipelineRunBackfill.RunAsync(db, Sdlc, projectId, DateTimeOffset.UtcNow, Ct);
    }

    /// <summary>A released 0.1.0 with a transition, gate, agent run and artifact, and 0.2.0 in review at checkpoint 4 (review
    /// cycle 1) with the same rows. Returns the two release version ids.</summary>
    private async Task<(Guid V1, Guid V2)> SeedSecondVersionInReviewAsync(Guid id)
    {
        var t0 = DateTimeOffset.UtcNow.AddDays(-2);
        var v2 = await ProjectApi.WithDbAsync(factory, db => db.Projects.Where(p => p.Id == id).Select(p => p.CurrentReleaseVersionId!.Value).SingleAsync(Ct));
        var v1 = await ProjectApi.WithDbAsync(factory, async db =>
        {
            var version = new ReleaseVersion
            {
                ProjectId = id,
                Version = "0.1.0",
                Status = ReleaseVersionStatus.Released,
                Tag = "v0.1.0",
                Request = "First",
                CreatedAt = t0,
                ReleasedAt = t0.AddHours(1),
            };
            db.ReleaseVersions.Add(version);
            await db.SaveChangesAsync(Ct);
            return version.Id;
        });

        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var transitions = new[]
            {
                (Version: v1, From: Phase.Review, To: Phase.Release, Checkpoint: 6, At: t0.AddMinutes(10)),
                (Version: v2, From: Phase.Refinement, To: Phase.Specification, Checkpoint: 2, At: DateTimeOffset.UtcNow.AddMinutes(-30)),
                (Version: v2, From: Phase.Execution, To: Phase.Review, Checkpoint: 4, At: DateTimeOffset.UtcNow.AddMinutes(-20)),
            };
            foreach (var t in transitions)
            {
                db.StateTransitions.Add(new StateTransition
                {
                    ProjectId = id,
                    ReleaseVersionId = t.Version,
                    FromPhase = t.From,
                    ToPhase = t.To,
                    Actor = "seed",
                    Reason = "seed",
                    Checkpoint = t.Checkpoint,
                    CreatedAt = t.At,
                });
            }

            foreach (var (versionId, number) in new[] { (v1, 1), (v2, 2) })
            {
                var content = $"# Request {number}";
                db.Artifacts.Add(new Artifact
                {
                    ProjectId = id,
                    ReleaseVersionId = versionId,
                    Type = SdlcArtifacts.ProjectRequest,
                    Version = number,
                    Content = content,
                    ContentHash = ContentHash.Compute(content),
                    Status = ArtifactStatus.Locked,
                    CheckpointAtCreation = 1,
                    CreatedAt = t0,
                });
                db.ApprovalGates.Add(new ApprovalGate
                {
                    ProjectId = id,
                    ReleaseVersionId = versionId,
                    Type = SdlcGates.SpecApproval,
                    Status = GateStatus.Approved,
                    CreatedAt = t0,
                    DecidedAt = t0,
                });
                db.AgentRuns.Add(new AgentRun
                {
                    ProjectId = id,
                    ReleaseVersionId = versionId,
                    Agent = SdlcAgents.CodeReview,
                    Phase = Phase.Review,
                    Provider = LlmProvider.Fake,
                    Model = "fake",
                    PromptVersion = "1",
                    Status = AgentRunStatus.Succeeded,
                    CreatedAt = t0,
                });
            }

            await db.SaveChangesAsync(Ct);
        });
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 4, ApprovalMode.Advisory, reviewCycle: 1);
        return (v1, v2);
    }

    /// <summary>The project as a 0.6.1 build left it: no runs, no links.</summary>
    private Task MakeLegacyAsync(Guid id) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            await db.Database.ExecuteSqlAsync($"UPDATE state_transitions SET pipeline_run_id = NULL, kind = NULL WHERE project_id = {id}", Ct);
            await db.Database.ExecuteSqlAsync($"UPDATE approval_gates SET pipeline_run_id = NULL WHERE project_id = {id}", Ct);
            await db.Database.ExecuteSqlAsync($"UPDATE agent_runs SET pipeline_run_id = NULL WHERE project_id = {id}", Ct);
            await db.Database.ExecuteSqlAsync($"UPDATE artifacts SET pipeline_run_id = NULL WHERE project_id = {id}", Ct);
            await db.Database.ExecuteSqlAsync($"DELETE FROM pipeline_runs WHERE project_id = {id}", Ct);
        });

    private Task<List<PipelineRun>> RunsAsync(Guid id) =>
        ProjectApi.WithDbAsync(factory, db => db.PipelineRuns.AsNoTracking().Where(r => r.ProjectId == id).OrderBy(r => r.StartedAt).ThenBy(r => r.Id).ToListAsync(Ct));

    private sealed record Link(string Table, Guid RowId, Guid? ReleaseVersionId, Guid? RunId);

    private Task<List<Link>> LinksAsync(Guid id) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var transitions = await db.StateTransitions.AsNoTracking().Where(t => t.ProjectId == id)
                .Select(t => new Link("state_transitions", t.Id, t.ReleaseVersionId, t.PipelineRunId)).ToListAsync(Ct);
            var gates = await db.ApprovalGates.AsNoTracking().Where(g => g.ProjectId == id)
                .Select(g => new Link("approval_gates", g.Id, g.ReleaseVersionId, g.PipelineRunId)).ToListAsync(Ct);
            var agentRuns = await db.AgentRuns.AsNoTracking().Where(r => r.ProjectId == id)
                .Select(r => new Link("agent_runs", r.Id, r.ReleaseVersionId, r.PipelineRunId)).ToListAsync(Ct);
            var artifacts = await db.Artifacts.AsNoTracking().Where(a => a.ProjectId == id)
                .Select(a => new Link("artifacts", a.Id, a.ReleaseVersionId, a.PipelineRunId)).ToListAsync(Ct);
            return transitions.Concat(gates).Concat(agentRuns).Concat(artifacts)
                .OrderBy(l => l.Table, StringComparer.Ordinal).ThenBy(l => l.RowId).ToList();
        });

    /// <summary>The run of the project's current release version equals the project (FR-292), no other run is open, and every
    /// transition, gate, agent run and artifact of the project points at the run of its release version.</summary>
    private async Task<PipelineRun> AssertInSyncAsync(Guid id)
    {
        var project = await LifecycleSeed.ProjectAsync(factory, id);
        var runs = await RunsAsync(id);
        var run = Assert.Single(runs, r => r.SubjectId == project.CurrentReleaseVersionId);
        Assert.Equal(
            (project.CurrentPhase.ToWire(), project.Status.ToWire(), project.Checkpoint, project.ApprovalMode, project.ReviewCycle),
            (run.CurrentStage, run.PackStatus, run.Checkpoint, run.ApprovalMode, run.Iterations["review"]));
        Assert.Equal(project.Status == ProjectStatus.Released ? GenericRunStatus.Completed : GenericRunStatus.Active, run.Status);
        Assert.DoesNotContain(runs, r => r.Id != run.Id && r.Status == GenericRunStatus.Active);

        var byVersion = runs.ToDictionary(r => r.SubjectId!.Value, r => r.Id);
        Assert.All(await LinksAsync(id), l => Assert.Equal(byVersion[l.ReleaseVersionId!.Value], l.RunId));
        return run;
    }
}
