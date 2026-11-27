using System.Net.Http.Json;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>FR-363, FR-364 (M72): what the engine UI reads for variants: the gate's variants, the chosen one and the tabs.</summary>
public sealed partial class VariantDriverTests
{
    [Fact]
    public async Task FR364_a_variant_select_gate_shows_viewers_its_variants_with_keys_and_versions_and_never_the_content()
    {
        var (app, _, _, _) = App(factory, "v1");
        await using var owned = app;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);
        var gate = await AtGateAsync(app, fixture, runId);
        var variants = (await ArtifactsAsync(app, runId, "headline")).Where(a => a.Variant is not null).ToList();

        var listed = Assert.Single(await ProjectApi.ReadAsync<List<PipelineGateDto>>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{runId}/gates", Ct)));
        var payload = listed.Payload;
        Assert.NotNull(payload);
        Assert.Equal((PipelineGatePayloadDto.VariantSelectKind, "headline", (string?)null), (payload.Kind, payload.Stage, payload.Choice));
        Assert.Equal(["v1", "v2", "v3"], payload.Variants!.Select(v => v.Label));
        Assert.All(payload.Variants!, v =>
        {
            var artifact = variants.Single(a => a.Id == v.ArtifactId);
            Assert.Equal((artifact.Variant, "headline", artifact.Version), (v.Label, v.ArtifactKey, v.ArtifactVersion));
        });
        Assert.Null(payload.Tool);
        Assert.Null(payload.Payload);

        // The inbox carries the same payload; only an Owner may decide.
        var inbox = await ProjectApi.ReadAsync<PipelineGateInboxPageDto>(
            await fixture.Viewer.Client.GetAsync(new Uri("/api/pipeline-gates?gateType=variant_select&limit=200", UriKind.Relative), Ct));
        var item = Assert.Single(inbox.Items, i => i.Gate.Id == gate.Id);
        Assert.False(item.CanDecide);
        Assert.Equal(payload.Variants, item.Gate.Payload!.Variants);

        await ProjectApi.ReadAsync<PipelineGateDto>(await fixture.Owner.Client.PostAsJsonAsync(Approve(runId, gate.Id), new { choice = "v2" }, Ct));
        var decided = Assert.Single(await ProjectApi.ReadAsync<List<PipelineGateDto>>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{runId}/gates", Ct)));
        Assert.Equal("v2", decided.Payload!.Choice);
    }

    [Fact]
    public async Task FR363_stages_mark_the_chosen_variant_and_artifacts_and_timeline_label_each_variant()
    {
        var (app, _, _, _) = App(factory, "v1");
        await using var owned = app;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);
        var gate = await AtGateAsync(app, fixture, runId);

        var before = await ProjectApi.ReadAsync<List<PipelineStageDto>>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{runId}/stages", Ct));
        Assert.Null(before.Single(s => s.Id == "headline").ChosenVariant);

        await ProjectApi.ReadAsync<PipelineGateDto>(await fixture.Owner.Client.PostAsJsonAsync(Approve(runId, gate.Id), new { choice = "v2" }, Ct));
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        // The gate picked v2 for the headline; the judge agent picked v1 for the tagline.
        var stages = await ProjectApi.ReadAsync<List<PipelineStageDto>>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{runId}/stages", Ct));
        Assert.Equal("v2", stages.Single(s => s.Id == "headline").ChosenVariant);
        Assert.Equal("v1", stages.Single(s => s.Id == "tagline").ChosenVariant);

        var artifacts = await ProjectApi.ReadAsync<List<PipelineArtifactDto>>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{runId}/artifacts", Ct));
        var headline = artifacts.Single(a => a.Key == "headline");
        Assert.Equal(["v1", "v2", "v3"], headline.Versions.Select(v => v.Variant).OfType<string>().Order(StringComparer.Ordinal));
        Assert.Single(headline.Versions, v => v.Variant is null);

        var timeline = await ProjectApi.ReadAsync<PipelineTimelinePageDto>(await fixture.Viewer.Client.GetAsync($"/api/pipeline-runs/{runId}/timeline?limit=200", Ct));
        Assert.Equal(
            ["v1", "v2", "v3"],
            timeline.Items.Where(i => i.Kind == PipelineTimelineKind.ArtifactVersion && i.ArtifactKey == "headline").Select(i => i.Variant).OfType<string>().Order(StringComparer.Ordinal));
        Assert.Equal(
            ["v1", "v2", "v3"],
            timeline.Items.Where(i => i.Kind == PipelineTimelineKind.StageEntered && i.StageId == "headline" && i.Branch is not null).Select(i => i.Variant!).Order(StringComparer.Ordinal));
    }
}
