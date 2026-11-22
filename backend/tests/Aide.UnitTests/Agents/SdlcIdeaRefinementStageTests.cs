using Aide.Api.Agents;
using Aide.Domain.Agents;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence.Conversion;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-318: <c>idea_refinement</c> on the engine runner records what 0.6.1's <c>AgentRunner</c> and
/// <c>ArtifactAgentJob</c> recorded for <see cref="IdeaRefinementJobHandler"/>'s request (a first version without
/// a summary or stack): the prompt, the run (answers and feedback as its <c>user_feedback</c>), the events with 0.6.1's
/// payload literals, the warnings, the corrective re-run with 0.6.1's wording and <c>INVALID_OUTPUT</c>.
/// </summary>
public class SdlcIdeaRefinementStageTests
{
    private const string Unreadable = "Not JSON at all.";

    private static readonly IReadOnlyList<RefinementAnswer> Answers = [new("q1", "Who uses it?", "Small teams.")];

    /// <summary>The refinement job's request and spec, as <see cref="IdeaRefinementJobHandler"/> builds them.</summary>
    private static SdlcAgentStageCall RefinementCall(JobExecution job, Guid projectId, string? feedback = null, IReadOnlyList<string>? warnings = null)
    {
        var answers = RefinementRules.FormatAnswers(Answers);
        AgentRunRequest Request(string? runFeedback) => new()
        {
            ProjectId = projectId,
            Agent = SdlcAgents.IdeaRefinement,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_NAME"] = "Todo API",
                ["IDEA"] = "A todo list API for small teams.",
                ["SOURCE_ITEMS"] = null,
                ["CURRENT_DRAFT"] = null,
                ["ANSWERS"] = answers,
                ["CODEBASE_SUMMARY"] = null,
                ["PREVIOUS_PROJECT_REQUEST"] = null,
                ["PREVIOUS_SPECIFICATION"] = null,
                ["STACK_GUIDANCE"] = null,
                ["STACK_DETECTION"] = null,
                [SdlcAgentRuns.FeedbackInput] = runFeedback,
            },
            UserFeedback = RefinementRules.RunFeedback(Answers, runFeedback),
            Repo = RepoScope.BaseCommit,
            Warnings = warnings ?? [],
        };

