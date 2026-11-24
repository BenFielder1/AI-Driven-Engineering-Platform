using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Support;
using Tevrin.Pack.Sdlc;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-352, §4.76 (M67): source-of-truth artifacts on <c>fixture-cv</c>: people add and lock versions of the master CV,
/// a run pins the newest locked version at its start and every stage reads it, and a run can't start without one.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class SourceOfTruthTests(TevrinApiFactory factory)
{
    private const string MasterCv = "master_cv";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ArtifactPath(ProjectFixture fixture, string key = MasterCv) =>
        $"{fixture.Path}/pipelines/{FixtureCvPack.PipelineId}/artifacts/{key}";

    private static object Start() => new { pipelineId = FixtureCvPack.PipelineId, approvalMode = "advisory", inputs = new { job = "Staff engineer, platform team" } };

    private static async Task<PipelineArtifactVersionDto> AddAsync(ProjectFixture fixture, string content) =>
        await ProjectApi.ReadAsync<PipelineArtifactVersionDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{ArtifactPath(fixture)}/versions", new { content }, Ct), HttpStatusCode.Created);

    private static async Task<PipelineArtifactVersionDto> LockAsync(ProjectFixture fixture, int version) =>
        await ProjectApi.ReadAsync<PipelineArtifactVersionDto>(
            await fixture.Owner.Client.PostAsync($"{ArtifactPath(fixture)}/versions/{version}/lock", null, Ct));

    private static async Task<PipelineArtifactVersionDto> AddLockedAsync(ProjectFixture fixture, string content) =>
        await LockAsync(fixture, (await AddAsync(fixture, content)).Version);

    [Fact]
    public async Task FR352_the_4_76_example_a_running_run_keeps_v2_and_the_next_run_pins_v3()
    {
        await using var app = FixtureCvApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        await AddLockedAsync(fixture, "CV v1");
        var v2 = await AddLockedAsync(fixture, "CV v2");

        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", Start(), Ct), HttpStatusCode.Created);
        Assert.Equal(new PipelineRunPinDto(MasterCv, v2.Id, 2), Assert.Single(run.PinnedArtifacts));

        // The fit scorer runs; then a user locks v3 while the run is at the tailor.
        await AgentJobs.RunAsync(app, Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id)));
        Assert.Equal("tailor", (await GetRunAsync(fixture, run.Id)).CurrentStage);
        var v3 = await AddLockedAsync(fixture, "CV v3");
        Assert.Equal((3, ArtifactStatus.Locked), (v3.Version, v3.Status));

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var done = await GetRunAsync(fixture, run.Id);
        Assert.Equal((GenericRunStatus.Completed, 2), (done.Status, done.AgentRuns));
        var agentRuns = await ProjectApi.WithDbAsync(app, db => db.AgentRuns.AsNoTracking().Where(r => r.PipelineRunId == run.Id).ToListAsync(Ct));
        Assert.Equal(["score", "tailor"], agentRuns.Select(r => r.Stage).Order());
        Assert.All(agentRuns, r => Assert.Equal((v2.Id, 2), r.InputArtifacts.Where(i => i.Type == MasterCv).Select(i => (i.ArtifactId, i.Version)).Single()));

        var next = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/pipeline-runs", Start(), Ct), HttpStatusCode.Created);
        Assert.Equal(new PipelineRunPinDto(MasterCv, v3.Id, 3), Assert.Single(next.PinnedArtifacts));
        Assert.Equal(2, (await GetRunAsync(fixture, run.Id)).PinnedArtifacts.Single().Version);
    }

    [Fact]
    public async Task FR352_SOURCE_ARTIFACT_MISSING_when_the_master_cv_has_no_locked_version()
    {
        await using var app = FixtureCvApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var url = $"{fixture.Path}/pipeline-runs";

        var none = await Problems.AssertAsync(await fixture.Contributor.Client.PostAsJsonAsync(url, Start(), Ct), HttpStatusCode.Conflict, ErrorCodes.SourceArtifactMissing);
        Assert.Equal("Master CV", none.GetProperty("reason").GetString());
        Assert.Equal([MasterCv], none.GetProperty("details").EnumerateArray().Select(d => d.GetString()));

        await AddAsync(fixture, "Only a draft");
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsJsonAsync(url, Start(), Ct), HttpStatusCode.Conflict, ErrorCodes.SourceArtifactMissing);
        Assert.Empty(await ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking()
            .Where(r => r.ProjectId == fixture.Project.Id && r.PipelineId == FixtureCvPack.PipelineId).ToListAsync(Ct)));

        await LockAsync(fixture, 1);
        await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Contributor.Client.PostAsJsonAsync(url, Start(), Ct), HttpStatusCode.Created);
    }

    [Fact]
    public async Task FR352_project_scope_rows_have_no_run_or_release_version_and_stay_out_of_sdlc_lists()
    {
        await using var app = FixtureCvApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var locked = await AddLockedAsync(fixture, "CV v1");
        var draft = await AddAsync(fixture, "CV v2");
        Assert.Equal((2, 1, ArtifactStatus.Draft), (draft.Version, draft.ParentVersion, draft.Status));

        var rows = await ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking().Where(a => a.ProjectId == fixture.Project.Id && a.Type == MasterCv).ToListAsync(Ct));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, a => Assert.Equal((ArtifactScope.Project, (Guid?)null, (Guid?)null), (a.Scope, a.ReleaseVersionId, a.PipelineRunId)));

        // A project-scope row of an SDLC type next to the SDLC's own row of that type and version: the SDLC lists show
        // the SDLC row and not the project-scope one, so the test fails if the SDLC filter lets project-scope rows through.
        var id = fixture.Project.Id;
        var seeded = await ProjectApi.WithDbAsync(app, async db =>
        {
            var releaseVersionId = (await db.PipelineRuns.AsNoTracking().FirstAsync(r => r.ProjectId == id && r.SubjectId != null, Ct)).SubjectId!.Value;
            var sdlcRow = NewArtifact(id, releaseVersionId, ArtifactScope.Run);
            var projectRow = NewArtifact(id, null, ArtifactScope.Project);
            db.Artifacts.AddRange(sdlcRow, projectRow);
            await db.SaveChangesAsync(Ct);
            return (Sdlc: sdlcRow.Id, Project: projectRow.Id);
        });
        Assert.Null(await ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking().Where(a => a.Id == seeded.Project).Select(a => a.ReleaseVersionId).SingleAsync(Ct)));

        var latest = await ProjectApi.ReadAsync<List<JsonElement>>(await fixture.Viewer.Client.GetAsync($"{fixture.Path}/artifacts", Ct));
        var history = await ProjectApi.ReadAsync<List<JsonElement>>(
            await fixture.Viewer.Client.GetAsync($"{fixture.Path}/artifacts/{SdlcArtifacts.TechnicalSpecification}/history?allReleaseVersions=true", Ct));
        foreach (var list in new[] { latest, history })
        {
            var ids = list.Select(a => a.GetProperty("id").GetGuid()).ToList();
            Assert.Contains(seeded.Sdlc, ids);
            Assert.DoesNotContain(seeded.Project, ids);
            Assert.DoesNotContain(locked.Id, ids);
            Assert.DoesNotContain(draft.Id, ids);
        }

        var versions = await ProjectApi.ReadAsync<List<PipelineArtifactVersionDto>>(await fixture.Viewer.Client.GetAsync(ArtifactPath(fixture), Ct));
        Assert.Equal([2, 1], versions.Select(v => v.Version));
        Assert.All(versions, v => Assert.Equal("Master CV", v.Title));
    }

    [Fact]
    public async Task FR352_a_new_version_supersedes_open_drafts_and_an_older_draft_cannot_be_locked()
    {
        await using var app = FixtureCvApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var v1 = await AddAsync(fixture, "CV v1");
        await AddAsync(fixture, "CV v2");

        var versions = await ProjectApi.ReadAsync<List<PipelineArtifactVersionDto>>(await fixture.Viewer.Client.GetAsync(ArtifactPath(fixture), Ct));
        Assert.Equal([(2, ArtifactStatus.Draft), (1, ArtifactStatus.Superseded)], versions.Select(v => (v.Version, v.Status)));
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsync($"{ArtifactPath(fixture)}/versions/{v1.Version}/lock", null, Ct), HttpStatusCode.Conflict, ErrorCodes.ArtifactNotDraft);

        // A draft older than the newest locked version (written directly: the API supersedes it) is CONFLICT.
        await LockAsync(fixture, 2);
        await ProjectApi.WithDbAsync(app, async db =>
        {
            var row = await db.Artifacts.FirstAsync(a => a.ProjectId == fixture.Project.Id && a.Type == MasterCv && a.Version == 1, Ct);
            row.Status = ArtifactStatus.Draft;
            await db.SaveChangesAsync(Ct);
        });
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsync($"{ArtifactPath(fixture)}/versions/1/lock", null, Ct), HttpStatusCode.Conflict, ErrorCodes.Conflict);
    }

    [Fact]
    public async Task FR352_artifact_endpoints_check_roles_keys_content_and_lock_rules()
    {
        await using var app = FixtureCvApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var versions = $"{ArtifactPath(fixture)}/versions";

        await Problems.AssertAsync(await fixture.Viewer.Client.PostAsJsonAsync(versions, new { content = "x" }, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(ArtifactPath(fixture), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync(versions, new { content = new { not = "markdown" } }, Ct), HttpStatusCode.BadRequest, ErrorCodes.InputInvalid);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsJsonAsync(versions, new { }, Ct), HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // Only project-scope artifacts of a registered pipeline are edited here.
        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync($"{ArtifactPath(fixture, "fit")}/versions", new { content = "x" }, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/pipelines/no-such-pipeline/artifacts/{MasterCv}/versions", new { content = "x" }, Ct),
            HttpStatusCode.NotFound, ErrorCodes.NotFound);

        var draft = await AddAsync(fixture, "CV v1");
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync($"{versions}/{draft.Version}/lock", null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync($"{versions}/9/lock", null, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        var locked = await LockAsync(fixture, draft.Version);
        Assert.Equal(ArtifactStatus.Locked, locked.Status);
        // FR-17: locking a locked version again is a no-op.
        var again = await LockAsync(fixture, draft.Version);
        Assert.Equal((locked.Id, ArtifactStatus.Locked, locked.Hash), (again.Id, again.Status, again.Hash));
    }

    private static Artifact NewArtifact(Guid projectId, Guid? releaseVersionId, ArtifactScope scope)
    {
        const string content = "{}";
        return new Artifact
        {
            ProjectId = projectId,
            ReleaseVersionId = releaseVersionId,
            Scope = scope,
            Type = SdlcArtifacts.TechnicalSpecification,
            Version = 900,
            Content = content,
            ContentHash = ContentHash.Compute(content),
            Status = ArtifactStatus.Locked,
            CheckpointAtCreation = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static async Task<PipelineRunDto> GetRunAsync(ProjectFixture fixture, Guid runId) =>
        await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Owner.Client.GetAsync($"/api/pipeline-runs/{runId}", Ct));
}
