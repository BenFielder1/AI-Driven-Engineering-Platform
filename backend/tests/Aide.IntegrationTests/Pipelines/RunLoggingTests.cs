using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Characterisation;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.IntegrationTests.Pipelines;

/// <summary>
/// FR-297, FR-298 (D-0.7.0-32): run logging: stage executions written with the transitions, the agent run's recorded input,
/// run totals and the prompt comparison over the run log.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RunLoggingTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SdlcPipeline => SdlcRunDefinition.Current(factory.Services.GetRequiredService<PipelineRegistryHost>()).Definition!.Pipeline;

    [Fact]
    public async Task FR297_a_review_cycle_records_execution_and_review_twice_with_entered_and_exited_times()
    {
        // The default Fake review: cycle 1 needs another cycle (a one-step optimisation plan), cycle 2 is release-eligible.
        using var flow = await SdlcFlow.CreateAsync(factory, factory, "advisory");
        await flow.ToReviewAsync();
        await flow.ApproveDraftAsync(SdlcArtifacts.OptimizationPlan);
        await flow.RunJobsAsync();
        await flow.ExpectPhaseAsync(Phase.Review);
        await flow.ReleaseAsync();

        var run = await ProjectApi.WithDbAsync(factory, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.ProjectId == flow.Id, Ct));
        var rows = await ProjectApi.WithDbAsync(factory, db => db.StageExecutions.AsNoTracking()
            .Where(e => e.PipelineRunId == run.Id)
            .OrderBy(e => e.EnteredAt)
            .ToListAsync(Ct));

        Assert.Equal(
            [("execution", 1), ("review", 1), ("execution", 2), ("review", 2)],
            rows.Where(r => r.StageId is "execution" or "review").Select(r => (r.StageId, r.Iteration)));
        Assert.All(rows, r =>
        {
            Assert.NotNull(r.ExitedAt);
            Assert.True(r.ExitedAt >= r.EnteredAt);
            Assert.NotNull(r.Outcome);
            Assert.True(r.Checkpoint >= 1);
        });
        Assert.Equal("loop", rows.Single(r => r.StageId == "review" && r.Iteration == 1).Outcome);
        Assert.Equal(("refinement", 1), (rows[0].StageId, rows[0].Iteration));
        Assert.Equal(("release", "completed"), (rows[^1].StageId, rows[^1].Outcome));

        // Each entry follows the previous exit, and the review entries match the review cycle (2 at the end).
        for (var i = 1; i < rows.Count; i++)
        {
            Assert.Equal(rows[i - 1].ExitedAt, rows[i].EnteredAt);
        }

        Assert.Equal(2, run.Iterations["review"]);
    }

    [Fact]
    public async Task FR297_an_agent_run_stores_its_rendered_input_and_no_latency_when_not_streamed()
    {
        using var flow = await SdlcFlow.CreateAsync(factory, factory, "advisory");
        await flow.ToSpecificationAsync();

        var runs = await ProjectApi.WithDbAsync(factory, db => db.AgentRuns.AsNoTracking().Where(r => r.ProjectId == flow.Id).ToListAsync(Ct));

        Assert.NotEmpty(runs);
        Assert.All(runs, r =>
        {
            Assert.StartsWith("[system]\n", r.Input, StringComparison.Ordinal);
            Assert.Contains("\n\n[user]\n", r.Input, StringComparison.Ordinal);
            Assert.False(r.InputTruncated);

            // The Fake provider doesn't stream.
            Assert.Null(r.LatencyMs);
        });
        Assert.Contains(runs, r => r.Agent == SdlcAgents.IdeaRefinement && r.Input!.Contains(SdlcFlow.Idea, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR297_the_run_totals_equal_the_sum_of_its_agent_runs()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var run = await SingleRunAsync(id);
        await SeedRunsAsync(id, run.Id);

        var totals = await ProjectApi.WithDbAsync(factory, db => new RunLogQueries(db).RunTotalsAsync(run.Id, Ct));
        var expected = await ProjectApi.WithDbAsync(factory, db => db.AgentRuns.AsNoTracking().Where(r => r.PipelineRunId == run.Id).ToListAsync(Ct));

        Assert.Equal(expected.Count, totals.AgentRuns);
        Assert.Equal(expected.Sum(r => r.CostEstimate), totals.Cost);
        Assert.Equal(expected.Sum(r => (long)r.InputTokens), totals.InputTokens);
        Assert.Equal(expected.Sum(r => (long)r.OutputTokens), totals.OutputTokens);
        Assert.Equal(RunTotals.None, await ProjectApi.WithDbAsync(factory, db => new RunLogQueries(db).RunTotalsAsync(Guid.CreateVersion7(), Ct)));
    }

    [Fact]
    public async Task FR298_prompt_stats_group_a_pipelines_agent_runs_by_agent_and_prompt_version()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var run = await SingleRunAsync(id);
        await SeedRunsAsync(id, run.Id);

        var all = await ProjectApi.WithDbAsync(factory, db => new RunLogQueries(db).PromptStatsAsync(SdlcPipeline, [id], Ct));

        // Sorted by agent key, then prompt version; only the seeded spec_generator runs are checked.
        Assert.Equal(all.OrderBy(s => s.Agent, StringComparer.Ordinal).ThenBy(s => s.PromptVersion, StringComparer.Ordinal), all);
        var stats = all.Where(s => s.Agent == SdlcAgents.SpecGenerator).ToList();
        Assert.Equal(["1", "2"], stats.Select(s => s.PromptVersion));
        var v1 = stats[0];
        Assert.Equal((2, 150d, 30d, 0.015m, (double?)400d, 1500d, 0.5d), (v1.Runs, v1.AvgInputTokens, v1.AvgOutputTokens, v1.AvgCost, v1.AvgLatencyMs, v1.AvgDurationMs, v1.FailureRate));
        var v2 = stats[1];
        Assert.Equal((1, 300d, 60d, 0.03m, (double?)null, 900d, 0d), (v2.Runs, v2.AvgInputTokens, v2.AvgOutputTokens, v2.AvgCost, v2.AvgLatencyMs, v2.AvgDurationMs, v2.FailureRate));

        // Another project's runs are left out when the caller passes only the projects it may see.
        Assert.Empty(await ProjectApi.WithDbAsync(factory, db => new RunLogQueries(db).PromptStatsAsync(SdlcPipeline, [Guid.CreateVersion7()], Ct)));
    }

    [Fact]
    public async Task FR297_a_new_release_version_closes_the_old_runs_open_execution()
    {
        await using var app = ReleaseEligible();
        using var flow = await SdlcFlow.CreateAsync(factory, app, "advisory");
        await flow.ToReviewAsync();
        await flow.ReleaseAsync();
        var old = await ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.ProjectId == flow.Id, Ct));

        // An execution left open on the ended run (e.g. a close missed by an overlapping 0.6.1 replica).
        await ProjectApi.WithDbAsync(app, db => db.Database.ExecuteSqlAsync(
            $"UPDATE stage_executions SET exited_at = NULL, outcome = NULL WHERE pipeline_run_id = {old.Id} AND stage_id = 'release'", Ct));

        await flow.StartVersionAsync("0.2.0", "Add CSV export of todos.");

        var oldRows = await ExecutionsAsync(app, old.Id);
        Assert.All(oldRows, r => Assert.NotNull(r.ExitedAt));
        Assert.Equal(("release", "completed"), (oldRows[^1].StageId, oldRows[^1].Outcome));
        var current = await ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.ProjectId == flow.Id && r.Id != old.Id, Ct));
        var opened = Assert.Single(await ExecutionsAsync(app, current.Id));
        Assert.Equal(("refinement", 1, (DateTimeOffset?)null), (opened.StageId, opened.Iteration, opened.ExitedAt));
    }

    [Fact]
    public async Task FR297_a_rollback_re_entering_a_stage_opens_a_new_row_with_the_next_iteration()
    {
        await using var app = ReleaseEligible();
        using var flow = await SdlcFlow.CreateAsync(factory, app, "advisory");
        await flow.ToReviewAsync();
        await flow.PendingGateAsync(SdlcGates.PreRelease);
        var run = await ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.ProjectId == flow.Id, Ct));
        var specification = (await ExecutionsAsync(app, run.Id)).Single(r => r.StageId == "specification");

        // FR-44: from review back to specification.
        await flow.RollbackAsync(specification.Checkpoint, "Back to the specification.");

        var rows = await ExecutionsAsync(app, run.Id);
        Assert.Equal(PipelineTransitionKinds.Rollback, rows.Single(r => r.StageId == "review").Outcome);
        var reentered = rows[^1];
        Assert.Equal(("specification", 2, (DateTimeOffset?)null), (reentered.StageId, reentered.Iteration, reentered.ExitedAt));
        Assert.Single(rows, r => r.ExitedAt is null);
    }

    [Fact]
    public async Task FR297_a_run_without_an_open_row_gets_one_on_its_next_transition()
    {
        using var flow = await SdlcFlow.CreateAsync(factory, factory, "advisory");
        var run = await SingleRunAsync(flow.Id);

        // A run started before 0.7.0 has no rows.
        await ProjectApi.WithDbAsync(factory, db => db.Database.ExecuteSqlAsync($"DELETE FROM stage_executions WHERE pipeline_run_id = {run.Id}", Ct));

        await flow.ToSpecificationAsync();

        var opened = Assert.Single(await ExecutionsAsync(factory, run.Id));
        Assert.Equal(("specification", 1, (DateTimeOffset?)null), (opened.StageId, opened.Iteration, opened.ExitedAt));
    }

    [Fact]
    public async Task FR297_an_sk_ant_key_in_the_prompt_is_stored_redacted_in_the_agent_runs_input()
    {
        const string key = "sk-ant-api03-Zq9LeakedKey0123456789";
        using var flow = await SdlcFlow.CreateAsync(factory, factory, new Dictionary<string, object?>
        {
            ["name"] = SdlcFlow.ProjectName,
            ["idea"] = $"Build a todo API. Our Anthropic key is {key}.",
            ["approvalMode"] = "advisory",
            ["git"] = new { provider = "local" },
        });
        await flow.RefineAsync();

        var run = await ProjectApi.WithDbAsync(factory, db => db.AgentRuns.AsNoTracking()
            .FirstAsync(r => r.ProjectId == flow.Id && r.Agent == SdlcAgents.IdeaRefinement, Ct));

        Assert.NotNull(run.Input);
        Assert.Contains("Build a todo API. Our Anthropic key is", run.Input, StringComparison.Ordinal);
        Assert.DoesNotContain(key, run.Input, StringComparison.Ordinal);
        Assert.Contains(SecretRedaction.Mask, run.Input, StringComparison.Ordinal);
    }

    /// <summary>The Fake review scores the first cycle release-eligible.</summary>
    private WebApplicationFactory<Program> ReleaseEligible() =>
        AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));

    private static Task<List<StageExecution>> ExecutionsAsync(WebApplicationFactory<Program> app, Guid pipelineRunId) =>
        ProjectApi.WithDbAsync(app, db => db.StageExecutions.AsNoTracking()
            .Where(e => e.PipelineRunId == pipelineRunId)
            .OrderBy(e => e.EnteredAt)
            .ToListAsync(Ct));

    private Task<PipelineRun> SingleRunAsync(Guid projectId) =>
        ProjectApi.WithDbAsync(factory, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.ProjectId == projectId, Ct));

    /// <summary>spec_generator v1: two runs (one failed, one streamed at 400 ms); v2: one run; idea_refinement v1: one run.</summary>
    private Task SeedRunsAsync(Guid projectId, Guid pipelineRunId) =>
        ProjectApi.WithDbAsync(factory, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            AgentRun Run(string agent, string version, AgentRunStatus status, int input, int output, decimal cost, long? latency, long duration) => new()
            {
                ProjectId = projectId,
                PipelineRunId = pipelineRunId,
                Agent = agent,
                Stage = Phase.Specification.ToWire(),
                Provider = LlmProvider.Fake,
                Model = "fake",
                PromptVersion = version,
                Status = status,
                InputTokens = input,
                OutputTokens = output,
                CostEstimate = cost,
                LatencyMs = latency,
                DurationMs = duration,
                CreatedAt = now,
            };

            db.AgentRuns.AddRange(
                Run(SdlcAgents.SpecGenerator, "1", AgentRunStatus.Succeeded, 200, 40, 0.02m, 400, 2000),
                Run(SdlcAgents.SpecGenerator, "1", AgentRunStatus.Failed, 100, 20, 0.01m, null, 1000),
                Run(SdlcAgents.SpecGenerator, "2", AgentRunStatus.Succeeded, 300, 60, 0.03m, null, 900),
                Run(SdlcAgents.IdeaRefinement, "1", AgentRunStatus.Succeeded, 50, 10, 0.005m, null, 300));
            await db.SaveChangesAsync(Ct);
        });
}