        var spec = new ArtifactAgentSpec(
            SdlcArtifacts.ProjectRequest,
            text =>
            {
                var parsed = RefinementRules.Parse(text);
                return parsed.IsSuccess ? Result.Success(parsed.Value.ProjectRequest) : Result.Failure<string>(parsed.Error!);
            },
            (_, error) => Request(ArtifactAgentSpec.JoinFeedback(feedback, $"Your previous reply was rejected: {error.Message} Respond with only the JSON object described above.")));
        return new SdlcAgentStageCall(Request(feedback), spec);
    }

    private static SdlcAgentStageHarness Harness(params string?[] replies)
    {
        var harness = new SdlcAgentStageHarness(SdlcAgents.IdeaRefinement, [], replies);
        harness.Data.Project.CurrentPhase = Domain.Lifecycle.Phase.Refinement;
        return harness;
    }

    [Fact]
    public async Task FR318_idea_refinement_success_records_the_0_6_1_prompt_run_events_and_draft()
    {
        using var harness = Harness([null]);
        var job = SdlcAgentStageHarness.Job();
        var call = RefinementCall(job, harness.Data.Project.Id, feedback: "Keep it small", warnings: [AgentRunWarnings.SummaryWaitTimeout]);

        await harness.RunCallAsync(call, "refinement");

        var run = Assert.Single(harness.Store.AgentRuns);
        var template = new EmbeddedPromptLibrary().Template(SdlcAgents.IdeaRefinement);
        var rendered = template.Render(call.PromptValues()).Value;
        var request = Assert.Single(harness.Provider.Requests);
        Assert.Equal((rendered.System, rendered.User), (request.System, request.Messages[^1].Content));
        Assert.Equal(
            (job.Id, SdlcAgents.IdeaRefinement, "refinement", AgentRunStatus.Succeeded, 1, (string?)null, RefinementRules.RunFeedback(Answers, "Keep it small")),
            (run.Id, run.Agent, run.Stage, run.Status, run.Attempts, run.Error, run.UserFeedback));
        var reply = SdlcAgentRuns.ReadOutput(run.Output)!;
        Assert.Equal([AgentRunWarnings.SummaryWaitTimeout], reply.Warnings);
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED", "ARTIFACT_CREATED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal(
            AideJson.Serialize(new { runId = job.Id, jobId = job.Id, agent = SdlcAgents.IdeaRefinement, provider = "fake", model = "fake-1", promptVersion = template.Version, stepId = (Guid?)null, stack = (string?)null, agentKey = (string?)null, legacyAgentKey = (string?)null }),
            harness.Data.Events[0].Payload);
        Assert.Equal(
            AideJson.Serialize(new { runId = job.Id, agent = SdlcAgents.IdeaRefinement, inputTokens = run.InputTokens, outputTokens = run.OutputTokens, costEstimate = run.CostEstimate, durationMs = run.DurationMs, warnings = new[] { AgentRunWarnings.SummaryWaitTimeout } }),
            harness.Data.Events[1].Payload);
        var draft = Assert.Single(harness.Store.Artifacts);
        Assert.Equal((SdlcArtifacts.ProjectRequest, (Guid?)job.Id, RefinementRules.Parse(reply.Text).Value.ProjectRequest), (draft.Type, draft.AgentRunId, draft.Content));
        Assert.Equal((2, 0), (harness.Data.ProjectReads, harness.Context.Reads));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FR318_FR314_idea_refinement_unreadable_reply_is_rerun_once_with_0_6_1s_wording(bool version3)
    {
        using var harness = Harness([Unreadable, null]);
        SdlcOutputGuardFixture.Pin(harness, version3); // D-0.7.0-44: v3's guard, or v2's host check
        var job = SdlcAgentStageHarness.Job();
        var call = RefinementCall(job, harness.Data.Project.Id, feedback: "Keep it small");

        var result = await harness.RunCallAsync(call, "refinement");

        var corrective = AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey);
        var runs = harness.Store.AgentRuns;
        Assert.Equal([(job.Id, AgentRunStatus.Succeeded, (string?)null), (corrective, AgentRunStatus.Succeeded, null)], runs.Select(r => (r.Id, r.Status, r.Error)));
        var error = RefinementRules.Parse(Unreadable).Error!;
        var wording = ArtifactAgentSpec.JoinFeedback("Keep it small", $"Your previous reply was rejected: {error.Message} Respond with only the JSON object described above.");
        Assert.Equal(RefinementRules.RunFeedback(Answers, wording), runs[1].UserFeedback);
        Assert.Contains("Your previous reply was rejected:", harness.Provider.Requests[1].Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED", "RUN_STARTED", "RUN_COMPLETED", "ARTIFACT_CREATED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal((Guid?)corrective, Assert.Single(harness.Store.Artifacts).AgentRunId);
        Assert.Equal(error.Message, result.Attempts[0].Findings); // FR-314: the check's one reason, once
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FR318_FR314_idea_refinement_still_unreadable_fails_with_INVALID_OUTPUT_and_no_draft(bool version3)
    {
        using var harness = Harness([Unreadable]);
        SdlcOutputGuardFixture.Pin(harness, version3); // D-0.7.0-44: v3's guard, or v2's host check
        var job = SdlcAgentStageHarness.Job();

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunCallAsync(RefinementCall(job, harness.Data.Project.Id), "refinement"));

        var error = RefinementRules.Parse(Unreadable).Error!;
        Assert.Equal((error.Code, error.Message, false), (ex.ErrorCode, ex.Message, ex.Transient));
        Assert.Equal("INVALID_OUTPUT", ex.ErrorCode);
        var runs = harness.Store.AgentRuns;
        Assert.Equal([AgentRunStatus.Succeeded, AgentRunStatus.Failed], runs.Select(r => r.Status));
        Assert.Equal(
            AideJson.Serialize(new { runId = runs[1].Id, agent = SdlcAgents.IdeaRefinement, errorCode = "INVALID_OUTPUT", error = runs[1].Error, attempts = 1, deadLettered = false }),
            harness.Data.Events[^1].Payload);
        Assert.Empty(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR318_idea_refinement_over_budget_is_refused_with_0_6_1s_event_and_no_call()
    {
        using var harness = Harness([null]);
        harness.Data.Project.TokenBudget = 10;
        harness.Data.Project.TokensUsed = 10;
        var job = SdlcAgentStageHarness.Job();

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunCallAsync(RefinementCall(job, harness.Data.Project.Id), "refinement"));

        Assert.Equal("BUDGET_EXCEEDED", ex.ErrorCode);
        Assert.Empty(harness.Provider.Requests);
        Assert.Equal(
            ("BUDGET_EXCEEDED", AideJson.Serialize(new { agent = SdlcAgents.IdeaRefinement, tokenBudget = (long?)10, tokensUsed = 10L, jobId = job.Id })),
            Assert.Single(harness.Data.Events));
    }
}
