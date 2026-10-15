using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Rollback;
using Aide.Api.Features.Versions;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Git;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Versions;

/// <summary>
/// §4.10, FR-60–FR-63, FR-66, D-V2-11: release versions (<c>GET/POST /versions</c>), checkpoints (<c>GET /checkpoints</c>),
/// version-scoped rollback, initial versions and the projects list badges and filter.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class VersionEndpointsTests(AideApiFactory factory)
{
    private const string MergeSha = "0123456789abcdef0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- FR-60: start a new version ----------

    [Fact]
    public async Task FR60_Starting_1_2_0_after_released_1_1_0_moves_to_refinement_records_VERSION_STARTED_and_queues_the_analyst()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.1.0");
        var id = fixture.Project.Id;
        var released = await MarkReleasedAsync(id);

        var response = await StartAsync(fixture.Owner.Client, id, "1.2.0", "  Add CSV export  ");

        var started = await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(response, HttpStatusCode.Created);
        var version = started.ReleaseVersion;
        Assert.Equal(("1.2.0", ReleaseVersionStatus.InDevelopment, "v1.1.0", MergeSha, "Add CSV export"),
            (version.Version, version.Status, version.BaseTag, version.BaseCommitSha, version.Request));
        Assert.Null(version.BranchName); // stored when the working copy is first prepared (FR-82)
        Assert.Equal((fixture.Owner.User!.Id, (string?)null, (string?)null), (version.StartedBy!.Id, version.Tag, version.PresetId));
        Assert.Equal(new ReleaseVersionCheckpointsDto(1, 1), version.Checkpoints);

        // The badges read "Released 1.1.0" and "In development 1.2.0"; the lifecycle restarts at checkpoint 1.
        var state = started.State;
        Assert.Equal((Phase.Refinement, ProjectStatus.Active, 1, "1.1.0", "1.2.0", version.Id),
            (state.Phase, state.Status, state.Checkpoint, state.ReleasedVersion, state.DevelopmentVersion, state.ReleaseVersionId));
        Assert.Equal((0, false), (state.Review.Cycle, state.ExecutionPaused));
        var project = await ProjectApi.ReadAsync<ProjectDto>(await fixture.Viewer.Client.GetAsync(new Uri(fixture.Path, UriKind.Relative), Ct));
        Assert.Equal(("Add CSV export", 1, "1.1.0", "1.2.0", version.Id), (project.Idea, project.Checkpoint, project.ReleasedVersion, project.DevelopmentVersion, project.ReleaseVersionId));

        // The transition, its event and VERSION_STARTED, in the new version.
        var transition = Assert.Single(await LifecycleSeed.TransitionsAsync(factory, id), t => t.ReleaseVersionId == version.Id);
        Assert.Equal((Phase.Release, Phase.Refinement, 1, version.Id), (transition.FromPhase, transition.ToPhase, transition.Checkpoint, transition.ReleaseVersionId));
        var changed = LifecycleSeed.Payload((await LifecycleSeed.EventsAsync(factory, id, EventTypes.PhaseChanged)).Last().Payload);
        Assert.Equal(("new_version", 1), (changed.GetProperty("kind").GetString(), changed.GetProperty("checkpoint").GetInt32()));
        var startedEvent = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.VersionStarted)).Payload);
        Assert.Equal((version.Id.ToString(), "1.2.0", "v1.1.0"), (startedEvent.GetProperty("releaseVersionId").GetString(), startedEvent.GetProperty("version").GetString(), startedEvent.GetProperty("baseTag").GetString()));

        // The Repository Analyst job is queued; until the analyst exists (FR-70) its handler completes without work.
        var analysis = Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.RepositoryAnalysis);
        Assert.Equal(version.Id.ToString(), LifecycleSeed.Payload(analysis.Payload).GetProperty("releaseVersionId").GetString());
        Assert.Equal(JobStatus.Succeeded, Assert.Single(await AgentJobs.RunQueuedAsync(factory, id), j => j.Id == analysis.Id).Status);

        // GET /versions: highest first, with the released baseline.
        var versions = await ListAsync(fixture.Viewer.Client, id);
        Assert.Equal([("1.2.0", ReleaseVersionStatus.InDevelopment), ("1.1.0", ReleaseVersionStatus.Released)], versions.Select(v => (v.Version, v.Status)));
        Assert.Equal((released, "v1.1.0", MergeSha, 6, 6), (versions[1].Id, versions[1].Tag, versions[1].MergeCommitSha, versions[1].Checkpoints.Current, versions[1].Checkpoints.Max));
    }

    [Fact]
    public async Task FR60_A_release_awaiting_merge_is_RELEASE_NOT_MERGED()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.1.0");
        var id = fixture.Project.Id;
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            await db.ReleaseVersions.Where(v => v.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, ReleaseVersionStatus.AwaitingMerge), Ct);
            await db.Projects.Where(p => p.Id == id).ExecuteUpdateAsync(
                s => s.SetProperty(p => p.CurrentPhase, Phase.Release).SetProperty(p => p.Status, ProjectStatus.Releasing).SetProperty(p => p.Checkpoint, 6), Ct);
        });

        await Problems.AssertAsync(await StartAsync(fixture.Owner.Client, id, "1.2.0"), HttpStatusCode.Conflict, ErrorCodes.ReleaseNotMerged);

        // §4.10: a version awaiting merge can't be rolled back either.
        var blocked = await Problems.AssertAsync(await RollbackAsync(fixture.Owner.Client, id, 1), HttpStatusCode.Conflict, ErrorCodes.RollbackBlocked);
        Assert.Equal(["reason: release_partially_applied"], blocked.GetProperty("details").EnumerateArray().Select(d => d.GetString()));
    }

    // ---------- FR-82: one branch per release version ----------

    [Fact]
    public async Task FR82_FR66_Every_new_version_works_on_aide_slug_v_version_including_the_first_one()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.0.0");
        var id = fixture.Project.Id;
        var slug = fixture.Project.Slug;

        // The first version (FR-66): its branch is stored when the working copy is prepared, and checked out.
        Assert.Equal($"aide/{slug}/v1.0.0", await PrepareAsync(id));
        Assert.Equal($"aide/{slug}/v1.0.0", (await ListAsync(fixture.Viewer.Client, id)).Single().BranchName);
        Assert.Equal($"aide/{slug}/v1.0.0", await TestGit.RunAsync(Path.Combine(factory.WorkspacesRoot, id.ToString("D")), Ct, "symbolic-ref", "--short", "HEAD"));

        // A started version gets its own branch; the released version keeps its own.
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            await db.ReleaseVersions.Where(v => v.ProjectId == id).ExecuteUpdateAsync(
                s => s.SetProperty(v => v.Status, ReleaseVersionStatus.Released).SetProperty(v => v.Tag, "v1.0.0"), Ct);
            await db.Projects.Where(p => p.Id == id).ExecuteUpdateAsync(
                s => s.SetProperty(p => p.CurrentPhase, Phase.Release).SetProperty(p => p.Status, ProjectStatus.Released)
                    .SetProperty(p => p.ReleasedVersion, "1.0.0").SetProperty(p => p.DevelopmentVersion, (string?)null), Ct);
        });
        await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(await StartAsync(fixture.Owner.Client, id, "1.1.0"), HttpStatusCode.Created);

        Assert.Equal($"aide/{slug}/v1.1.0", await PrepareAsync(id));
        var versions = await ListAsync(fixture.Viewer.Client, id);
        Assert.Equal([($"aide/{slug}/v1.1.0", "1.1.0"), ($"aide/{slug}/v1.0.0", "1.0.0")], versions.Select(v => (v.BranchName, v.Version)));
    }

    [Fact]
    public async Task FR82_A_stored_branch_name_such_as_a_migrated_v1_one_is_kept()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var v1Branch = $"aide/{fixture.Project.Slug}-0199abcd";
        await ProjectApi.WithDbAsync(factory, db => db.ReleaseVersions.Where(v => v.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.BranchName, v1Branch), Ct));

        Assert.Equal(v1Branch, await PrepareAsync(id));
    }

    // ---------- FR-61: validation ----------

    [Fact]
    public async Task FR61_Released_1_1_0_refuses_1_0_5_accepts_v1_2_0_normalised_and_refuses_a_second_start()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.1.0");
        var id = fixture.Project.Id;
        await MarkReleasedAsync(id);

        await Problems.AssertAsync(await StartAsync(fixture.Owner.Client, id, "1.0.5"), HttpStatusCode.Conflict, ErrorCodes.VersionNotGreater);
        await Problems.AssertAsync(await StartAsync(fixture.Owner.Client, id, "1.1.0"), HttpStatusCode.Conflict, ErrorCodes.VersionNotGreater);
        await Problems.AssertAsync(await StartAsync(fixture.Owner.Client, id, "01.2.0"), HttpStatusCode.BadRequest, ErrorCodes.VersionInvalid);
        await Problems.AssertAsync(await StartAsync(fixture.Owner.Client, id, "1.2.0+build"), HttpStatusCode.BadRequest, ErrorCodes.VersionInvalid);
        await Problems.AssertAsync(await StartAsync(fixture.Owner.Client, id, "1.2.0", presetId: "nextjs-frontend"), HttpStatusCode.BadRequest, ErrorCodes.PresetNotFound);
        await Problems.AssertAsync(await StartAsync(fixture.Owner.Client, id, "1.2.0", request: "   "), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await StartAsync(fixture.Contributor.Client, id, "1.2.0"), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await StartAsync(fixture.Outsider.Client, id, "1.2.0"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.VersionStarted));

        var started = await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(await StartAsync(fixture.Owner.Client, id, "v1.2.0"), HttpStatusCode.Created);
        Assert.Equal("1.2.0", started.ReleaseVersion.Version);

        await Problems.AssertAsync(await StartAsync(fixture.Owner.Client, id, "1.3.0"), HttpStatusCode.Conflict, ErrorCodes.VersionInProgress);
    }

    [Fact]
    public async Task FR61_A_version_whose_tag_is_already_in_the_repository_is_TAG_EXISTS_and_an_unreleased_project_is_NOT_RELEASED()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.1.0");
        var id = fixture.Project.Id;

        await Problems.AssertAsync(await StartAsync(fixture.Owner.Client, id, "1.2.0"), HttpStatusCode.Conflict, ErrorCodes.VersionInProgress);
        await MarkReleasedAsync(id);

        // Someone already tagged v1.2.0 in the (Local) repository.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var copies = scope.ServiceProvider.GetRequiredService<IWorkingCopies>();
            await copies.InitAsync(id, "main", Ct);
            var head = await copies.ResolveCommitAsync(id, "refs/heads/main", Ct);
            await copies.CreateTagAsync(id, "v1.2.0", head!, "Tagged by hand", Ct);
        }

        await Problems.AssertAsync(await StartAsync(fixture.Owner.Client, id, "1.2.0"), HttpStatusCode.Conflict, ErrorCodes.TagExists);
        Assert.Equal(201, (int)(await StartAsync(fixture.Owner.Client, id, "1.2.1")).StatusCode);

        using var other = await ProjectApi.FixtureAsync(factory);
        await LifecycleSeed.SetProjectAsync(factory, other.Project.Id, Phase.Review, 5, ApprovalMode.Advisory);
        await ProjectApi.WithDbAsync(factory, db => db.ReleaseVersions.Where(v => v.ProjectId == other.Project.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, ReleaseVersionStatus.Released), Ct));
        await Problems.AssertAsync(await StartAsync(other.Owner.Client, other.Project.Id, "0.2.0"), HttpStatusCode.Conflict, ErrorCodes.NotReleased);
    }

    // ---------- FR-63: checkpoints and rollback per version ----------

    [Fact]
    public async Task FR63_Rollback_targets_only_the_checkpoints_of_1_2_0_and_never_touches_1_1_0()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.1.0");
        var id = fixture.Project.Id;
        var old = await MarkReleasedAsync(id);
        var oldSpec = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 5);
        var started = await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(await StartAsync(fixture.Owner.Client, id, "1.2.0"), HttpStatusCode.Created);
        var v120 = started.ReleaseVersion.Id;

        // 1.2.0 climbs to checkpoint 5 in review.
        var t0 = DateTimeOffset.UtcNow;
        (Phase From, Phase To, int Checkpoint)[] history =
        [
            (Phase.Refinement, Phase.Specification, 2), (Phase.Specification, Phase.Planning, 3), (Phase.Planning, Phase.Execution, 4), (Phase.Execution, Phase.Review, 5),
        ];
        for (var i = 0; i < history.Length; i++)
        {
            await LifecycleSeed.AddTransitionAsync(factory, id, history[i].From, history[i].To, history[i].Checkpoint, t0.AddMinutes(i + 1));
        }

        var newSpec = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 2, projectVersionAtCreation: 2);
        var newPlan = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ImplementationPlan, projectVersionAtCreation: 3);
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory);
        Assert.Equal((v120, v120), (newSpec.ReleaseVersionId, newPlan.ReleaseVersionId));

        var checkpoints = await CheckpointsAsync(fixture.Viewer.Client, id);
        Assert.Equal([5, 4, 3, 2, 1], checkpoints.Select(c => c.Checkpoint));
        Assert.Equal([1, 2, 3, 4], checkpoints.Where(c => c.RollbackAllowed).Select(c => c.Checkpoint).Order());
        var oldCheckpoints = await CheckpointsAsync(fixture.Viewer.Client, id, old);
        Assert.All(oldCheckpoints, c => Assert.Equal((false, (CheckpointBlock?)CheckpointBlock.VersionReleased), (c.RollbackAllowed, c.RollbackBlockedReason)));

        var rollback = await ProjectApi.ReadAsync<RollbackDto>(await RollbackAsync(fixture.Owner.Client, id, 2));

        Assert.Equal((v120, 5, 2, Phase.Specification), (rollback.ReleaseVersionId, rollback.FromCheckpoint, rollback.ToCheckpoint, rollback.RestoredPhase));
        Assert.Equal([newPlan.Id], rollback.InvalidatedArtifactIds);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            // 1.1.0's spec was created at checkpoint 5 of its own version: a baseline, never invalidated.
            Assert.Equal(ArtifactStatus.Locked, (await db.Artifacts.AsNoTracking().SingleAsync(a => a.Id == oldSpec.Id, Ct)).Status);
            Assert.Equal(ArtifactStatus.Locked, (await db.Artifacts.AsNoTracking().SingleAsync(a => a.Id == newSpec.Id, Ct)).Status);
            Assert.Equal(ArtifactStatus.Invalidated, (await db.Artifacts.AsNoTracking().SingleAsync(a => a.Id == newPlan.Id, Ct)).Status);
        });
    }

    [Fact]
    public async Task FR63_A_released_project_with_nothing_in_development_is_ROLLBACK_BLOCKED_version_released()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.1.0");
        var id = fixture.Project.Id;
        await MarkReleasedAsync(id);

        var blocked = await Problems.AssertAsync(await RollbackAsync(fixture.Owner.Client, id, 3), HttpStatusCode.Conflict, ErrorCodes.RollbackBlocked);

        Assert.Equal(["reason: version_released"], blocked.GetProperty("details").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal((Phase.Release, ProjectStatus.Released), ((await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(factory, id)).Status));
    }

    [Fact]
    public async Task S4_10_A_baseline_draft_of_the_released_version_can_not_be_approved()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.1.0");
        var id = fixture.Project.Id;
        await MarkReleasedAsync(id);
        var stray = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.OptimizationPlan, ArtifactStatus.Draft, projectVersionAtCreation: 6);
        await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(await StartAsync(fixture.Owner.Client, id, "1.2.0"), HttpStatusCode.Created);

        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/artifacts/{stray.Id}/approve", new { }, Ct), HttpStatusCode.Conflict, ErrorCodes.WrongPhase);
        Assert.Equal(ArtifactStatus.Draft, (await ProjectApi.WithDbAsync(factory, db => db.Artifacts.AsNoTracking().SingleAsync(a => a.Id == stray.Id, Ct))).Status);
    }

    [Fact]
    public async Task S4_10_D_V2_3_A_new_draft_in_1_2_0_never_supersedes_a_draft_left_in_closed_1_1_0()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.1.0");
        var id = fixture.Project.Id;
        await MarkReleasedAsync(id);
        var leftover = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, ArtifactStatus.Draft, projectVersionAtCreation: 6);
        await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(await StartAsync(fixture.Owner.Client, id, "1.2.0"), HttpStatusCode.Created);

        var draft = await ProjectApi.ReadAsync<Aide.Api.Features.Artifacts.ArtifactDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/artifacts/project_request", new { content = "# CSV export" }, Ct), HttpStatusCode.Created);

        Assert.Equal((2, ArtifactStatus.Draft), (draft.Version, draft.Status)); // numbering stays project-wide
        Assert.NotEqual(leftover.ReleaseVersionId, draft.ReleaseVersionId);
        Assert.Equal(ArtifactStatus.Draft, (await ProjectApi.WithDbAsync(factory, db => db.Artifacts.AsNoTracking().SingleAsync(a => a.Id == leftover.Id, Ct))).Status);
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactSuperseded));
    }

    // ---------- FR-66: initial version ----------

    [Fact]
    public async Task FR66_A_new_project_starts_its_initial_version_in_development_with_nothing_released()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var byDefault = await ProjectApi.CreateAsync(owner.Client);
        var chosen = await ProjectApi.CreateAsync(owner.Client, initialVersion: "v1.0.0");

        Assert.Equal(((string?)null, "0.1.0", 1), (byDefault.ReleasedVersion, byDefault.DevelopmentVersion, byDefault.Checkpoint));
        Assert.Equal(((string?)null, "1.0.0"), (chosen.ReleasedVersion, chosen.DevelopmentVersion));
        var version = Assert.Single(await ListAsync(owner.Client, chosen.Id));
        Assert.Equal(("1.0.0", ReleaseVersionStatus.InDevelopment, chosen.ReleaseVersionId, (string?)null, "v"), (version.Version, version.Status, (Guid?)version.Id, version.BaseTag, version.TagPrefix));
        Assert.Equal(version.Id, Assert.Single(await LifecycleSeed.TransitionsAsync(factory, chosen.Id)).ReleaseVersionId);

        var invalid = await owner.Client.PostAsJsonAsync("/api/projects", new { name = ProjectApi.UniqueName(), idea = "x", git = new { provider = "local" }, initialVersion = "1.0" }, Ct);
        await Problems.AssertAsync(invalid, HttpStatusCode.BadRequest, ErrorCodes.VersionInvalid);
    }

    // ---------- FR-62: list badges and filter ----------

    [Fact]
    public async Task FR62_The_projects_list_shows_both_badges_and_filters_on_a_version_in_development()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var prefix = ProjectApi.UniqueName("badges");
        var developing = await ProjectApi.CreateAsync(owner.Client, name: prefix + "-dev", initialVersion: "1.1.0");
        var released = await ProjectApi.CreateAsync(owner.Client, name: prefix + "-rel", initialVersion: "1.1.0");
        await MarkReleasedAsync(released.Id);
        var iterating = await ProjectApi.CreateAsync(owner.Client, name: prefix + "-it", initialVersion: "1.1.0");
        await MarkReleasedAsync(iterating.Id);
        await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(await StartAsync(owner.Client, iterating.Id, "1.2.0"), HttpStatusCode.Created);

        var all = (await PageAsync(owner.Client, $"search={prefix}")).Items.ToDictionary(p => p.Id);
        Assert.Equal(3, all.Count);
        Assert.Equal(((string?)null, (string?)"1.1.0"), (all[developing.Id].ReleasedVersion, all[developing.Id].DevelopmentVersion));
        Assert.Equal(("1.1.0", (string?)null), (all[released.Id].ReleasedVersion, all[released.Id].DevelopmentVersion));
        Assert.Equal(("1.1.0", "1.2.0", 1), (all[iterating.Id].ReleasedVersion, all[iterating.Id].DevelopmentVersion, all[iterating.Id].Checkpoint));

        var inDevelopment = await PageAsync(owner.Client, $"search={prefix}&hasDevelopmentVersion=true");
        Assert.Equal([developing.Id, iterating.Id], inDevelopment.Items.Select(p => p.Id).Order());
        var notInDevelopment = await PageAsync(owner.Client, $"search={prefix}&hasDevelopmentVersion=false");
        Assert.Equal(released.Id, Assert.Single(notInDevelopment.Items).Id);
        Assert.Equal(1, notInDevelopment.Total);
    }

    // ---------- helpers ----------

    /// <summary>
    /// Marks the project's (only) release version released as <c>v&lt;version&gt;</c> on <see cref="MergeSha"/>, with transitions at
    /// checkpoints 2–6 (6 is the release), and puts the project in release / released at checkpoint 6. Returns the version id.
    /// </summary>
    private async Task<Guid> MarkReleasedAsync(Guid projectId)
    {
        // After the creation transition (checkpoint 1), so the release is the latest transition of the version.
        var t0 = DateTimeOffset.UtcNow.AddSeconds(1);
        (Phase From, Phase To, int Checkpoint)[] history =
        [
            (Phase.Refinement, Phase.Specification, 2), (Phase.Specification, Phase.Planning, 3), (Phase.Planning, Phase.Execution, 4),
            (Phase.Execution, Phase.Review, 5), (Phase.Review, Phase.Release, 6), (Phase.Release, Phase.Release, 6),
        ];
        for (var i = 0; i < history.Length; i++)
        {
            await LifecycleSeed.AddTransitionAsync(factory, projectId, history[i].From, history[i].To, history[i].Checkpoint, t0.AddMinutes(i));
        }

        return await ProjectApi.WithDbAsync(factory, async db =>
        {
            var version = await db.ReleaseVersions.SingleAsync(v => v.ProjectId == projectId, Ct);
            version.Status = ReleaseVersionStatus.Released;
            version.Tag = "v" + version.Version;
            version.MergeCommitSha = MergeSha;
            version.ReleasedAt = t0.AddMinutes(10);
            var project = await db.Projects.SingleAsync(p => p.Id == projectId, Ct);
            project.CurrentPhase = Phase.Release;
            project.Status = ProjectStatus.Released;
            project.Checkpoint = 6;
            project.ReleasedVersion = version.Version;
            project.DevelopmentVersion = null;
            await db.SaveChangesAsync(Ct);
            return version.Id;
        });
    }

    /// <summary>Prepares the project's working copy (FR-35) and returns the branch it is on.</summary>
    private async Task<string> PrepareAsync(Guid projectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var prepared = await scope.ServiceProvider.GetRequiredService<Aide.Api.Features.Execution.WorkingCopyPreparer>().PrepareAsync(projectId, Ct);
        Assert.True(prepared.IsSuccess, prepared.Error?.Message);
        return prepared.Value.Branch;
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, Guid id, string version, string request = "Add CSV export", string? presetId = null) =>
        client.PostAsJsonAsync($"/api/projects/{id}/versions", new { version, request, presetId }, Ct);

    private static Task<HttpResponseMessage> RollbackAsync(HttpClient client, Guid id, int targetCheckpoint) =>
        client.PostAsJsonAsync($"/api/projects/{id}/rollback", new { targetCheckpoint, reason = "Back to an earlier checkpoint" }, Ct);

    private static async Task<List<ReleaseVersionDto>> ListAsync(HttpClient client, Guid id) =>
        await ProjectApi.ReadAsync<List<ReleaseVersionDto>>(await client.GetAsync(new Uri($"/api/projects/{id}/versions", UriKind.Relative), Ct));

    private static async Task<List<CheckpointDto>> CheckpointsAsync(HttpClient client, Guid id, Guid? releaseVersionId = null) =>
        await ProjectApi.ReadAsync<List<CheckpointDto>>(await client.GetAsync(
            new Uri($"/api/projects/{id}/checkpoints{(releaseVersionId is { } v ? $"?releaseVersionId={v}" : string.Empty)}", UriKind.Relative), Ct));

    private static async Task<PagedResult<ProjectSummaryDto>> PageAsync(HttpClient client, string query) =>
        await ProjectApi.ReadAsync<PagedResult<ProjectSummaryDto>>(await client.GetAsync(new Uri($"/api/projects?{query}", UriKind.Relative), Ct));
}
