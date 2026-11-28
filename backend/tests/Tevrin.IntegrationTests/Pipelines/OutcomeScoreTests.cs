using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Common;
using Tevrin.Engine.Api;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-353 (M69d): external outcome scores on a <c>fixture-linear</c> run's agent runs and artifacts, listed per run and
/// target, and the prompt comparison's mean score per metric.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class OutcomeScoreTests(TevrinApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Observed = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static object Brier(object target, double prediction, double value, string metric = "won") =>
        new { target, metric, kind = "probability_brier", value, prediction, observedAt = Observed, source = "crm" };

    /// <summary>Starts a <c>fixture-linear</c> run in the fixture's project and drives it to its gate (two agent runs).</summary>
    private static async Task<(PipelineRunDto Run, List<AgentRun> AgentRuns)> RunToGateAsync(WebApplicationFactory<Program> app, ProjectFixture fixture)
    {
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs",
                new { pipelineId = FixtureLinearPack.PipelineId, approvalMode = "advisory", inputs = new { brief = new { topic = "Tides" } } },
                Ct),
            HttpStatusCode.Created);
        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);
        var agentRuns = await ProjectApi.WithDbAsync(app, db => db.AgentRuns.AsNoTracking()
            .Where(r => r.ProjectId == fixture.Project.Id && r.PipelineRunId == run.Id).OrderBy(r => r.CreatedAt).ToListAsync(Ct));
        Assert.NotEmpty(agentRuns);
        return (run, agentRuns);
    }

    [Fact]
    public async Task FR353_a_brier_score_on_an_agent_run_is_stored_listed_and_averaged_in_the_prompt_comparison()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var (run, agentRuns) = await RunToGateAsync(app, fixture);
        var scored = agentRuns[0];
        var outcomes = $"/api/pipeline-runs/{run.Id}/outcomes";

        var created = await ProjectApi.ReadAsync<OutcomeScoreDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(outcomes, Brier(new { agentRunId = scored.Id }, 0.7, 1), Ct), HttpStatusCode.Created);
        Assert.Equal((run.Id, scored.Id, "won", OutcomeScoreKind.ProbabilityBrier), (created.PipelineRunId, created.Target.AgentRunId, created.Metric, created.Kind));
        Assert.Equal(0.09, created.Score, 10);
        Assert.Equal((0.7, 1d, "crm", Observed), (created.Prediction, created.Value, created.Source, created.ObservedAt));
        Assert.Equal(fixture.Contributor.User!.Id, created.CreatedBy);

        // A second target of the same agent key and prompt version, through an artifact it produced, when there is one.
        var produced = await ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == fixture.Project.Id && a.PipelineRunId == run.Id && a.AgentRunId != null)
            .OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(Ct));
        if (produced is not null)
        {
            var onArtifact = await ProjectApi.ReadAsync<OutcomeScoreDto>(
                await fixture.Contributor.Client.PostAsJsonAsync(
                    outcomes, Brier(new { artifactKey = produced.Type, version = produced.Version, variant = produced.Variant }, 0.2, 1), Ct),
                HttpStatusCode.Created);
            Assert.Equal((produced.Type, (int?)produced.Version, (Guid?)null), (onArtifact.Target.ArtifactKey, onArtifact.Target.Version, onArtifact.Target.AgentRunId));
            Assert.Equal(0.64, onArtifact.Score, 10);

            var byArtifact = await ProjectApi.ReadAsync<List<OutcomeScoreDto>>(
                await fixture.Viewer.Client.GetAsync($"{outcomes}?artifactKey={produced.Type}&version={produced.Version}", Ct));
            Assert.Equal([onArtifact.Id], byArtifact.Select(s => s.Id));
        }

        var all = await ProjectApi.ReadAsync<List<OutcomeScoreDto>>(await fixture.Viewer.Client.GetAsync(outcomes, Ct));
        Assert.Equal(produced is null ? 1 : 2, all.Count);
        Assert.Equal(created.Id, all[0].Id);
        var byRun = await ProjectApi.ReadAsync<List<OutcomeScoreDto>>(await fixture.Viewer.Client.GetAsync($"{outcomes}?agentRunId={scored.Id}", Ct));
        Assert.Equal([created.Id], byRun.Select(s => s.Id));

        // The prompt comparison: a plain mean over the scored targets of each agent key and prompt version.
        var stats = await ProjectApi.ReadAsync<List<PromptStatsDto>>(
            await fixture.Viewer.Client.GetAsync($"/api/pipelines/{FixtureLinearPack.PipelineId}/prompt-stats?projectId={fixture.Project.Id}", Ct));
        var row = stats.Single(s => s.AgentKey == scored.Agent && s.PromptVersion == scored.PromptVersion);
        var won = Assert.Single(row.Outcomes);
        Assert.Equal("won", won.Metric);
        var producer = produced is null ? null : agentRuns.Single(r => r.Id == produced.AgentRunId);
        if (producer is not null && producer.Agent == scored.Agent && producer.PromptVersion == scored.PromptVersion)
        {
            Assert.Equal((2, (0.09 + 0.64) / 2), (won.Count, Math.Round(won.MeanScore, 10)));
        }
        else
        {
            Assert.Equal((1, 0.09), (won.Count, Math.Round(won.MeanScore, 10)));
        }

        Assert.All(stats.Where(s => s.AgentKey != scored.Agent && (producer is null || s.AgentKey != producer.Agent)), s => Assert.Empty(s.Outcomes));
    }

    [Fact]
    public async Task FR353_a_target_from_another_run_is_NOT_FOUND()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        using var other = await ProjectApi.FixtureAsync(app);
        var (run, _) = await RunToGateAsync(app, fixture);
        var (_, otherAgentRuns) = await RunToGateAsync(app, other);

        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync($"/api/pipeline-runs/{run.Id}/outcomes", Brier(new { agentRunId = otherAgentRuns[0].Id }, 0.5, 1), Ct),
            HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync($"/api/pipeline-runs/{run.Id}/outcomes", Brier(new { artifactKey = "no-such-artifact", version = 1 }, 0.5, 1), Ct),
            HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal(0, await ProjectApi.WithDbAsync(app, db => db.OutcomeScores.AsNoTracking().CountAsync(s => s.PipelineRunId == run.Id, Ct)));
    }

    [Fact]
    public async Task FR353_a_prediction_out_of_range_is_VALIDATION_and_a_viewer_may_list_but_not_score()
    {
        await using var app = FixtureLinearApp.Create(factory);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var (run, agentRuns) = await RunToGateAsync(app, fixture);
        var outcomes = $"/api/pipeline-runs/{run.Id}/outcomes";

        await Problems.AssertAsync(
            await fixture.Contributor.Client.PostAsJsonAsync(outcomes, Brier(new { agentRunId = agentRuns[0].Id }, 1.5, 1), Ct),
            HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(
            await fixture.Viewer.Client.PostAsJsonAsync(outcomes, Brier(new { agentRunId = agentRuns[0].Id }, 0.5, 1), Ct),
            HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Outsider.Client.GetAsync(outcomes, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Empty(await ProjectApi.ReadAsync<List<OutcomeScoreDto>>(await fixture.Viewer.Client.GetAsync(outcomes, Ct)));
    }
}
