using Microsoft.EntityFrameworkCore;
using Tevrin.Api.Features.Artifacts;
using Tevrin.Api.Features.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Artifacts;
using Tevrin.IntegrationTests.Support;
using Tevrin.Pack.Sdlc;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-334: a run without a subject owns its artifacts and gates. They have no release version, so the SDLC's gate list and
/// artifact history never show them, and they don't collide with the SDLC's per-project uniqueness (artifact versions,
/// one pending gate per type), which applies only to rows with a release version.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class SubjectlessRunRowsTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR334_a_subjectless_runs_artifacts_and_gates_are_ignored_by_the_sdlc_lists()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var sdlcGate = await LifecycleSeed.AddGateAsync(factory, id, SdlcGates.SpecApproval);

        var seeded = await ProjectApi.WithDbAsync(factory, async db =>
        {
            var sdlcRun = await db.PipelineRuns.AsNoTracking().FirstAsync(r => r.ProjectId == id && r.SubjectId != null, Ct);
            var releaseVersionId = sdlcRun.SubjectId!.Value;

            // (1) The SDLC's own v1 of the type: the per-project version index covers it, not the run-owned rows.
            var sdlcArtifact = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(
                a => a.ProjectId == id && a.ReleaseVersionId == releaseVersionId && a.Type == SdlcArtifacts.TechnicalSpecification && a.Version == 1, Ct);
            if (sdlcArtifact is null)
            {
                sdlcArtifact = NewArtifact(id, releaseVersionId, null);
                db.Artifacts.Add(sdlcArtifact);
            }

            // (2) Two open subject-less runs of the same pipeline in the same project, each with a v1 artifact and a
            // pending gate of the same type: the run-scoped indexes and the filtered open-run index allow all of it.
            var rows = new List<(Guid Run, Guid Artifact, Guid Gate)>();
            for (var i = 0; i < 2; i++)
            {
                var run = new PipelineRun
                {
                    ProjectId = id,
                    PipelineId = "subjectless",
                    DefinitionHash = sdlcRun.DefinitionHash,
                    SubjectId = null,
                    CurrentStage = "one",
                    PackStatus = "active",
                    Status = GenericRunStatus.Active,
                    ApprovalMode = sdlcRun.ApprovalMode,
                    StartedAt = DateTimeOffset.UtcNow,
                };
                db.PipelineRuns.Add(run);
                var artifact = NewArtifact(id, null, run.Id);
                db.Artifacts.Add(artifact);
                var gate = new ApprovalGate
                {
                    ProjectId = id,
                    ReleaseVersionId = null,
                    PipelineRunId = run.Id,
                    Type = SdlcGates.SpecApproval,
                    Status = GateStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                db.ApprovalGates.Add(gate);
                rows.Add((run.Id, artifact.Id, gate.Id));
            }

            await db.SaveChangesAsync(Ct);
            return (ReleaseVersionId: releaseVersionId, SdlcArtifactId: sdlcArtifact.Id, Rows: rows);
        });

        var counts = await ProjectApi.WithDbAsync(factory, async db => (
            Runs: await db.PipelineRuns.CountAsync(r => r.ProjectId == id && r.PipelineId == "subjectless" && r.Status == GenericRunStatus.Active, Ct),
            V1s: await db.Artifacts.CountAsync(a => a.ProjectId == id && a.Type == SdlcArtifacts.TechnicalSpecification && a.Version == 1, Ct),
            Pending: await db.ApprovalGates.CountAsync(g => g.ProjectId == id && g.Type == SdlcGates.SpecApproval && g.Status == GateStatus.Pending, Ct)));
        Assert.Equal((2, 3, 3), counts);

        var runOwnedArtifacts = seeded.Rows.Select(r => r.Artifact).ToHashSet();
        var runOwnedGates = seeded.Rows.Select(r => r.Gate).ToHashSet();

        var gates = await ProjectApi.ReadAsync<List<GateDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/gates", UriKind.Relative), Ct));
        Assert.Contains(gates, g => g.Id == sdlcGate.Id);
        Assert.DoesNotContain(gates, g => runOwnedGates.Contains(g.Id));

        // (3) Both history paths: across release versions, and within the SDLC's release version.
        foreach (var query in new[] { "allReleaseVersions=true", $"releaseVersionId={seeded.ReleaseVersionId}" })
        {
            var history = await ProjectApi.ReadAsync<List<ArtifactSummaryDto>>(
                await ArtifactApi.GetAsync(fixture.Viewer.Client, id, $"/{SdlcArtifacts.TechnicalSpecification}/history?{query}"));
            Assert.Contains(history, a => a.Id == seeded.SdlcArtifactId);
            Assert.DoesNotContain(history, a => runOwnedArtifacts.Contains(a.Id));
        }
    }

    private static Artifact NewArtifact(Guid projectId, Guid? releaseVersionId, Guid? pipelineRunId)
    {
        const string content = "{}";
        return new Artifact
        {
            ProjectId = projectId,
            ReleaseVersionId = releaseVersionId,
            PipelineRunId = pipelineRunId,
            Type = SdlcArtifacts.TechnicalSpecification,
            Version = 1,
            Content = content,
            ContentHash = ContentHash.Compute(content),
            Status = ArtifactStatus.Locked,
            CheckpointAtCreation = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
