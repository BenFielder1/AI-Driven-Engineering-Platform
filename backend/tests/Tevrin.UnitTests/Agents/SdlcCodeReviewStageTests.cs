using Tevrin.Api.Agents;
using Tevrin.Domain.Agents;
using Tevrin.Domain.RepoTools;
using Tevrin.Domain.Review;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;
using Tevrin.Infrastructure.Agents;
using Tevrin.Infrastructure.Persistence.Conversion;
using Tevrin.Infrastructure.RepoTools;
using Tevrin.Pack.Sdlc;

namespace Tevrin.UnitTests.Agents;

/// <summary>
/// FR-318 (FR-38): <c>code_review</c> on the engine runner records what 0.6.1's <c>ReviewJobHandler</c> got from
/// <c>AgentRunner</c>: the prompt from the job's values (the cycle, findings, previous review, code and its own
/// plan view win over the stage inputs), the run and its events with 0.6.1's payload literals, the repo tools on the working
/// tree, the corrective re-run (the first reply must read and meet the requirement, the second only read) and the
/// failure. No draft is stored with the run: the job stores the optimisation plan after scoring.
/// </summary>
public class SdlcCodeReviewStageTests
{
    private const string Unreadable = "Not a review.";

    /// <summary>The review's request and spec, as <c>ReviewJobHandler.RunAgentAsync</c> builds them (no stack, no findings).</summary>
    private static SdlcAgentStageCall ReviewCall(SdlcAgentStageHarness harness, JobExecution job, string? feedback = null)
    {
        AgentRunRequest Request(string? runFeedback) => new()
        {
            ProjectId = harness.Data.Project.Id,
            Agent = SdlcAgents.CodeReview,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["REVIEW_CYCLE"] = "1",
                ["TECHNICAL_SPECIFICATION"] = SdlcAgentStageHarness.ProjectRequest,
                ["IMPLEMENTATION_PLAN"] = "# Plan\n\n---\n\n# Optimization plan v1 (the latest cycle's steps)",
                ["CODEBASE_SUMMARY"] = null,
                ["DETERMINISTIC_FINDINGS"] = null,
                ["PREVIOUS_REVIEW"] = null,
                ["CODE"] = "- src/app.ts (modify)\n\nconsole.log('hi');",
                ["MAX_FILES_PER_STEP"] = "5",
                ["STACK_GUIDANCE"] = null,
            },
            UserFeedback = runFeedback,
            Repo = RepoScope.WorkingTree,
        };

