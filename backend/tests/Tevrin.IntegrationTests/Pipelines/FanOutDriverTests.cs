using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Runs;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-345, FR-346, §4.65 (M68): fan-out with isolated context and aggregation in the run driver, end to end on the Fake
/// provider, with <c>fixture-forecast</c> (three forecasters fan out, then the aggregate and its spread guard) and
/// <c>fixture-forecast-leak</c> (a context provider leaks the base-rate forecast to the contrarian).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class FanOutDriverTests(TevrinApiFactory factory)
{
    private static readonly string[] Labels = ["forecaster-base-rate", "forecaster-trend", "forecaster-contrarian"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (WebApplicationFactory<Program> App, RecordingResponder[] Forecasters, RecordingResponder Aggregator) App(
        TevrinApiFactory factory, double baseRate, double trend, double contrarian)
    {
        RecordingResponder[] forecasters =
        [
            new(Labels[0], Forecast(baseRate)),
            new(Labels[1], Forecast(trend)),
            new(Labels[2], Forecast(contrarian)),
        ];
        var aggregator = new RecordingResponder("aggregator", "The consensus is a coin flip.");
        return (FixtureFlowApp.Create(factory, new PositionLimit(), [.. forecasters, aggregator]), forecasters, aggregator);
    }

    private static string Forecast(double probability) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"probability\": {probability:0.00}, \"basis\": \"the fixture forecaster's reading\"}}");

    private static async Task<Guid> StartAsync(ProjectFixture fixture, string pipelineId)
    {
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs",
                new { pipelineId, approvalMode = "advisory", inputs = new { question = "Will it rain in Leeds tomorrow?" } },
                Ct),
            HttpStatusCode.Created);
        return run.Id;
    }

    private static Task<PipelineRun> RunAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct));

    private static Task<List<StageExecution>> ExecutionsAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.StageExecutions.AsNoTracking()
            .Where(e => e.PipelineRunId == runId).OrderBy(e => e.EnteredAt).ThenBy(e => e.Branch).ToListAsync(Ct));

    private static Task<List<Artifact>> ForecastsAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking()
            .Where(a => a.PipelineRunId == runId && a.Type == "forecast").ToListAsync(Ct));

    private static Task<List<Job>> FailedDriverJobsAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == EngineJobTypes.StageRun && j.Status == JobStatus.Failed).ToListAsync(Ct));

    [Fact]
    public async Task FR345_FR346_three_forecasts_are_stored_as_variants_the_aggregate_reads_all_three_and_a_disagreement_flags_it()
    {
        var (app, forecasters, aggregator) = App(factory, 0.20, 0.50, 0.70);
        await using var _ = app;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Forecast);

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Completed, "aggregate"), (run.Status, run.CurrentStage));

        // FR-345: one variant per forecaster, each the stage's own artifact.
        var forecasts = await ForecastsAsync(app, runId);
        Assert.Equal(Labels.Order(StringComparer.Ordinal), forecasts.Select(f => f.Variant!).Order(StringComparer.Ordinal));
        Assert.All(forecasts, f => Assert.Equal((1, ArtifactStatus.Locked), (f.Version, f.Status)));

        // §4.65: each forecaster saw the question and none of its siblings' forecasts.
        for (var i = 0; i < forecasters.Length; i++)
        {
            var prompt = Assert.Single(forecasters[i].Prompts);
            Assert.Contains("Will it rain in Leeds tomorrow?", prompt, StringComparison.Ordinal);
            foreach (var sibling in forecasts.Where(f => f.Variant != Labels[i]))
            {
                Assert.DoesNotContain(sibling.Content.Trim(), prompt, StringComparison.Ordinal);
            }
        }

        // FR-346: the aggregator got every forecast, labelled with its variant.
        var aggregatePrompt = Assert.Single(aggregator.Prompts);
        foreach (var forecast in forecasts)
        {
            Assert.Contains("### " + forecast.Variant, aggregatePrompt, StringComparison.Ordinal);
            Assert.Contains(forecast.Content.Trim(), aggregatePrompt, StringComparison.Ordinal);
        }

        // The spread (0.50) flags the aggregate without failing it: the run completed.
        var executions = await ExecutionsAsync(app, runId);
        var branches = executions.Where(e => e.StageId == "forecast" && e.Branch is not null).ToList();
        Assert.Equal(3, branches.Count);
        Assert.All(branches, b => Assert.Equal((b.Branch, BranchOutcomes.Completed), (b.Variant, b.Outcome)));
        Assert.Equal(PipelineTransitionKinds.Advance, executions.Single(e => e.StageId == "forecast" && e.Branch is null).Outcome);
        var aggregate = executions.Single(e => e.StageId == "aggregate");
        Assert.Equal("completed", aggregate.Outcome);
        Assert.Equal(
            ["flow.forecast-spread: The forecasts spread 0.50, over 0.25."],
            JsonSerializer.Deserialize<string[]>(aggregate.VetoFindings!)!);
        Assert.Empty(await FailedDriverJobsAsync(app, fixture.Project.Id));
    }

    [Fact]
    public async Task FR346_forecasts_that_agree_leave_the_aggregate_unflagged()
    {
        var (app, _, _) = App(factory, 0.40, 0.45, 0.50);
        await using var _app = app;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Forecast);

        await FixtureLinearApp.DriveAsync(app, fixture.Project.Id);

        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        Assert.Null((await ExecutionsAsync(app, runId)).Single(e => e.StageId == "aggregate").VetoFindings);
    }

    [Fact]
    public async Task FR345_section_4_65_a_branch_given_a_siblings_output_fails_with_FANOUT_ISOLATION_BROKEN()
    {
        var (app, forecasters, aggregator) = App(factory, 0.20, 0.50, 0.70);
        await using var _ = app;
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.ForecastLeak);

        // The fan-out's own job opens one execution (and one job) per branch.
        await AgentJobs.RunAsync(app, Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id)));
        var executions = await ExecutionsAsync(app, runId);
        var branchJobs = (await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id))
            .ToDictionary(j => executions.Single(e => e.Id == StageRunPayload.Parse(j.Payload)!.StageExecutionId).Branch!);
        Assert.Equal(Labels.Order(StringComparer.Ordinal), branchJobs.Keys.Order(StringComparer.Ordinal));

        // The base rate stores its forecast; the scripted context then hands it to the contrarian, which is refused.
        await AgentJobs.RunAsync(app, branchJobs[Labels[0]]);
        await AgentJobs.RunAsync(app, branchJobs[Labels[2]]);

        var failed = Assert.Single(await FailedDriverJobsAsync(app, fixture.Project.Id));
        Assert.StartsWith("FANOUT_ISOLATION_BROKEN", failed.LastError, StringComparison.Ordinal);
        Assert.Empty(forecasters[2].Prompts);
        Assert.Equal([Labels[0]], (await ForecastsAsync(app, runId)).Select(f => f.Variant!));

        // The failed branch fails the stage: the run waits in the fan-out (for a retry) and the aggregate never runs.
        await AgentJobs.RunAsync(app, branchJobs[Labels[1]]);
        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Active, "forecast"), (run.Status, run.CurrentStage));
        Assert.Empty(aggregator.Prompts);
    }
}
