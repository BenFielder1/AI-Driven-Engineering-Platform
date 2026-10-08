using System.Net;
using Aide.Api.Features.Artifacts;
using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.IntegrationTests.Support;

namespace Aide.IntegrationTests.Artifacts;

/// <summary>FR-20: every artifact read re-checks the content hash.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ArtifactIntegrityTests(AideApiFactory factory)
{
    [Fact]
    public async Task FR20_A_row_altered_in_the_database_reads_as_corrupted_raises_an_alert_and_regenerates_once()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        var spec = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, projectVersionAtCreation: 2);
        await ArtifactApi.TamperAsync(factory, spec.Id, "# Spec, edited behind the API's back");

        var read = await ArtifactApi.ReadAsync(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, "/technical_specification/versions/1"));

        Assert.Equal((ArtifactIntegrity.Corrupted, ArtifactStatus.Corrupted), (read.Integrity, read.Status));
        Assert.Equal(ArtifactStatus.Corrupted, (await ArtifactApi.RowAsync(factory, spec.Id)).Status);
        var alert = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.IntegrityAlert)).Payload);
        Assert.Equal((spec.Id.ToString(), spec.ContentHash), (alert.GetProperty("artifactId").GetString(), alert.GetProperty("expectedHash").GetString()));
        var corrupted = Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactCorrupted));
        Assert.Equal(SystemActor.Name, corrupted.Actor);
        Assert.Equal(EventCategory.Integrity, EventTypes.CategoryOf(EventTypes.IntegrityAlert));

        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
        Assert.Equal(JobTypes.SpecGeneration, job.Type);
        var payload = LifecycleSeed.Payload(job.Payload);
        Assert.Equal(("artifact_corrupted", spec.Id.ToString()), (payload.GetProperty("trigger").GetString(), payload.GetProperty("artifactId").GetString()));

        // Later reads (any endpoint) report corrupted without raising the alert again.
        var history = await ProjectApi.ReadAsync<List<ArtifactSummaryDto>>(await ArtifactApi.GetAsync(fixture.Owner.Client, id, "/technical_specification/history"));
        Assert.Equal(ArtifactIntegrity.Corrupted, Assert.Single(history).Integrity);
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.IntegrityAlert));
        Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
    }

    [Fact]
    public async Task FR20_Concurrent_reads_of_a_corrupted_row_raise_one_alert_and_one_job()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var request = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest);
        await ArtifactApi.TamperAsync(factory, request.Id, "tampered");

        var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ArtifactApi.GetAsync(fixture.Owner.Client, id, "/project_request/latest")));

        foreach (var response in reads)
        {
            Assert.Equal(ArtifactIntegrity.Corrupted, (await ArtifactApi.ReadAsync(response)).Integrity);
        }

        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.IntegrityAlert));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactCorrupted));
        Assert.Equal([JobTypes.IdeaRefinement], (await LifecycleSeed.JobsAsync(factory, id)).Select(j => j.Type));
    }

    [Fact]
    public async Task FR20_A_corrupted_human_authored_artifact_raises_the_alert_but_reruns_no_agent()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var draft = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, ArtifactStatus.Draft, createdByUserId: fixture.Owner.User!.Id);
        await ArtifactApi.TamperAsync(factory, draft.Id, "tampered");

        var list = await ProjectApi.ReadAsync<List<ArtifactSummaryDto>>(await ArtifactApi.GetAsync(fixture.Owner.Client, id, "/"));

        Assert.Equal((ArtifactIntegrity.Corrupted, ArtifactStatus.Corrupted), (Assert.Single(list).Integrity, list[0].Status));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.IntegrityAlert));
        Assert.Empty(await LifecycleSeed.JobsAsync(factory, id));
    }

    [Fact]
    public async Task FR20_Approving_a_corrupted_draft_is_refused_and_the_draft_is_marked_corrupted()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var draft = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, ArtifactStatus.Draft);
        await ArtifactApi.TamperAsync(factory, draft.Id, "tampered");

        var response = await ArtifactApi.DecideAsync(fixture.Owner.Client, id, draft.Id, "approve");

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.ArtifactNotDraft);
        Assert.Equal(ArtifactStatus.Corrupted, (await ArtifactApi.RowAsync(factory, draft.Id)).Status);
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.IntegrityAlert));
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ArtifactApproved));
    }

    [Fact]
    public async Task FR20_When_the_list_is_the_first_read_of_a_corrupted_current_version_it_alerts_and_regenerates_once()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Superseded, version: 1, projectVersionAtCreation: 2);
        var current = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, version: 2, projectVersionAtCreation: 2);
        await ArtifactApi.TamperAsync(factory, current.Id, "tampered");

        var list = await ProjectApi.ReadAsync<List<ArtifactSummaryDto>>(await ArtifactApi.GetAsync(fixture.Viewer.Client, id, "/"));

        var spec = Assert.Single(list);
        Assert.Equal((2, ArtifactIntegrity.Corrupted, ArtifactStatus.Corrupted), (spec.Version, spec.Integrity, spec.Status));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.IntegrityAlert));
        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id));
        Assert.Equal((JobTypes.SpecGeneration, current.Id.ToString()), (job.Type, LifecycleSeed.Payload(job.Payload).GetProperty("artifactId").GetString()));
    }

    [Fact]
    public async Task FR20_When_history_is_the_first_read_older_corrupted_versions_are_alerted_but_not_regenerated()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Advisory);
        var v1 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Locked, version: 1, projectVersionAtCreation: 2);
        var v2 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Rejected, version: 2, projectVersionAtCreation: 2);
        var v3 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.TechnicalSpecification, ArtifactStatus.Locked, version: 3, projectVersionAtCreation: 2);
        await ArtifactApi.TamperAsync(factory, v1.Id, "tampered");
        await ArtifactApi.TamperAsync(factory, v2.Id, "tampered");

        var history = await ProjectApi.ReadAsync<List<ArtifactSummaryDto>>(await ArtifactApi.GetAsync(fixture.Owner.Client, id, "/technical_specification/history"));

        Assert.Equal(
            [(3, ArtifactIntegrity.Ok), (2, ArtifactIntegrity.Corrupted), (1, ArtifactIntegrity.Corrupted)],
            history.Select(h => (h.Version, h.Integrity)));
        var alerts = await LifecycleSeed.EventsAsync(factory, id, EventTypes.IntegrityAlert);
        Assert.Equal(
            new[] { v1.Id.ToString(), v2.Id.ToString() }.Order(),
            alerts.Select(a => LifecycleSeed.Payload(a.Payload).GetProperty("artifactId").GetString()).Order());
        Assert.All(alerts, a => Assert.Equal(System.Text.Json.JsonValueKind.Null, LifecycleSeed.Payload(a.Payload).GetProperty("rerunAgent").ValueKind));
        Assert.Empty(await LifecycleSeed.JobsAsync(factory, id));

        // The current version corrupted later does regenerate.
        await ArtifactApi.TamperAsync(factory, v3.Id, "tampered");
        await ArtifactApi.GetAsync(fixture.Owner.Client, id, "/technical_specification/history");
        Assert.Equal(3, (await LifecycleSeed.EventsAsync(factory, id, EventTypes.IntegrityAlert)).Count);
        Assert.Equal([JobTypes.SpecGeneration], (await LifecycleSeed.JobsAsync(factory, id)).Select(j => j.Type));
    }

    [Fact]
    public async Task FR20_When_history_is_the_first_read_of_a_corrupted_open_draft_it_regenerates()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, ArtifactStatus.Locked, version: 1);
        var draft = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, ArtifactStatus.Draft, version: 2);
        await ArtifactApi.TamperAsync(factory, draft.Id, "tampered");

        var history = await ProjectApi.ReadAsync<List<ArtifactSummaryDto>>(await ArtifactApi.GetAsync(fixture.Owner.Client, id, "/project_request/history"));

        Assert.Equal([ArtifactIntegrity.Corrupted, ArtifactIntegrity.Ok], history.Select(h => h.Integrity));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.IntegrityAlert));
        Assert.Equal([JobTypes.IdeaRefinement], (await LifecycleSeed.JobsAsync(factory, id)).Select(j => j.Type));
    }

    [Fact]
    public async Task FR20_Diffing_a_corrupted_version_marks_it_too()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var v1 = await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, ArtifactStatus.Superseded);
        await LifecycleSeed.AddArtifactAsync(factory, id, ArtifactType.ProjectRequest, version: 2);
        await ArtifactApi.TamperAsync(factory, v1.Id, "tampered");

        await ProjectApi.ReadAsync<ArtifactDiffDto>(await ArtifactApi.GetAsync(fixture.Owner.Client, id, "/project_request/diff?from=1&to=2"));

        Assert.Equal(ArtifactStatus.Corrupted, (await ArtifactApi.RowAsync(factory, v1.Id)).Status);
    }
}
