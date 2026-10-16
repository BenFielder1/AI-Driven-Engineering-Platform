using System.Net;
using Aide.Api.Features.Artifacts;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.IntegrationTests.Artifacts;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Stages;

/// <summary>FR-58 (restore an artifact version), D-V2-15 and FR-59 (history and diff across release versions).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ArtifactRestoreTests(AideApiFactory factory)
{
    private const string Spec = "technical_specification";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR58_Restoring_spec_v2_while_in_specification_creates_v4_draft_with_v2_content_parent_3_and_records_ARTIFACT_RESTORED()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var (v2, _) = await SeedSpecsAsync(id, Phase.Specification);

        var restored = await ArtifactApi.ReadAsync(await RestoreAsync(fixture.Contributor.Client, id, Spec, 2), HttpStatusCode.Created);

        Assert.Equal((4, (int?)3, (int?)2, ArtifactStatus.Draft), (restored.Version, restored.ParentVersion, restored.RestoredFromVersion, restored.Status));
        Assert.Equal((v2.Content, v2.ContentHash), (restored.Content, restored.ContentHash));
        Assert.Equal((fixture.Project.ReleaseVersionId!.Value, fixture.Project.DevelopmentVersion), (restored.ReleaseVersionId, restored.ReleaseVersion));
        Assert.Equal(ArtifactIntegrity.Ok, restored.Integrity);
        var restoredEvent = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactRestored)).Payload);
        Assert.Equal((4, 3, 2), (restoredEvent.GetProperty("version").GetInt32(), restoredEvent.GetProperty("parentVersion").GetInt32(), restoredEvent.GetProperty("restoredFromVersion").GetInt32()));

        // FR-17: the restored draft is approved as usual.
        var approved = await ArtifactApi.ReadAsync(await ArtifactApi.DecideAsync(fixture.Owner.Client, id, restored.Id, "approve"));
        Assert.Equal(ArtifactStatus.Locked, approved.Status);
    }

    [Fact]
    public async Task FR58_Once_the_project_has_advanced_to_planning_restoring_spec_v2_is_RESTORE_NOT_ALLOWED_stage_completed()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedSpecsAsync(id, Phase.Planning);

        var body = await Problems.AssertAsync(await RestoreAsync(fixture.Owner.Client, id, Spec, 2), HttpStatusCode.Conflict, ErrorCodes.RestoreNotAllowed);

        Assert.Equal(RestoreReasons.StageCompleted, body.GetProperty("reason").GetString());
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactRestored));
    }

    [Fact]
    public async Task FR58_A_restore_supersedes_the_open_draft_of_the_type()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 3, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 2, content: "# Spec v2");
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Draft, version: 3, content: "# Spec v3 draft");

        var restored = await ArtifactApi.ReadAsync(await RestoreAsync(fixture.Contributor.Client, id, Spec, 2), HttpStatusCode.Created);

        Assert.Equal((4, (int?)3), (restored.Version, restored.ParentVersion));
        var statuses = await ProjectApi.WithDbAsync(factory, db => db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == id && a.Type == ArtifactType.TechnicalSpecification)
            .OrderBy(a => a.Version)
            .Select(a => a.Status)
            .ToListAsync(Ct));
        Assert.Equal([ArtifactStatus.Locked, ArtifactStatus.Superseded, ArtifactStatus.Draft], statuses);
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactSuperseded));
    }

    [Fact]
    public async Task FR58_Restoring_the_latest_version_is_VALIDATION_and_an_unknown_version_NOT_FOUND()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedSpecsAsync(id, Phase.Specification);

        await Problems.AssertAsync(await RestoreAsync(fixture.Contributor.Client, id, Spec, 3), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await RestoreAsync(fixture.Contributor.Client, id, Spec, 9), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await RestoreAsync(fixture.Contributor.Client, id, Spec, 0), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await RestoreAsync(fixture.Contributor.Client, id, "nonsense", 1), HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR58_Viewers_are_forbidden_and_outsiders_see_NOT_FOUND()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedSpecsAsync(id, Phase.Specification);

        await Problems.AssertAsync(await RestoreAsync(fixture.Viewer.Client, id, Spec, 2), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await RestoreAsync(fixture.Outsider.Client, id, Spec, 2), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR58_While_releasing_restore_is_RESTORE_NOT_ALLOWED_releasing()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedSpecsAsync(id, Phase.Specification);
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 3, ApprovalMode.Advisory, ProjectStatus.Releasing);

        var body = await Problems.AssertAsync(await RestoreAsync(fixture.Contributor.Client, id, Spec, 2), HttpStatusCode.Conflict, ErrorCodes.RestoreNotAllowed);

        Assert.Equal(RestoreReasons.Releasing, body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task FR58_A_corrupted_version_is_RESTORE_NOT_ALLOWED_corrupted()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var (v2, _) = await SeedSpecsAsync(id, Phase.Specification);
        await ProjectApi.WithDbAsync(factory, db => db.Artifacts.Where(a => a.Id == v2.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Content, "# Tampered"), Ct));

        var body = await Problems.AssertAsync(await RestoreAsync(fixture.Contributor.Client, id, Spec, 2), HttpStatusCode.Conflict, ErrorCodes.RestoreNotAllowed);

        Assert.Equal(RestoreReasons.Corrupted, body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task FR58_A_version_of_an_earlier_release_version_is_RESTORE_NOT_ALLOWED_other_release_version()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.2.0");
        var id = fixture.Project.Id;
        await SeedAcrossVersionsAsync(id);
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Specification, 3, ApprovalMode.Advisory);

        var body = await Problems.AssertAsync(await RestoreAsync(fixture.Contributor.Client, id, Spec, 3), HttpStatusCode.Conflict, ErrorCodes.RestoreNotAllowed);

        Assert.Equal(RestoreReasons.OtherReleaseVersion, body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task FR58_The_codebase_summary_is_never_restorable()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Refinement, 1, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.CodebaseSummary, version: 1);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.CodebaseSummary, version: 2);

        var body = await Problems.AssertAsync(await RestoreAsync(fixture.Contributor.Client, id, "codebase_summary", 1), HttpStatusCode.Conflict, ErrorCodes.RestoreNotAllowed);

        Assert.Equal(RestoreReasons.StageCompleted, body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task FR59_History_across_release_versions_lists_every_version_with_its_release_version_and_diffs_v3_against_v6()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, initialVersion: "1.2.0");
        var id = fixture.Project.Id;
        await SeedAcrossVersionsAsync(id);

        var scoped = await ProjectApi.ReadAsync<IReadOnlyList<ArtifactSummaryDto>>(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, $"/{Spec}/history"));
        var all = await ProjectApi.ReadAsync<IReadOnlyList<ArtifactSummaryDto>>(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, $"/{Spec}/history?allReleaseVersions=true"));
        var diff = await ProjectApi.ReadAsync<ArtifactDiffDto>(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, $"/{Spec}/diff?from=3&to=6"));

        Assert.Equal([6], scoped.Select(a => a.Version));
        Assert.Equal([(6, "1.2.0"), (3, "1.1.0")], all.Select(a => (a.Version, a.ReleaseVersion)));
        Assert.Contains(diff.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "Adds exports.");
        Assert.Contains(diff.Lines, l => l.Kind == DiffLineKind.Removed && l.Text == "The first release.");
    }

    /// <summary>Spec v2 and v3, both locked (v3 current), with the project in <paramref name="phase"/>.</summary>
    private async Task<(Artifact V2, Artifact V3)> SeedSpecsAsync(Guid id, Phase phase)
    {
        await LifecycleSeed.SetProjectAsync(factory, id, phase, 3, ApprovalMode.Advisory);
        var v2 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 2, content: "# Spec\n\nVersion two.");
        var v3 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 3, content: "# Spec\n\nVersion three.");
        return (v2, v3);
    }

    /// <summary>A released 1.1.0 with spec v3, and spec v6 in the 1.2.0 in development.</summary>
    private Task SeedAcrossVersionsAsync(Guid id) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var earlier = DateTimeOffset.UtcNow.AddDays(-7);
            var released = new ReleaseVersion
            {
                ProjectId = id,
                Version = "1.1.0",
                Status = ReleaseVersionStatus.Released,
                Tag = "v1.1.0",
                Request = "First release",
                CreatedAt = earlier,
                ReleasedAt = earlier.AddDays(1),
            };
            db.ReleaseVersions.Add(released);
            db.Artifacts.Add(SpecArtifact(id, 3, "# Spec\n\nThe first release.", released.Id, earlier));
            var current = await db.Projects.AsNoTracking().Where(p => p.Id == id).Select(p => p.CurrentReleaseVersionId!.Value).SingleAsync(Ct);
            db.Artifacts.Add(SpecArtifact(id, 6, "# Spec\n\nAdds exports.", current, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Ct);
        });

    private static Artifact SpecArtifact(Guid projectId, int version, string content, Guid releaseVersionId, DateTimeOffset createdAt) => new()
    {
        ProjectId = projectId,
        Type = ArtifactType.TechnicalSpecification,
        Version = version,
        ParentVersion = version - 1,
        Content = content,
        ContentHash = ContentHash.Compute(content),
        Status = ArtifactStatus.Locked,
        CheckpointAtCreation = 2,
        ReleaseVersionId = releaseVersionId,
        CreatedAt = createdAt,
    };

    private static Task<HttpResponseMessage> RestoreAsync(HttpClient client, Guid id, string type, int version) =>
        client.PostAsync(new Uri($"/api/projects/{id}/artifacts/{type}/versions/{version}/restore", UriKind.Relative), null, Ct);
}