        var spec = new ArtifactAgentSpec(
            SdlcArtifacts.OptimizationPlan,
            text =>
            {
                var parsed = CodeReviewOutput.Parse(text, 5);
                var problem = parsed.IsSuccess ? (parsed.Value.OptimizationPlan.Count == 0 ? ReviewAssessment.MissingOptimizationPlanError() : null) : parsed.Error!;
                return problem is null ? Result.Success(text) : Result.Failure<string>(problem);
            },
            (_, problem) => Request(ArtifactAgentSpec.JoinFeedback(feedback, CodeReviewOutput.CorrectiveFeedback(problem))))
        {
            CorrectiveToContent = text => CodeReviewOutput.Parse(text, 5) is { IsFailure: true } failed ? Result.Failure<string>(failed.Error!) : Result.Success(text),
        };
        return new SdlcAgentStageCall(Request(feedback), spec);
    }

    private static SdlcAgentStageHarness Harness(IEnumerable<IFakeToolScript>? scripts = null, params string?[] replies)
    {
        var harness = new SdlcAgentStageHarness(SdlcAgents.CodeReview, scripts ?? [], replies);
        harness.Data.Project.CurrentPhase = Domain.Lifecycle.Phase.Review;
        return harness;
    }

    [Fact]
    public async Task FR318_code_review_success_returns_0_6_1s_run_outcome_with_its_prompt_events_and_no_draft()
    {
        using var harness = Harness(replies: [null]);
        var job = SdlcAgentStageHarness.Job();
        var call = ReviewCall(harness, job, feedback: "Check the API");

        var outcome = await harness.RunReplyJobAsync(call, "review");

        var run = Assert.Single(harness.Store.AgentRuns);
        var template = new EmbeddedPromptLibrary().Template(SdlcAgents.CodeReview);
        var rendered = template.Render(call.PromptValues()).Value;
        var request = Assert.Single(harness.Provider.Requests);
        Assert.Equal((rendered.System, rendered.User), (request.System, request.Messages[^1].Content));
        Assert.Contains("# Optimization plan v1", request.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal((job.Id, "review", AgentRunStatus.Succeeded, (string?)null, "Check the API"), (run.Id, run.Stage, run.Status, run.Error, run.UserFeedback));
        Assert.Equal((job.Id, SdlcAgentRuns.ReadOutput(run.Output)!.Text, run.CompletedAt!.Value, run.InputTokens), (outcome.RunId, outcome.Text, outcome.CompletedAt, outcome.InputTokens));
        Assert.True(CodeReviewOutput.Parse(outcome.Text, 5).IsSuccess);
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal(
            TevrinJson.Serialize(new { runId = job.Id, jobId = job.Id, agent = SdlcAgents.CodeReview, provider = "fake", model = "fake-1", promptVersion = template.Version, stepId = (Guid?)null, stack = (string?)null, agentKey = (string?)null, legacyAgentKey = (string?)null }),
            harness.Data.Events[0].Payload);
        Assert.Empty(harness.Store.Artifacts);
        Assert.Equal((2, 0), (harness.Data.ProjectReads, harness.Context.Reads));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FR318_FR314_code_review_an_unreadable_reply_is_rerun_once_with_0_6_1s_corrective_feedback(bool version3)
    {
        using var harness = Harness(replies: [Unreadable, null]);
        SdlcOutputGuardFixture.Pin(harness, version3); // D-0.7.0-44: v3's guard, or v2's host check
        var job = SdlcAgentStageHarness.Job();

        var outcome = await harness.RunReplyJobAsync(ReviewCall(harness, job), "review");

        var corrective = AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey);
        var runs = harness.Store.AgentRuns;
        Assert.Equal([(job.Id, AgentRunStatus.Succeeded, (string?)null), (corrective, AgentRunStatus.Succeeded, null)], runs.Select(r => (r.Id, r.Status, r.Error)));
        Assert.Equal(corrective, outcome.RunId);
        Assert.Equal(ArtifactAgentSpec.JoinFeedback(null, CodeReviewOutput.CorrectiveFeedback(CodeReviewOutput.Parse(Unreadable, 5).Error!)), runs[1].UserFeedback);
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED", "RUN_STARTED", "RUN_COMPLETED"], harness.Data.Events.Select(e => e.Type));
        Assert.Empty(harness.Store.Artifacts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FR318_FR314_code_review_a_reply_without_the_required_plan_is_rerun_and_a_readable_second_reply_is_used(bool version3)
    {
        // 0.6.1: the requirement (an optimisation plan) applies to the first reply only; the corrective one only has to read.
        var noPlan = new FakeCodeReviewResponder(FakeCodeReviewScript.Default with { OptimizationStepsByCycle = [0] });
        var replyWithoutPlan = noPlan.Respond(new LlmRequest
        {
            Model = "fake-1",
            System = string.Empty,
            Messages = [new LlmMessage(LlmRole.User, "<review_cycle>1</review_cycle>\n<code>- src/app.ts (modify)</code>")],
            Agent = SdlcAgents.CodeReview,
        });
        using var harness = Harness(replies: [replyWithoutPlan]);
        SdlcOutputGuardFixture.Pin(harness, version3); // D-0.7.0-44: v3's guard, or v2's host check
        var job = SdlcAgentStageHarness.Job();

        var outcome = await harness.RunReplyJobAsync(ReviewCall(harness, job), "review");

        Assert.Equal(AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey), outcome.RunId);
        Assert.Empty(CodeReviewOutput.Parse(outcome.Text, 5).Value.OptimizationPlan);
        Assert.Equal(ArtifactAgentSpec.JoinFeedback(null, CodeReviewOutput.CorrectiveFeedback(ReviewAssessment.MissingOptimizationPlanError())), harness.Store.AgentRuns[1].UserFeedback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FR318_FR314_code_review_still_unreadable_after_the_rerun_fails_the_run_with_its_code(bool version3)
    {
        using var harness = Harness(replies: [Unreadable]);
        SdlcOutputGuardFixture.Pin(harness, version3); // D-0.7.0-44: v3's guard, or v2's host check
        var job = SdlcAgentStageHarness.Job();

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunReplyJobAsync(ReviewCall(harness, job), "review"));

        var error = CodeReviewOutput.Parse(Unreadable, 5).Error!;
        Assert.Equal((error.Code, error.Message, false), (ex.ErrorCode, ex.Message, ex.Transient));
        var runs = harness.Store.AgentRuns;
        Assert.Equal([AgentRunStatus.Succeeded, AgentRunStatus.Failed], runs.Select(r => r.Status));
        Assert.Equal(
            TevrinJson.Serialize(new { runId = runs[1].Id, agent = SdlcAgents.CodeReview, errorCode = error.Code, error = runs[1].Error, attempts = 1, deadLettered = false }),
            harness.Data.Events[^1].Payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FR314_code_review_the_first_reply_must_read_and_the_reason_is_given_once(bool version3)
    {
        // D-0.7.0-44: on version 3 the review stage's sdlc.review-valid guard gives the reason, and the host check is skipped.
        using var harness = Harness(replies: [Unreadable, null]);
        SdlcOutputGuardFixture.Pin(harness, version3);

        var result = await harness.RunCallAsync(ReviewCall(harness, SdlcAgentStageHarness.Job()), "review");

        Assert.Equal([CodeReviewOutput.Parse(Unreadable, 5).Error!.Message, null], result.Attempts.Select(a => a.Findings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FR314_code_review_the_requirement_applies_to_the_first_reply_only(bool version3)
    {
        // 0.6.1's split, kept by the guard: the first reply must meet the requirement, the corrective one only has to read.
        var noPlan = new FakeCodeReviewResponder(FakeCodeReviewScript.Default with { OptimizationStepsByCycle = [0] });
        var replyWithoutPlan = noPlan.Respond(new LlmRequest
        {
            Model = "fake-1",
            System = string.Empty,
            Messages = [new LlmMessage(LlmRole.User, "<review_cycle>1</review_cycle>\n<code>- src/app.ts (modify)</code>")],
            Agent = SdlcAgents.CodeReview,
        });
        using var harness = Harness(replies: [replyWithoutPlan]);
        SdlcOutputGuardFixture.Pin(harness, version3);

        var result = await harness.RunCallAsync(ReviewCall(harness, SdlcAgentStageHarness.Job()), "review");

        Assert.Equal([ReviewAssessment.MissingOptimizationPlanError().Message, null], result.Attempts.Select(a => a.Findings));
    }

    [Fact]
    public async Task FR318_FR71_code_review_tool_calls_on_the_working_tree_are_the_runs()
    {
        var root = Directory.CreateTempSubdirectory("tevrin-review-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "app.ts"), "console.log('hi');", TestContext.Current.CancellationToken);
            var script = new FakeToolScript(SdlcAgents.CodeReview, [new FakeToolCall(RepoToolNames.ReadFile, """{"path":"app.ts"}""")]);
            using var harness = Harness([script], [null]);
            var session = new RepoToolSession(root.FullName);
            harness.Repo.Open = () => RepoToolContext.ForSession(session);

            await harness.RunReplyJobAsync(ReviewCall(harness, SdlcAgentStageHarness.Job()), "review");

            var run = Assert.Single(harness.Store.AgentRuns);
            Assert.Equal(session.ToJson(), run.ToolCalls);
            Assert.Contains(RepoToolNames.ReadFile, run.ToolCalls, StringComparison.Ordinal);
            Assert.Equal(RepoScope.WorkingTree, SdlcRepoToolScope.ScopeOf(SdlcAgents.CodeReview));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
