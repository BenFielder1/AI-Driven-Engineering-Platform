using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Runs;
using Tevrin.IntegrationTests.Support;

namespace Tevrin.IntegrationTests.Pipelines;

/// <summary>
/// FR-355, §4.68 (M70): map-reduce in the run driver, end to end on the Fake provider with <c>fixture-qa</c> (a scorer
/// agent per transcript, a summariser over every score) and its handler variants (<c>fixture-qa-handlers</c>,
/// <c>fixture-qa-fail</c>), which keep the larger counts cheap.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed partial class MapDriverTests(TevrinApiFactory factory)
{
    private const int MaxParallel = 8;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary><paramref name="count"/> transcripts; those in <paramref name="failing"/> are marked to fail the handler.</summary>
    private static object Transcripts(int count, params int[] failing) => new
    {
        items = Enumerable.Range(0, count)
            .Select(i => new Dictionary<string, object> { ["id"] = i, ["text"] = $"call transcript number {i}", ["fail"] = failing.Contains(i) })
            .ToArray(),
    };

    private static async Task<Guid> StartAsync(ProjectFixture fixture, string pipelineId, object transcripts)
    {
        var run = await ProjectApi.ReadAsync<PipelineRunDto>(
            await fixture.Contributor.Client.PostAsJsonAsync(
                $"{fixture.Path}/pipeline-runs",
                new { pipelineId, approvalMode = "advisory", inputs = new { transcripts } },
                Ct),
            HttpStatusCode.Created);
        return run.Id;
    }

    private static Task<PipelineRun> RunAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct));

    private static Task<List<StageExecution>> ItemsAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.StageExecutions.AsNoTracking()
            .Where(e => e.PipelineRunId == runId && e.ParentExecutionId != null).OrderBy(e => e.ItemIndex).ToListAsync(Ct));

    private static Task<List<Artifact>> ScoresAsync(WebApplicationFactory<Program> app, Guid runId) =>
        ProjectApi.WithDbAsync(app, db => db.Artifacts.AsNoTracking()
            .Where(a => a.PipelineRunId == runId && a.Type == "scores").ToListAsync(Ct));

    private static Task<List<Job>> DriverJobsAsync(WebApplicationFactory<Program> app, Guid projectId, JobStatus status) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId && j.Type == EngineJobTypes.StageRun && j.Status == status).ToListAsync(Ct));

    /// <summary>
    /// Runs the project's queued <c>stage.run</c> jobs one at a time, oldest first, until none is left, asserting after each
    /// that no more than <see cref="MaxParallel"/> item jobs are queued at once (§4.68). Returns the most seen at once.
    /// </summary>
    private static async Task<int> DriveAsync(WebApplicationFactory<Program> app, Guid projectId, Guid runId)
    {
        var most = 0;
        for (var step = 0; step < 2000; step++)
        {
            var queued = (await FixtureLinearApp.QueuedDriverJobsAsync(app, projectId)).OrderBy(j => j.CreatedAt).ToList();
            if (queued.Count == 0)
            {
                return most;
            }

            var itemIds = (await ItemsAsync(app, runId)).Select(e => e.Id).ToHashSet();
            var inFlight = queued.Count(j => itemIds.Contains(StageRunPayload.Parse(j.Payload)!.StageExecutionId));
            Assert.InRange(inFlight, 0, MaxParallel);
            most = Math.Max(most, inFlight);
            await AgentJobs.RunAsync(app, queued[0]);
        }

        Assert.Fail("The map did not settle.");
        return most;
    }

    [Fact]
    public async Task FR355_200_transcripts_are_scored_one_isolated_agent_run_each_and_reduced_into_the_stage_output()
    {
        var scorer = new RecordingResponder("qa-scorer", "{\"score\": 4}");
        var summariser = new RecordingResponder("qa-summariser", "{\"mean\": 4.0, \"count\": 200}");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), scorer, summariser);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Qa, Transcripts(200));

        var most = await DriveAsync(app, fixture.Project.Id, runId);

        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Completed, "score"), (run.Status, run.CurrentStage));
        Assert.Equal(MaxParallel, most);

        // One item execution per transcript, each with its index, all completed.
        var items = await ItemsAsync(app, runId);
        Assert.Equal(Enumerable.Range(0, 200), items.Select(i => i.ItemIndex!.Value));
        Assert.All(items, i => Assert.Equal(BranchOutcomes.Completed, i.Outcome));

        // One agent run per item (its index recorded), and one artifact collection of 200 item outputs plus the reduce's.
        var runs = await ProjectApi.WithDbAsync(app, db => db.AgentRuns.AsNoTracking().Where(r => r.PipelineRunId == runId).ToListAsync(Ct));
        Assert.Equal(Enumerable.Range(0, 200), runs.Where(r => r.Agent == "qa-scorer").Select(r => r.ItemIndex!.Value).Order());
        Assert.Null(Assert.Single(runs, r => r.Agent == "qa-summariser").ItemIndex);
        var scores = await ScoresAsync(app, runId);
        Assert.Equal(Enumerable.Range(0, 200), scores.Where(a => a.ItemIndex is not null).Select(a => a.ItemIndex!.Value).Order());
        Assert.Contains("\"count\": 200", Assert.Single(scores, a => a.ItemIndex is null).Content, StringComparison.Ordinal);

        // Each scorer prompt held its own transcript only; the summariser got every item output, labelled.
        Assert.Equal(200, scorer.Prompts.Count);
        var prompt17 = Assert.Single(scorer.Prompts, p => p.Contains("Transcript 17:", StringComparison.Ordinal));
        Assert.Contains("call transcript number 17\"", prompt17, StringComparison.Ordinal);
        Assert.DoesNotContain("call transcript number 18\"", prompt17, StringComparison.Ordinal);
        var reduce = Assert.Single(summariser.Prompts);
        Assert.Contains("### item-0\n", reduce, StringComparison.Ordinal);
        Assert.Contains("### item-199\n", reduce, StringComparison.Ordinal);
        Assert.Empty(await DriverJobsAsync(app, fixture.Project.Id, JobStatus.Failed));
    }

    [Fact]
    public async Task FR355_section_4_68_items_start_in_index_order_at_most_max_parallel_at_a_time()
    {
        var log = new QaLog();
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), log);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.QaHandlers, Transcripts(20));

        // The map's own job opens all 20 items and queues the first 8.
        await AgentJobs.RunAsync(app, Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id)));
        var items = await ItemsAsync(app, runId);
        Assert.Equal(Enumerable.Range(0, 20), items.Select(i => i.ItemIndex!.Value));
        Assert.All(items, i => Assert.Null(i.ExitedAt));
        var queued = (await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id))
            .Select(j => items.Single(i => i.Id == StageRunPayload.Parse(j.Payload)!.StageExecutionId).ItemIndex!.Value);
        Assert.Equal(Enumerable.Range(0, MaxParallel), queued.Order());

        Assert.Equal(MaxParallel, await DriveAsync(app, fixture.Project.Id, runId));
        Assert.Equal(Enumerable.Range(0, 20), log.Scored.Order());
        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        Assert.Equal(20, Assert.Single(log.Tallies).Count);
    }

    [Fact]
    public async Task FR355_section_4_68_200_items_at_5_percent_3_failures_pass_and_the_reduce_skips_them()
    {
        var log = new QaLog();
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), log);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.QaHandlers, Transcripts(200, 5, 50, 150));

        await DriveAsync(app, fixture.Project.Id, runId);

        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        var items = await ItemsAsync(app, runId);
        Assert.Equal([5, 50, 150], items.Where(i => i.Outcome == BranchOutcomes.Failed).Select(i => i.ItemIndex!.Value));
        var tally = Assert.Single(log.Tallies);
        Assert.Equal(197, tally.Count);
        Assert.DoesNotContain(tally, t => t.Label == "item-50");
        Assert.Equal("{\"count\": 197}", Assert.Single(await ScoresAsync(app, runId), a => a.ItemIndex is null).Content);
        Assert.Equal(3, (await DriverJobsAsync(app, fixture.Project.Id, JobStatus.Failed)).Count);
    }

    [Fact]
    public async Task FR355_section_4_68_200_items_at_5_percent_11_failures_fail_the_stage_and_no_further_item_starts()
    {
        var log = new QaLog();
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), log);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.QaHandlers, Transcripts(200, [.. Enumerable.Range(0, 11)]));

        await DriveAsync(app, fixture.Project.Id, runId);

        // Items 0–10 fail; the 11th failure (item 10) fails the stage, items 11–17 already running finish, nothing more starts.
        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Active, "score"), (run.Status, run.CurrentStage));
        Assert.Equal(Enumerable.Range(0, 18), log.Scored.Order());
        var items = await ItemsAsync(app, runId);
        Assert.Equal(11, items.Count(i => i.Outcome == BranchOutcomes.Failed));
        Assert.Equal(182, items.Count(i => i.ExitedAt is null));
        Assert.Empty(log.Tallies);
        Assert.Empty(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
    }

    [Fact]
    public async Task FR355_section_4_68_with_on_item_failure_fail_the_first_failure_fails_the_stage_and_a_retry_reruns_it()
    {
        var log = new QaLog();
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), log);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.QaFail, Transcripts(30, 2));

        await DriveAsync(app, fixture.Project.Id, runId);

        // Item 2 fails. The first window's 8 jobs share a creation time, so which of 0–7 run before item 2 varies: each that
        // ends first queues the next item (at most 7 of them, items 8–14). Once the failure is recorded nothing more is queued.
        Assert.Equal(GenericRunStatus.Active, (await RunAsync(app, runId)).Status);
        Assert.Equal([2], (await ItemsAsync(app, runId)).Where(i => i.Outcome == BranchOutcomes.Failed).Select(i => i.ItemIndex!.Value));
        Assert.DoesNotContain(log.Scored, i => i > 14);
        Assert.Empty(log.Tallies);
        var failed = Assert.Single(await DriverJobsAsync(app, fixture.Project.Id, JobStatus.Failed));
        Assert.StartsWith("VALIDATION: Transcript 2 is unreadable.", failed.LastError, StringComparison.Ordinal);
        var queuedAfter = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .CountAsync(j => j.ProjectId == fixture.Project.Id && j.Type == EngineJobTypes.StageRun && j.CreatedAt > failed.CompletedAt, Ct));
        Assert.Equal(0, queuedAfter);

        // FR-333: a retry reopens the failed item (and starts the items never run); it fails again, so the stage stays failed.
        await ProjectApi.ReadAsync<PipelineRunDto>(await fixture.Contributor.Client.PostAsync($"/api/pipeline-runs/{runId}/retry", null, Ct));
        await DriveAsync(app, fixture.Project.Id, runId);
        Assert.Equal(2, log.Scored.Count(i => i == 2));
        Assert.Equal(GenericRunStatus.Active, (await RunAsync(app, runId)).Status);
        Assert.Empty(log.Tallies);
    }

    [Fact]
    public async Task FR355_section_4_68_1001_items_fail_the_stage_with_MAP_TOO_MANY_ITEMS_and_no_item_runs()
    {
        var scorer = new RecordingResponder("qa-scorer", "{\"score\": 4}");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), scorer);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Qa, new { items = Enumerable.Range(0, 1001).ToArray() });

        await DriveAsync(app, fixture.Project.Id, runId);

        var failed = Assert.Single(await DriverJobsAsync(app, fixture.Project.Id, JobStatus.Failed));
        Assert.StartsWith("MAP_TOO_MANY_ITEMS", failed.LastError, StringComparison.Ordinal);
        Assert.Empty(await ItemsAsync(app, runId));
        Assert.Empty(scorer.Prompts);
        Assert.Equal(GenericRunStatus.Active, (await RunAsync(app, runId)).Status);
    }

    [Fact]
    public async Task FR355_a_redelivered_map_or_item_job_creates_no_duplicate_executions_agent_runs_or_artifacts()
    {
        var scorer = new RecordingResponder("qa-scorer", "{\"score\": 4}");
        var summariser = new RecordingResponder("qa-summariser", "{\"mean\": 4.0}");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), scorer, summariser);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.Qa, Transcripts(3));

        // The map's job, delivered twice: the items open once and are queued once.
        var mapJob = Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
        await AgentJobs.RunAsync(app, mapJob);
        await AgentJobs.RunAsync(app, mapJob);
        Assert.Equal(3, (await ItemsAsync(app, runId)).Count);
        var itemJobs = await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id);
        Assert.Equal(3, itemJobs.Count);

        // An item's job, delivered twice: one agent run and one artifact.
        await AgentJobs.RunAsync(app, itemJobs[0]);
        await AgentJobs.RunAsync(app, itemJobs[0]);
        Assert.Single(scorer.Prompts);
        Assert.Single(await ScoresAsync(app, runId));

        await DriveAsync(app, fixture.Project.Id, runId);
        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        Assert.Equal(3, scorer.Prompts.Count);
        Assert.Single(summariser.Prompts);
        var runs = await ProjectApi.WithDbAsync(app, db => db.AgentRuns.AsNoTracking().CountAsync(r => r.PipelineRunId == runId, Ct));
        Assert.Equal(4, runs);
        Assert.Equal(4, (await ScoresAsync(app, runId)).Count);
    }

    [Fact]
    public async Task FR355_zero_items_reduce_over_an_empty_list_and_the_stage_completes()
    {
        var log = new QaLog();
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), log);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.QaHandlers, Transcripts(0));

        await DriveAsync(app, fixture.Project.Id, runId);

        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        Assert.Empty(await ItemsAsync(app, runId));
        Assert.Empty(Assert.Single(log.Tallies));
    }
    [Fact]
    public async Task FR355_an_escalated_item_is_not_rerun_by_its_siblings_and_the_map_waits_for_the_gate_decision()
    {
        var scorer = new QaScorer(zeroFor: 1);
        var summariser = new RecordingResponder("qa-summariser", "{\"mean\": 4.0}");
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), scorer, summariser);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.QaEscalate, Transcripts(4));

        await DriveAsync(app, fixture.Project.Id, runId);

        // Item 1's zero score escalated: its siblings finished, but nobody ran it again and the map didn't reduce.
        Assert.Equal(1, scorer.CallsFor(1));
        Assert.Empty(summariser.Prompts);
        var items = await ItemsAsync(app, runId);
        Assert.Equal([1], items.Where(i => i.ExitedAt is null).Select(i => i.ItemIndex!.Value));
        var run = await RunAsync(app, runId);
        Assert.Equal((GenericRunStatus.Active, "score"), (run.Status, run.CurrentStage));
        var gate = await ProjectApi.WithDbAsync(app, db => db.ApprovalGates.AsNoTracking()
            .SingleAsync(g => g.PipelineRunId == runId && g.Status == GateStatus.Pending, Ct));
        Assert.Equal(EngineGates.Escalation, gate.Type);
        Assert.Empty(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));

        // The decision re-queues the map, which reruns item 1 (now scored 4), then reduces and moves on.
        await ProjectApi.ReadAsync<PipelineGateDto>(
            await fixture.Owner.Client.PostAsJsonAsync($"/api/pipeline-runs/{runId}/gates/{gate.Id}/approve", new { comment = "Rescore it" }, Ct));
        await DriveAsync(app, fixture.Project.Id, runId);

        Assert.Equal(2, scorer.CallsFor(1));
        Assert.Equal(1, scorer.CallsFor(0));
        Assert.Single(summariser.Prompts);
        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
    }

    [Fact]
    public async Task FR355_two_items_ending_at_once_reduce_once_and_queue_the_map_hand_over_once()
    {
        var log = new QaLog();
        await using var app = FixtureFlowApp.Create(factory, new PositionLimit(), log);
        using var fixture = await ProjectApi.FixtureAsync(app);
        var runId = await StartAsync(fixture, FlowFixturePack.QaHandlers, Transcripts(2));
        var mapJob = Assert.Single(await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id));
        await AgentJobs.RunAsync(app, mapJob);
        var mapEntryId = StageRunPayload.Parse(mapJob.Payload)!.StageExecutionId;

        // Both item jobs run at once, each in its own scope: one move wins on the run's xmin and the other retries (the
        // harness records the transient loser as dead; it is delivered again, as the worker would).
        var itemJobs = await FixtureLinearApp.QueuedDriverJobsAsync(app, fixture.Project.Id);
        Assert.Equal(2, itemJobs.Count);
        await Task.WhenAll(itemJobs.Select(j => Task.Run(() => AgentJobs.RunAsync(app, j), Ct)));
        foreach (var dead in await DriverJobsAsync(app, fixture.Project.Id, JobStatus.Dead))
        {
            await AgentJobs.RunAsync(app, dead);
        }

        await DriveAsync(app, fixture.Project.Id, runId);

        Assert.Equal(GenericRunStatus.Completed, (await RunAsync(app, runId)).Status);
        Assert.Equal(2, Assert.Single(log.Tallies).Count);
        Assert.All(await ItemsAsync(app, runId), i => Assert.Equal(BranchOutcomes.Completed, i.Outcome));
        Assert.Equal(2, (await ScoresAsync(app, runId)).Count(a => a.ItemIndex is not null));
        var mapJobs = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == fixture.Project.Id && j.Type == EngineJobTypes.StageRun).ToListAsync(Ct));
        Assert.Equal(2, mapJobs.Count(j => StageRunPayload.Parse(j.Payload)!.StageExecutionId == mapEntryId));
    }
}
