using System.Diagnostics;
using Aide.Api.Agents;
using Aide.Api.Logging;
using Aide.Domain.RepoTools;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.Infrastructure.RepoTools;
using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-318: the job layer of the engine path (<see cref="SdlcAgentStageJob"/>: where it runs, the refusals that
/// replaced 0.6.1's fallback (FR-320), the draft conflict), the repo tools (FR-71: <c>tool_calls</c>, <c>snapshot_unavailable</c>, the budget between tool
/// turns), the reads an attempt makes, and 0.6.1's <c>agent run</c> span.
/// </summary>
public class SdlcAgentStageJobTests
{
    private static SdlcAgentStageCall Spec(SdlcAgentStageHarness harness, JobExecution job) =>
        SdlcAgentStageHarness.SpecCall(job, harness.Data.Project.Id);

    [Fact]
    public async Task FR318_a_found_stage_runs_on_the_engine_and_returns_its_run()
    {
        using var harness = new SdlcAgentStageHarness([null]);
        var job = SdlcAgentStageHarness.Job();

        var runId = await harness.RunJobAsync(Spec(harness, job), new SdlcStageTarget.Found(harness.Run.Id, "specification"));

        Assert.Equal(job.Id, runId);
        Assert.Single(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR320_a_project_still_without_a_pipeline_run_fails_permanently_with_INVALID_STATE_and_no_call()
    {
        using var harness = new SdlcAgentStageHarness([null]);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunJobAsync(Spec(harness, SdlcAgentStageHarness.Job()), new SdlcStageTarget.NoRun()));

        Assert.Equal(("INVALID_STATE", false, SdlcAgentStageJob.NoRunMessage), (ex.ErrorCode, ex.Transient, ex.Message));
        Assert.Empty(harness.Provider.Requests);
        Assert.Empty(harness.Store.AgentRuns);
    }

    [Fact]
    public async Task FR320_a_definition_without_the_agents_stage_fails_permanently_with_DEFINITION_UNAVAILABLE_and_no_call()
    {
        using var harness = new SdlcAgentStageHarness([null]);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() =>
            harness.RunJobAsync(Spec(harness, SdlcAgentStageHarness.Job()), new SdlcStageTarget.NoStage(harness.Run.Id)));

        Assert.Equal(("DEFINITION_UNAVAILABLE", false), (ex.ErrorCode, ex.Transient));
        Assert.Empty(harness.Provider.Requests);
        Assert.Empty(harness.Store.AgentRuns);
    }

    [Fact]
    public async Task FR318_FR277_an_unregistered_pinned_definition_fails_permanently_with_DEFINITION_UNAVAILABLE()
    {
        using var harness = new SdlcAgentStageHarness([null]);

        var ex = await Assert.ThrowsAsync<JobFailedException>(() =>
            harness.RunJobAsync(Spec(harness, SdlcAgentStageHarness.Job()), new SdlcStageTarget.Unavailable(harness.Run.Id, "sha256:unknown", "tool:missing.tool")));

        Assert.Equal(("DEFINITION_UNAVAILABLE", false), (ex.ErrorCode, ex.Transient));
        Assert.Empty(harness.Store.AgentRuns);
        Assert.Empty(harness.Provider.Requests);
    }

    [Fact]
    public async Task FR318_FR277_a_run_pinned_to_a_stored_only_runnable_definition_runs_on_the_engine()
    {
        using var harness = new SdlcAgentStageHarness([null]);
        Assert.True(harness.Services.GetRequiredService<PipelineRegistry>().TryGet(SdlcPack.PipelineId, out var loaded));
        const string hash = "sha256:stored-only-sdlc";
        harness.Services.GetRequiredService<PipelineDefinitionCache>().Add(hash, new StoredPipelineDefinition(loaded.PackId, loaded.Definition! with { Version = 99 }));
        var run = harness.Run;
        harness.Store.Runs.Remove(run);
        harness.Store.Runs.Add(new PipelineRun
        {
            Id = run.Id,
            ProjectId = run.ProjectId,
            PipelineId = run.PipelineId,
            DefinitionHash = hash,
            SubjectId = run.SubjectId,
            CurrentStage = run.CurrentStage,
            PackStatus = run.PackStatus,
        });
        var job = SdlcAgentStageHarness.Job();

        var runId = await harness.RunJobAsync(Spec(harness, job), new SdlcStageTarget.Found(run.Id, "specification"));

        Assert.Equal(job.Id, runId);
        Assert.Single(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR318_a_draft_that_collides_with_a_concurrent_change_is_a_transient_CONFLICT_and_the_retry_makes_no_call()
    {
        using var harness = new SdlcAgentStageHarness([null]);
        harness.Data.ConflictOnDraft = true;
        var job = SdlcAgentStageHarness.Job();
        var target = new SdlcStageTarget.Found(harness.Run.Id, "specification");

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunJobAsync(Spec(harness, job), target));

        Assert.Equal(("CONFLICT", true), (ex.ErrorCode, ex.Transient));
        Assert.Equal("The technical_specification draft collided with a concurrent change: DbUpdateException.", ex.Message);
        harness.Data.ConflictOnDraft = false;
        harness.Store.Artifacts.Clear();
        await harness.RunJobAsync(Spec(harness, job with { Attempt = 2 }), target);
        Assert.Single(harness.Provider.Requests);
        Assert.Single(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR318_FR71_the_repo_tools_calls_are_the_runs_tool_calls()
    {
        var root = Directory.CreateTempSubdirectory("tevrin-repo-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "README.md"), "# Demo", TestContext.Current.CancellationToken);
            var script = new FakeToolScript(SdlcAgents.SpecGenerator, [new FakeToolCall(RepoToolNames.ListFiles, "{}")]);
            using var harness = new SdlcAgentStageHarness(SdlcAgents.SpecGenerator, [script], [null]);
            var session = new RepoToolSession(root.FullName);
            harness.Repo.Open = () => RepoToolContext.ForSession(session);

            await harness.RunAsync(SdlcAgentStageHarness.Job());

            var run = Assert.Single(harness.Store.AgentRuns);
            Assert.Equal(session.ToJson(), run.ToolCalls);
            Assert.Contains(RepoToolNames.ListFiles, run.ToolCalls, StringComparison.Ordinal);
            Assert.Equal(1, harness.Repo.Opened);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FR318_FR71_a_missing_snapshot_is_the_snapshot_unavailable_warning_of_the_run()
    {
        using var harness = new SdlcAgentStageHarness([null]);
        harness.Repo.Open = () => new RepoToolContext(null, AgentRunWarnings.SnapshotUnavailable);

        await harness.RunAsync(SdlcAgentStageHarness.Job());

        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal([AgentRunWarnings.SnapshotUnavailable], SdlcAgentRuns.ReadOutput(run.Output)!.Warnings);
        Assert.Equal("[]", run.ToolCalls);
        Assert.Contains("\"warnings\":[\"snapshot_unavailable\"]", harness.Data.Events.Single(e => e.Type == "RUN_COMPLETED").Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR318_FR26_a_budget_used_up_between_tool_turns_fails_the_run_with_BUDGET_EXCEEDED()
    {
        var root = Directory.CreateTempSubdirectory("tevrin-repo-");
        try
        {
            var script = new FakeToolScript(SdlcAgents.SpecGenerator, [new FakeToolCall(RepoToolNames.ListFiles, "{}")]);
            using var harness = new SdlcAgentStageHarness(SdlcAgents.SpecGenerator, [script], [null]);
            harness.Repo.Open = () => RepoToolContext.ForSession(new RepoToolSession(root.FullName));
            harness.Data.Project.TokenBudget = 1;
            var job = SdlcAgentStageHarness.Job();

            var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunAsync(job));

            Assert.Equal(("BUDGET_EXCEEDED", false), (ex.ErrorCode, ex.Transient));
            var run = Assert.Single(harness.Store.AgentRuns);
            var used = (long)run.InputTokens + run.OutputTokens;
            Assert.Equal((AgentRunStatus.Failed, $"BUDGET_EXCEEDED: {ex.Message}"), (run.Status, run.Error));
            Assert.Equal(["RUN_STARTED", "BUDGET_EXCEEDED", "RUN_FAILED"], harness.Data.Events.Select(e => e.Type));
            Assert.Equal(
                AideJson.Serialize(new { agent = SdlcAgents.SpecGenerator, tokenBudget = (long?)1, tokensUsed = used, jobId = job.Id, runId = job.Id }),
                harness.Data.Events[1].Payload);
            Assert.Equal(used, harness.Data.Project.TokensUsed);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FR318_an_attempt_reads_the_project_twice_and_no_context_the_host_supplies()
    {
        using var harness = new SdlcAgentStageHarness([null]);

        await harness.RunAsync(SdlcAgentStageHarness.Job());

        // 0.6.1: once at the run's start, once before the call (the tool-turn budget).
        Assert.Equal((2, 0), (harness.Data.ProjectReads, harness.Context.Reads));
    }

    [Fact]
    public async Task FR318_NFR_OBS_each_attempt_has_0_6_1s_agent_run_span_and_a_rejected_final_attempt_is_an_error()
    {
        using var harness = new SdlcAgentStageHarness([SdlcSpecGeneratorStageTests.Invalid]);
        // Other tests stop spans on other threads at the same time.
        var spans = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AideTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var job = SdlcAgentStageHarness.Job();

        await Assert.ThrowsAsync<JobFailedException>(() => harness.RunAsync(job));

        var corrective = AgentRunRules.RunId(job.Id, Domain.Agents.AgentOutput.CorrectiveRunKey);
        var ours = spans.Where(s => s.OperationName == "agent run" && Equals(s.GetTagItem(AideTelemetry.ProjectIdTag), harness.Data.Project.Id.ToString())).ToList();
        Assert.Equal([job.Id.ToString(), corrective.ToString()], ours.Select(s => s.GetTagItem(AideTelemetry.RunIdTag)));
        Assert.Equal((ActivityStatusCode.Ok, "succeeded"), (ours[0].Status, ours[0].GetTagItem(AideTelemetry.RunStatusTag)));
        Assert.Equal((ActivityStatusCode.Error, "MISSING_SECTIONS", "failed"), (ours[1].Status, ours[1].StatusDescription, ours[1].GetTagItem(AideTelemetry.RunStatusTag)));
        Assert.Equal("SpecGenerator", ours[1].GetTagItem(AideTelemetry.AgentTag));
    }
}
