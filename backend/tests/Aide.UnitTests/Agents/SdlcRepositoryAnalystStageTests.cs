using Aide.Api.Agents;
using Aide.Domain.Agents;
using Aide.Domain.Stack;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence.Conversion;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-318, FR-319 (FR-70), D-0.7.0-42: <c>repository_analyst</c>, bound on <c>refinement</c>, on the engine runner records
/// what 0.6.1's <c>RepositoryAnalysisJobHandler</c> got from <see cref="AgentRunner"/>: the prompt from the payload's
/// version (<c>VERSION_REQUEST</c>, <c>BASE</c>), the run recorded under the project's current phase, the events with
/// 0.6.1's payload literals, the one corrective re-run with the missing sections, <c>MISSING_SECTIONS</c>, and redelivery.
/// The run stores nothing: the job stores the summary locked afterwards, as 0.6.1.
/// </summary>
public class SdlcRepositoryAnalystStageTests
{
    private const string Missing = "## Overview\nOnly an overview.";

    private const string BaseText = "v0.1.0 (0123456789abcdef0123456789abcdef01234567)";

    /// <summary>The analyst's request and spec, as <c>RepositoryAnalysisJobHandler</c> builds them (no stack).</summary>
    private static SdlcAgentStageCall AnalystCall(SdlcAgentStageHarness harness, JobExecution job)
    {
        Result<string> Check(string text) => StackAnalysis.Check(AgentOutput.UnwrapFence(text), null);
        AgentRunRequest Request(string? feedback) => new()
        {
            ProjectId = harness.Data.Project.Id,
            Agent = SdlcAgents.RepositoryAnalyst,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_NAME"] = harness.Data.Project.Name,
                ["VERSION_REQUEST"] = "Add dark mode.",
                ["BASE"] = BaseText,
                ["STACK_GUIDANCE"] = null,
                ["STACK_DETECTION"] = StackGuidance.DetectionInput(null),
                [AgentRunner.FeedbackInput] = feedback,
            },
            UserFeedback = feedback,
            Repo = RepoScope.BaseCommit,
        };

        return new SdlcAgentStageCall(
            Request(null),
            new ArtifactAgentSpec(SdlcArtifacts.CodebaseSummary, Check, (_, error) => Request(StackAnalysis.CorrectiveFeedback(error))) { LogsRejection = true });
    }

    private static SdlcAgentStageHarness Harness(params string?[] replies)
    {
        // Queued when a version with code starts: the project may still be in analysis (the run records that phase).
        var harness = new SdlcAgentStageHarness(SdlcAgents.RepositoryAnalyst, [], replies);
        harness.Data.Project.CurrentPhase = Domain.Lifecycle.Phase.Analysis;
        return harness;
    }

    [Fact]
    public async Task FR318_FR319_repository_analyst_runs_bound_on_refinement_recorded_under_the_projects_phase()
    {
        using var harness = Harness([null]);
        var job = SdlcAgentStageHarness.Job();
        var call = AnalystCall(harness, job);

        var outcome = await harness.RunReplyJobAsync(call, "refinement");

        var run = Assert.Single(harness.Store.AgentRuns);
        var template = new EmbeddedPromptLibrary().Template(SdlcAgents.RepositoryAnalyst);
        var rendered = template.Render(call.PromptValues()).Value;
        var request = Assert.Single(harness.Provider.Requests);
        Assert.Equal((rendered.System, rendered.User), (request.System, request.Messages[^1].Content));
        Assert.Contains(BaseText, request.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal(
            (job.Id, SdlcAgents.RepositoryAnalyst, "analysis", AgentRunStatus.Succeeded, (string?)null, (string?)null),
            (run.Id, run.Agent, run.Stage, run.Status, run.Error, run.AgentKey));
        Assert.Equal((job.Id, run.CompletedAt!.Value), (outcome.RunId, outcome.CompletedAt));
        Assert.True(StackAnalysis.Check(AgentOutput.UnwrapFence(outcome.Text), null).IsSuccess);
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal(
            AideJson.Serialize(new { runId = job.Id, jobId = job.Id, agent = SdlcAgents.RepositoryAnalyst, provider = "fake", model = "fake-1", promptVersion = template.Version, stepId = (Guid?)null, stack = (string?)null, agentKey = (string?)null, legacyAgentKey = (string?)null }),
            harness.Data.Events[0].Payload);
        Assert.Empty(harness.Store.Artifacts);
        Assert.Equal((2, 0), (harness.Data.ProjectReads, harness.Context.Reads));
    }

    [Fact]
    public async Task FR318_FR319_repository_analyst_a_summary_missing_sections_is_rerun_once_with_their_names()
    {
        using var harness = Harness([Missing, null]);
        var job = SdlcAgentStageHarness.Job();

        var outcome = await harness.RunReplyJobAsync(AnalystCall(harness, job), "refinement");

        var corrective = AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey);
        var runs = harness.Store.AgentRuns;
        Assert.Equal([(job.Id, AgentRunStatus.Succeeded, (string?)null), (corrective, AgentRunStatus.Succeeded, null)], runs.Select(r => (r.Id, r.Status, r.Error)));
        Assert.Equal(corrective, outcome.RunId);
        Assert.Equal(StackAnalysis.CorrectiveFeedback(StackAnalysis.Check(Missing, null).Error!), runs[1].UserFeedback);
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED", "RUN_STARTED", "RUN_COMPLETED"], harness.Data.Events.Select(e => e.Type));
    }

    [Fact]
    public async Task FR318_FR319_repository_analyst_still_missing_sections_fails_with_MISSING_SECTIONS()
    {
        using var harness = Harness([Missing]);
        var job = SdlcAgentStageHarness.Job();

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunReplyJobAsync(AnalystCall(harness, job), "refinement"));

        var error = StackAnalysis.Check(Missing, null).Error!;
        Assert.Equal(("MISSING_SECTIONS", error.Message, false), (ex.ErrorCode, ex.Message, ex.Transient));
        var runs = harness.Store.AgentRuns;
        Assert.Equal([AgentRunStatus.Succeeded, AgentRunStatus.Failed], runs.Select(r => r.Status));
        Assert.Equal(
            AideJson.Serialize(new { runId = runs[1].Id, agent = SdlcAgents.RepositoryAnalyst, errorCode = "MISSING_SECTIONS", error = runs[1].Error, attempts = 1, deadLettered = false }),
            harness.Data.Events[^1].Payload);
    }

    [Fact]
    public async Task FR318_FR319_FR310_repository_analyst_redelivery_returns_the_stored_reply_without_a_call()
    {
        using var harness = Harness([Missing, null]);
        var job = SdlcAgentStageHarness.Job();
        var first = await harness.RunReplyJobAsync(AnalystCall(harness, job), "refinement");

        var again = await harness.RunReplyJobAsync(AnalystCall(harness, job with { Attempt = 2 }), "refinement");

        Assert.Equal(2, harness.Provider.Requests.Count);
        Assert.Equal(2, harness.Store.AgentRuns.Count);
        Assert.Equal((first.RunId, first.Text, first.CompletedAt), (again.RunId, again.Text, again.CompletedAt));
        Assert.Empty(harness.Store.Artifacts); // the store override stores nothing, on every delivery
    }
}
