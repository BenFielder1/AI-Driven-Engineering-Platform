using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Rollback;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Rollback;

/// <summary>FR-44, FR-45, §4.8: <c>POST /rollback</c>, the rollback history and the version picker.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RollbackEndpointTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The FR-44 example: a project at version 7 in review whose version 4 was planning.</summary>
    private sealed record Seeded(
        ProjectFixture Fixture,
        Artifact Plan4,
        IReadOnlyList<Artifact> Later,
        IReadOnlyList<ExecutionStep> PlanSteps,
        IReadOnlyList<ExecutionStep> OptimisationSteps,
        ApprovalGate Gate,
        Job QueuedJob) : IDisposable
    {
        public Guid Id => Fixture.Project.Id;

        public void Dispose() => Fixture.Dispose();
    }

    private async Task<Seeded> SeedVersion7InReviewAsync()
    {
        var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 7, ApprovalMode.Advisory, reviewCycle: 2);
        (Phase From, Phase To, int Version)[] history =
        [
            (Phase.Refinement, Phase.Specification, 2),
            (Phase.Specification, Phase.Planning, 3),
            (Phase.Planning, Phase.Planning, 4),
            (Phase.Planning, Phase.Execution, 5),
            (Phase.Execution, Phase.Review, 6),
            (Phase.Review, Phase.Execution, 6),
            (Phase.Execution, Phase.Review, 7),
        ];
        for (var i = 0; i < history.Length; i++)
        {
            await LifecycleSeed.AddTransitionAsync(factory, id, history[i].From, history[i].To, history[i].Version, t0.AddMinutes(i));
        }

        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, projectVersionAtCreation: 1);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2);
        var plan4 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, projectVersionAtCreation: 4);
        var spec5 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Draft, version: 2, projectVersionAtCreation: 5);
        var optimisation6 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.OptimizationPlan, projectVersionAtCreation: 6);
        var optimisation7 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.OptimizationPlan, ArtifactStatus.Draft, version: 2, projectVersionAtCreation: 7);
        var planSteps = await LifecycleSeed.AddStepsAsync(factory, id, plan4.Id, 2, StepStatus.Completed);
        var optimisationSteps = await LifecycleSeed.AddStepsAsync(factory, id, optimisation6.Id, 2, StepStatus.Completed);
        var gate = await LifecycleSeed.AddGateAsync(factory, id, GateType.Escalation);
        var job = await ProjectApi.WithDbAsync(factory, async db =>
        {
            var queued = new Job { ProjectId = id, Type = JobTypes.Review, Payload = "{}", RunAfter = DateTimeOffset.UtcNow.AddHours(1), CreatedAt = DateTimeOffset.UtcNow };
            db.Jobs.Add(queued);
            await db.SaveChangesAsync(Ct);
            return queued;
        });
        return new Seeded(fixture, plan4, [spec5, optimisation6, optimisation7], planSteps, optimisationSteps, gate, job);
    }

    [Fact]
    public async Task FR44_Version7_in_review_rolled_back_to_version4_restores_planning_invalidates_v5_to_v7_and_records_them()
    {
        using var seeded = await SeedVersion7InReviewAsync();
        var id = seeded.Id;
        var owner = seeded.Fixture.Owner;

        var rollback = await ProjectApi.ReadAsync<RollbackDto>(await RollbackAsync(owner.Client, id, 4, "  Wrong direction  "));

        Assert.Equal((7, 4, Phase.Planning, "Wrong direction"), (rollback.FromVersion, rollback.ToVersion, rollback.RestoredPhase, rollback.Reason));
        Assert.Equal(seeded.Later.Select(a => a.Id).Order(), rollback.InvalidatedArtifactIds.Order());
        Assert.Equal(seeded.OptimisationSteps.Select(s => s.Id).Order(), rollback.ResetStepIds.Order());
        Assert.Empty(rollback.RequeuedStepIds);
        Assert.Equal([seeded.Gate.Id], rollback.CancelledGateIds);
        Assert.Equal([seeded.QueuedJob.Id], rollback.CancelledJobIds);
        Assert.Equal((owner.User!.Id, owner.User.Name, false), (rollback.CreatedBy.Id, rollback.CreatedBy.Name, rollback.CreatedBy.IsSystem));

        // Everything was written together.
        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Planning, ProjectStatus.RolledBack, 4, 0), (project.CurrentPhase, project.Status, project.ProjectVersion, project.ReviewCycle));
        Assert.NotNull(project.LastRollbackAt);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var artifacts = await db.Artifacts.AsNoTracking().Where(a => a.ProjectId == id).ToListAsync(Ct);
            Assert.All(artifacts.Where(a => a.ProjectVersionAtCreation > 4), a => Assert.Equal(ArtifactStatus.Invalidated, a.Status));
            Assert.All(artifacts.Where(a => a.ProjectVersionAtCreation <= 4), a => Assert.Equal(ArtifactStatus.Locked, a.Status));
            var steps = await db.ExecutionSteps.AsNoTracking().Where(s => s.ProjectId == id).ToListAsync(Ct);
            Assert.All(steps.Where(s => s.PlanArtifactId == seeded.Plan4.Id), s => Assert.Equal(StepStatus.Completed, s.Status));
            Assert.All(steps.Where(s => s.PlanArtifactId != seeded.Plan4.Id), s => Assert.Equal(StepStatus.Invalidated, s.Status));
            Assert.Equal(GateStatus.Cancelled, (await db.ApprovalGates.AsNoTracking().SingleAsync(g => g.Id == seeded.Gate.Id, Ct)).Status);
            Assert.Equal(JobStatus.Cancelled, (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == seeded.QueuedJob.Id, Ct)).Status);
            var record = await db.RollbackEvents.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct);
            Assert.Equal((rollback.Id, owner.User.Id.ToString()), (record.Id, record.TriggeredBy));
        });
        var transition = (await LifecycleSeed.TransitionsAsync(factory, id)).Last();
        Assert.Equal((Phase.Review, Phase.Planning, 4, "Wrong direction"), (transition.FromPhase, transition.ToPhase, transition.ProjectVersion, transition.Reason));
        var completed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.RollbackCompleted)).Payload);
        Assert.Equal(rollback.Id.ToString(), completed.GetProperty("rollbackId").GetString());

        // History and the version picker.
        var history = await ProjectApi.ReadAsync<List<RollbackDto>>(await seeded.Fixture.Viewer.Client.GetAsync(new Uri($"{seeded.Fixture.Path}/rollbacks", UriKind.Relative), Ct));
        var listed = Assert.Single(history);
        Assert.Equal((rollback.Id, rollback.ToVersion, rollback.RestoredPhase, rollback.CreatedBy), (listed.Id, listed.ToVersion, listed.RestoredPhase, listed.CreatedBy));
        Assert.Equal(rollback.InvalidatedArtifactIds.Order(), listed.InvalidatedArtifactIds.Order());
        Assert.Equal(rollback.CancelledJobIds, listed.CancelledJobIds);
        var versions = await VersionsAsync(seeded.Fixture.Viewer.Client, id);
        Assert.Equal(
            [(4, Phase.Planning, false, (RollbackTargetBlock?)RollbackTargetBlock.CurrentVersion), (3, Phase.Planning, true, null), (2, Phase.Specification, true, null), (1, Phase.Refinement, true, null)],
            versions.Select(v => (v.Version, v.Phase, v.RollbackAllowed, v.RollbackBlockedReason)));
    }

    [Fact]
    public async Task FR44_Versions_list_every_recorded_version_with_whether_it_is_a_target()
    {
        using var seeded = await SeedVersion7InReviewAsync();

        var versions = await VersionsAsync(seeded.Fixture.Viewer.Client, seeded.Id);

        Assert.Equal([7, 6, 5, 4, 3, 2, 1], versions.Select(v => v.Version));
        Assert.Equal((Phase.Review, false, (RollbackTargetBlock?)RollbackTargetBlock.CurrentVersion), (versions[0].Phase, versions[0].RollbackAllowed, versions[0].RollbackBlockedReason));
        Assert.Equal((Phase.Execution, true), (versions[1].Phase, versions[1].RollbackAllowed)); // the latest transition at 6 (a review cycle)
        Assert.Equal(Phase.Planning, versions[3].Phase);
        await Problems.AssertAsync(
            await seeded.Fixture.Outsider.Client.GetAsync(new Uri($"{seeded.Fixture.Path}/versions", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await seeded.Fixture.Outsider.Client.GetAsync(new Uri($"{seeded.Fixture.Path}/rollbacks", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR45_A_second_rollback_within_a_minute_is_RATE_LIMITED_with_Retry_After()
    {
        using var seeded = await SeedVersion7InReviewAsync();
        var client = seeded.Fixture.Owner.Client;
        Assert.Equal(HttpStatusCode.OK, (await RollbackAsync(client, seeded.Id, 5)).StatusCode);

        var second = await RollbackAsync(client, seeded.Id, 4);

        await Problems.AssertAsync(second, HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        var retryAfter = int.Parse(Assert.Single(second.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 60);
        Assert.Equal(5, (await LifecycleSeed.ProjectAsync(factory, seeded.Id)).ProjectVersion);
        Assert.Single(await ProjectApi.WithDbAsync(factory, db => db.RollbackEvents.AsNoTracking().Where(r => r.ProjectId == seeded.Id).ToListAsync(Ct)));
    }

    [Fact]
    public async Task FR45_A_rollback_while_a_step_is_in_progress_is_ROLLBACK_BLOCKED_and_writes_nothing()
    {
        using var seeded = await SeedVersion7InReviewAsync();
        await ProjectApi.WithDbAsync(factory, db => db.ExecutionSteps.Where(s => s.Id == seeded.OptimisationSteps[0].Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StepStatus.InProgress), Ct));

        var blocked = await Problems.AssertAsync(await RollbackAsync(seeded.Fixture.Owner.Client, seeded.Id, 4), HttpStatusCode.Conflict, ErrorCodes.RollbackBlocked);
        Assert.Equal(["reason: step_in_progress"], blocked.GetProperty("details").EnumerateArray().Select(d => d.GetString()));

        var project = await LifecycleSeed.ProjectAsync(factory, seeded.Id);
        Assert.Equal((Phase.Review, 7, (DateTimeOffset?)null), (project.CurrentPhase, project.ProjectVersion, project.LastRollbackAt));
        Assert.Equal(GateStatus.Pending, Assert.Single(await LifecycleSeed.GatesAsync(factory, seeded.Id)).Status);
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, seeded.Id, EventTypes.RollbackCompleted));
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, seeded.Id, EventTypes.ArtifactInvalidated));
    }

    [Fact]
    public async Task FR44_Rollback_checks_the_target_the_reason_and_the_role()
    {
        using var seeded = await SeedVersion7InReviewAsync();
        var fixture = seeded.Fixture;
        await LifecycleSeed.AddTransitionAsync(factory, seeded.Id, Phase.Review, Phase.Release, 7, DateTimeOffset.UtcNow.AddMinutes(-1));
        await LifecycleSeed.SetProjectAsync(factory, seeded.Id, Phase.Release, 8, ApprovalMode.Advisory, ProjectStatus.Released);

        await Problems.AssertAsync(await RollbackAsync(fixture.Contributor.Client, seeded.Id, 4), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await RollbackAsync(fixture.Outsider.Client, seeded.Id, 4), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await RollbackAsync(fixture.Owner.Client, seeded.Id, 4, "   "), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await RollbackAsync(fixture.Owner.Client, seeded.Id, 4, new string('x', 501)), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await RollbackAsync(fixture.Owner.Client, seeded.Id, 0), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await RollbackAsync(fixture.Owner.Client, seeded.Id, 8), HttpStatusCode.BadRequest, ErrorCodes.InvalidTargetVersion);
        await Problems.AssertAsync(await RollbackAsync(fixture.Owner.Client, seeded.Id, 7), HttpStatusCode.BadRequest, ErrorCodes.InvalidTargetVersion); // release phase

        Assert.Equal(Phase.Execution, (await ProjectApi.ReadAsync<RollbackDto>(await RollbackAsync(fixture.Owner.Client, seeded.Id, 6))).RestoredPhase);
    }

    [Fact]
    public async Task FR45_A_rollback_row_without_restored_phase_written_during_a_rolling_update_still_lists()
    {
        using var seeded = await SeedVersion7InReviewAsync();
        var rollback = await ProjectApi.ReadAsync<RollbackDto>(await RollbackAsync(seeded.Fixture.Owner.Client, seeded.Id, 4));

        // A pod of the previous version inserts rollback rows without the column: it is nullable in the database.
        await ProjectApi.WithDbAsync(factory, db => db.Database.ExecuteSqlAsync($"UPDATE rollback_events SET restored_phase = NULL WHERE id = {rollback.Id}", Ct));

        var listed = await ProjectApi.ReadAsync<List<RollbackDto>>(
            await seeded.Fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{seeded.Id}/rollbacks", UriKind.Relative), Ct));
        Assert.Equal((rollback.Id, Phase.Planning), (Assert.Single(listed).Id, listed[0].RestoredPhase)); // from the rollback's transition
    }

    private static Task<HttpResponseMessage> RollbackAsync(HttpClient client, Guid id, int targetVersion, string reason = "Back to an earlier version") =>
        client.PostAsJsonAsync($"/api/projects/{id}/rollback", new { targetVersion, reason }, Ct);

    private static async Task<List<ProjectVersionDto>> VersionsAsync(HttpClient client, Guid id) =>
        await ProjectApi.ReadAsync<List<ProjectVersionDto>>(await client.GetAsync(new Uri($"/api/projects/{id}/versions", UriKind.Relative), Ct));
}
