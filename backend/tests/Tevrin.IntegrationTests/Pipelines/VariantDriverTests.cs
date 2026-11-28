using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-349, §4.67 (M68): multiple output variants in the run driver, end to end on the Fake provider, with
/// <c>fixture-variants</c>: three headline variants picked at a <c>variant_select</c> gate, then two tagline variants a
/// judge agent picks between.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed partial class VariantDriverTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A Fake writer whose answer names the variant its prompt asked for, so each variant's output is its own.</summary>
    private sealed partial class VariantWriter(string agent, string kind) : IFakeAgentResponder
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _prompts = new();

        public string Agent => agent;

        public IReadOnlyList<string> Prompts => [.. _prompts];

        public string Respond(LlmRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var prompt = string.Join("\n", request.Messages.Select(m => m.Content));
            _prompts.Enqueue(prompt);
            return $"{kind} written as variant {VariantLine().Match(prompt).Groups[1].Value} for the fixture.";
        }

        [GeneratedRegex(@"Write variant (v\d+)\.")]
        private static partial Regex VariantLine();
    }

    private static (WebApplicationFactory<Program> App, VariantWriter Copywriter, VariantWriter Tagliner, RecordingResponder Judge) App(
        TevrinApiFactory factory, string judgeChoice)
    {
        var copywriter = new VariantWriter("copywriter", "Headline");
        var tagliner = new VariantWriter("tagliner", "Tagline");
        var judge = new RecordingResponder("tagline-judge", $"{{\"choice\": \"{judgeChoice}\", \"reason\": \"It is the shortest.\"}}");
        return (FixtureFlowApp.Create(factory, new PositionLimit(), copywriter, tagliner, judge), copywriter, tagliner, judge);
    }

    private static async Task<Guid> StartAsync(ProjectFixture fixture)
    {
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs",
                new { pipelineId = FlowFixturePack.Variants, approvalMode = "advisory", inputs = new { product = "A kettle that boils in ten seconds." } },
                Ct),
            HttpStatusCode.Created);
        return run.Id;
    }

    private static Task<PipelineRun> RunAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct));

    private static Task<List<Artifact>> ArtifactsAsync(WebApplicationFactory<Program> app, Guid runId, string type) =>
        ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking()
            .Where(a => a.PipelineRunId == runId && a.Type == type).OrderBy(a => a.CreatedAt).ToListAsync(Ct));

    private static Task<ApprovalGate> VariantGateAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.ApprovalGates.AsNoTracking()
            .SingleAsync(g => g.PipelineRunId == runId && g.Type == EngineGates.VariantSelect, Ct));

    private static Task<List<Job>> FailedDriverJobsAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == EngineJobTypes.StageRun && j.Status == JobStatus.Failed).ToListAsync(Ct));

    private static string Approve(Guid runId, Guid gateId) => $"/api/pipeline-runs/{runId}/gates/{gateId}/approve";

    /// <summary>Drives the headline variants to the <c>variant_select</c> gate.</summary>
    private static async Task<ApprovalGate> AtGateAsync(WebApplicationFactory<Program> app, ProjectFixture fixture, Guid runId)
    {
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);
        var waiting = await RunAsync(app, runId);
        Assert.Equal("headline", waiting.CurrentStage);
        return await VariantGateAsync(app, runId);
    }

    [Fact]
    public async Task FR349_select_gate_raises_variant_select_with_a_redacted_payload_and_the_chosen_v2_goes_downstream()
    {
        var (app, copywriter, tagliner, judge) = App(factory, "v1");
        await using var _ = app;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);

        var gate = await AtGateAsync(app, fixture, runId);

        // Three variants, each its own agent run with its label in the prompt; nothing downstream yet.
        var variants = (await ArtifactsAsync(app, runId, "headline")).Where(a => a.Variant is not null).ToList();
        Assert.Equal(["v1", "v2", "v3"], variants.Select(v => v.Variant!).Order(StringComparer.Ordinal));
        Assert.Equal(3, copywriter.Prompts.Count);
        Assert.All(["v1", "v2", "v3"], l => Assert.Contains(copywriter.Prompts, p => p.Contains($"Write variant {l}.", StringComparison.Ordinal)));
        Assert.Empty(tagliner.Prompts);

        // §5: the payload holds the labels and artifact ids, never the content.
        Assert.Equal(GateStatus.Pending, gate.Status);
        Assert.Equal(["v1", "v2", "v3"], VariantRules.PayloadLabels(gate.Payload));
        Assert.All(variants, v =>
        {
            Assert.Contains(v.Id.ToString(), gate.Payload!, StringComparison.Ordinal);
            Assert.DoesNotContain(v.Content, gate.Payload!, StringComparison.Ordinal);
        });

        await ProjectApi.ReadAsync<PipelineGateDto>(await fixture.Owner.Client.PostAsJsonAsync(Approve(runId, gate.Id), new { choice = "v2" }, Ct));
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Completed, "tagline"), (run.Status, run.CurrentStage));
        Assert.Equal("v2", VariantRules.PayloadChoice((await VariantGateAsync(app, runId)).Payload));

        // The promoted plain version is v2's content; the variants are unchanged.
        var headlines = await ArtifactsAsync(app, runId, "headline");
        var promoted = Assert.Single(headlines, a => a.Variant is null);
        var v2 = headlines.Single(a => a.Variant == "v2");
        Assert.Equal((v2.Content, v2.ContentHash, ArtifactStatus.Locked), (promoted.Content, promoted.ContentHash, promoted.Status));
        Assert.All(headlines.Where(a => a.Variant is not null), a => Assert.Equal(ArtifactStatus.Locked, a.Status));

        // Downstream: every tagline variant read v2's headline, and the judge saw both taglines, labelled.
        Assert.Equal(2, tagliner.Prompts.Count);
        Assert.All(tagliner.Prompts, p => Assert.Contains("Headline written as variant v2", p, StringComparison.Ordinal));
        var judgePrompt = Assert.Single(judge.Prompts);
        Assert.Contains("### v1", judgePrompt, StringComparison.Ordinal);
        Assert.Contains("### v2", judgePrompt, StringComparison.Ordinal);
        Assert.Empty(await FailedDriverJobsAsync(app, fixture.Project.Id));
    }

    [Fact]
    public async Task FR349_VARIANT_CHOICE_INVALID_a_choice_outside_v1_to_vn_is_refused_with_400_and_the_gate_stays_pending()
    {
        var (app, _, tagliner, _) = App(factory, "v1");
        await using var _app = app;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);
        var gate = await AtGateAsync(app, fixture, runId);

        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync(Approve(runId, gate.Id), new { choice = "v4" }, Ct),
            HttpStatusCode.BadRequest,
            ErrorCodes.VariantChoiceInvalid);
        await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync(Approve(runId, gate.Id), new { comment = "No choice" }, Ct),
            HttpStatusCode.BadRequest,
            ErrorCodes.VariantChoiceInvalid);

        Assert.Equal(GateStatus.Pending, (await VariantGateAsync(app, runId)).Status);
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);
        var run = await RunAsync(app, runId);
        Assert.Equal(("headline", GenericRunStatus.Active), (run.CurrentStage, run.Status));
        Assert.Empty(tagliner.Prompts);
        Assert.DoesNotContain(await ArtifactsAsync(app, runId, "headline"), a => a.Variant is null);
    }

    [Fact]
    public async Task FR349_rejecting_the_variant_select_gate_promotes_nothing_and_fails_the_run()
    {
        var (app, _, tagliner, _) = App(factory, "v1");
        await using var _app = app;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);
        var gate = await AtGateAsync(app, fixture, runId);

        await ProjectApi.ReadAsync<PipelineGateDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"/api/pipeline-runs/{runId}/gates/{gate.Id}/reject", new { comment = "None of these" }, Ct));
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        Assert.Equal(GenericRunStatus.Failed, (await RunAsync(app, runId)).Status);
        Assert.DoesNotContain(await ArtifactsAsync(app, runId, "headline"), a => a.Variant is null);
        Assert.Empty(tagliner.Prompts);
    }

    [Fact]
    public async Task FR349_an_agent_selector_picks_a_variant_which_becomes_the_stages_output()
    {
        var (app, _, _, judge) = App(factory, "v2");
        await using var _app = app;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);
        var gate = await AtGateAsync(app, fixture, runId);
        await ProjectApi.ReadAsync<PipelineGateDto>(await fixture.Owner.Client.PostAsJsonAsync(Approve(runId, gate.Id), new { choice = "v1" }, Ct));

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        Assert.Single(judge.Prompts);
        var taglines = await ArtifactsAsync(app, runId, "tagline");
        var promoted = Assert.Single(taglines, a => a.Variant is null);
        Assert.Equal(taglines.Single(a => a.Variant == "v2").Content, promoted.Content);
        Assert.Contains("Tagline written as variant v2", promoted.Content, StringComparison.Ordinal);

        // The selector stored nothing of its own: only the two variants and the promoted copy.
        Assert.Equal(3, taglines.Count);
        Assert.Empty(await FailedDriverJobsAsync(app, fixture.Project.Id));
    }

    [Fact]
    public async Task FR349_VARIANT_CHOICE_INVALID_an_agent_selector_choosing_outside_v1_to_vn_fails_the_stage()
    {
        var (app, _, _, _) = App(factory, "v3");
        await using var _app = app;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture);
        var gate = await AtGateAsync(app, fixture, runId);
        await ProjectApi.ReadAsync<PipelineGateDto>(await fixture.Owner.Client.PostAsJsonAsync(Approve(runId, gate.Id), new { choice = "v1" }, Ct));

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var failed = Assert.Single(await FailedDriverJobsAsync(app, fixture.Project.Id));
        Assert.StartsWith(ErrorCodes.VariantChoiceInvalid, failed.LastError, StringComparison.Ordinal);
        Assert.Equal("tagline", (await RunAsync(app, runId)).CurrentStage);
        Assert.DoesNotContain(await ArtifactsAsync(app, runId, "tagline"), a => a.Variant is null);
    }
}
