using System.Net;
using Aide.Api.Errors;
using Aide.Api.Features.Artifacts;
using Aide.Api.Features.Refinement;
using Aide.Api.Features.Runs;
using Aide.Domain.Common;
using Aide.IntegrationTests.Artifacts;
using Aide.IntegrationTests.Support;

namespace Aide.IntegrationTests.Stages;

/// <summary>
/// FR-55–FR-59 (M11): the new fields on existing DTOs, release-version scoping of refinement and the RESTORE_NOT_ALLOWED
/// error shape. Behaviour of the stage-page endpoints: <see cref="ArtifactRestoreTests"/> and <see cref="ExecutionRunsTests"/>.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class StagePagesContractTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FR58_RESTORE_NOT_ALLOWED_is_409_and_carries_its_reason()
    {
        Assert.Equal(409, ErrorCodes.StatusCodeFor(ErrorCodes.RestoreNotAllowed));
        var result = ApiProblems.FromError(new DomainError(ErrorCodes.RestoreNotAllowed, "x") { Reason = RestoreReasons.StageCompleted });
        Assert.Equal("stage_completed", result.ProblemDetails.Extensions[ApiProblems.ReasonKey]);
        Assert.Equal(["stage_completed", "stage_not_reached", "other_release_version", "corrupted", "releasing"], RestoreReasons.All);
    }

    [Fact]
    public async Task FR55_Refinement_accepts_releaseVersionId()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var versionId = fixture.Project.ReleaseVersionId!.Value;

        var scoped = await ProjectApi.ReadAsync<RefinementDto>(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/refinement?releaseVersionId={versionId}"));
        var current = await ProjectApi.ReadAsync<RefinementDto>(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/refinement"));

        Assert.Equal(current.LatestDraft?.Id, scoped.LatestDraft?.Id);
        Assert.Equal(current.LatestRound?.JobId, scoped.LatestRound?.JobId);
        await Problems.AssertAsync(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/refinement?releaseVersionId={Guid.NewGuid()}"), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR59_Artifact_versions_carry_their_release_version_semver_and_no_restore_source()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        var created = await ArtifactApi.ReadAsync(await ArtifactApi.CreateDraftAsync(fixture.Owner.Client, id, "project_request", "# Request\n\nBuild it."), HttpStatusCode.Created);
        var history = await ProjectApi.ReadAsync<IReadOnlyList<ArtifactSummaryDto>>(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, "/project_request/history"));

        Assert.Equal((fixture.Project.DevelopmentVersion, (int?)null), (created.ReleaseVersion, created.RestoredFromVersion));
        Assert.All(history, a => Assert.Equal((fixture.Project.DevelopmentVersion, (int?)null), (a.ReleaseVersion, a.RestoredFromVersion)));
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[{\"tool\":\"read_file\"},{\"tool\":\"list_dir\"}]", 2)]
    [InlineData("{}", 0)]
    [InlineData("not json", 0)]
    [InlineData(null, 0)]
    public void FR71_Tool_call_count_is_the_length_of_the_tool_call_array(string? json, int expected) =>
        Assert.Equal(expected, RunEndpoints.ToolCallCount(json));

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path) =>
        client.GetAsync(new Uri(path, UriKind.Relative), Ct);
}
