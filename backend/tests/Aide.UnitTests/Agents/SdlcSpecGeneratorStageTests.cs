using Aide.Api.Agents;
using Aide.Domain.Agents;
using Aide.Domain.Specs;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence.Conversion;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-318: <c>spec_generator</c> on the engine runner records what 0.6.1's <c>AgentRunner</c> and
/// <c>ArtifactAgentJob</c> recorded (their integration tests, <c>SpecAndPlanAgentTests</c> and
/// <c>AgentRunnerTests</c>, are the reference): the run row, the events with 0.6.1's payloads in 0.6.1's order, the
/// prompt, the token commit and the draft. Expected payloads are 0.6.1's literals.
/// </summary>
public class SdlcSpecGeneratorStageTests
{
    internal const string Invalid = "## 1. Overview\nOnly an overview.";

    private static readonly SpecValidationContext Context = IterationContext.None.SpecContext;

    [Fact]
    public async Task FR318_spec_generator_success_records_the_0_6_1_run_events_prompt_tokens_and_draft()
    {
        using var harness = new SdlcAgentStageHarness([null]);
        var job = SdlcAgentStageHarness.Job();

        await harness.RunAsync(job);

        var run = Assert.Single(harness.Store.AgentRuns);
        var template = new EmbeddedPromptLibrary().Template(SdlcAgents.SpecGenerator);
        var reply = SdlcAgentRuns.ReadOutput(run.Output)!.Text;
        Assert.Equal(
            (job.Id, SdlcAgents.SpecGenerator, "specification", AgentRunStatus.Succeeded, 1, (string?)null, (string?)null, harness.Data.Project.CurrentReleaseVersionId, template.Version),
            (run.Id, run.Agent, run.Stage, run.Status, run.Attempts, run.Error, run.UserFeedback, run.ReleaseVersionId, run.PromptVersion));
        Assert.Empty(run.InputArtifacts);
        Assert.Equal(AideJson.Serialize(new RunOutput(reply, "end_turn") { MaxOutputTokens = FixedOutputLimits.MaxTokens, Continuations = 0 }), run.Output);
        Assert.Equal(0, run.CompletedAt!.Value.Ticks % TimeSpan.TicksPerMicrosecond);

        // The prompt is 0.6.1's render of the job's values.
        var rendered = template.Render(SdlcAgentStageHarness.SpecCall(job, harness.Data.Project.Id).PromptValues()).Value;
        var request = Assert.Single(harness.Provider.Requests);
        Assert.Equal((rendered.System, rendered.User, (int?)FixedOutputLimits.MaxTokens), (request.System, request.Messages[^1].Content, request.MaxTokens));

        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED", "ARTIFACT_CREATED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal(
            AideJson.Serialize(new { runId = job.Id, jobId = job.Id, agent = SdlcAgents.SpecGenerator, provider = "fake", model = "fake-1", promptVersion = template.Version, stepId = (Guid?)null, stack = (string?)null, agentKey = (string?)null, legacyAgentKey = (string?)null }),
            harness.Data.Events[0].Payload);
        Assert.Equal(
            AideJson.Serialize(new { runId = job.Id, agent = SdlcAgents.SpecGenerator, inputTokens = run.InputTokens, outputTokens = run.OutputTokens, costEstimate = run.CostEstimate, durationMs = run.DurationMs, warnings = Array.Empty<string>() }),
            harness.Data.Events[1].Payload);

        Assert.Equal([(long)run.InputTokens + run.OutputTokens], harness.Data.TokenCommits);
        var draft = Assert.Single(harness.Store.Artifacts);
        Assert.Equal((SdlcArtifacts.TechnicalSpecification, (Guid?)job.Id, SpecValidator.Check(AgentOutput.UnwrapFence(reply), Context).Value, run.CompletedAt!.Value), (draft.Type, draft.AgentRunId, draft.Content, draft.CreatedAt));
    }

    [Fact]
    public async Task FR318_spec_generator_rejected_draft_rerun_gets_the_previous_spec_and_the_comment()
    {
        using var harness = new SdlcAgentStageHarness([null]);
        var job = SdlcAgentStageHarness.Job();

        await harness.RunAsync(job, previousSpec: "## 1. Overview\nThe first spec.", feedback: "Add rate limits");

        var run = Assert.Single(harness.Store.AgentRuns);
        Assert.Equal("Add rate limits", run.UserFeedback);
        var prompt = Assert.Single(harness.Provider.Requests).Messages[^1].Content;
        var call = SdlcAgentStageHarness.SpecCall(job, harness.Data.Project.Id, "## 1. Overview\nThe first spec.", "Add rate limits");
        Assert.Equal(new EmbeddedPromptLibrary().Template(SdlcAgents.SpecGenerator).Render(call.PromptValues()).Value.User, prompt);
        Assert.Contains("Add rate limits", prompt, StringComparison.Ordinal);
        Assert.Contains("The first spec.", prompt, StringComparison.Ordinal);
        Assert.Equal((Guid?)job.Id, Assert.Single(harness.Store.Artifacts).AgentRunId);
    }

    [Fact]
    public async Task FR318_spec_generator_invalid_reply_is_rerun_once_with_0_6_1s_corrective_feedback()
    {
        using var harness = new SdlcAgentStageHarness([Invalid, null]);
        var job = SdlcAgentStageHarness.Job();

        await harness.RunAsync(job);

        var corrective = AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey);
        var runs = harness.Store.AgentRuns;
        Assert.Equal([(job.Id, AgentRunStatus.Succeeded, (string?)null), (corrective, AgentRunStatus.Succeeded, null)], runs.Select(r => (r.Id, r.Status, r.Error)));
        var error = SpecValidator.Check(Invalid, Context).Error!;
        Assert.Equal(ArtifactAgentSpec.JoinFeedback(null, SpecValidator.CorrectiveFeedback(error, Context)), runs[1].UserFeedback);
        Assert.Contains("Only an overview.", harness.Provider.Requests[1].Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED", "RUN_STARTED", "RUN_COMPLETED", "ARTIFACT_CREATED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal((Guid?)corrective, Assert.Single(harness.Store.Artifacts).AgentRunId);
        Assert.Equal(runs.Select(r => (long)r.InputTokens + r.OutputTokens), harness.Data.TokenCommits);
    }

    [Fact]
    public async Task FR318_spec_generator_still_invalid_after_the_rerun_fails_with_MISSING_SECTIONS_and_no_draft()
    {
        using var harness = new SdlcAgentStageHarness([Invalid]);
        var job = SdlcAgentStageHarness.Job();

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunAsync(job));

        var error = SpecValidator.Check(Invalid, Context).Error!;
        Assert.Equal(("MISSING_SECTIONS", error.Message, false), (ex.ErrorCode, ex.Message, ex.Transient));
        var runs = harness.Store.AgentRuns;
        Assert.Equal([AgentRunStatus.Succeeded, AgentRunStatus.Failed], runs.Select(r => r.Status));
        Assert.Equal($"MISSING_SECTIONS: {error.Message}", runs[1].Error);
        Assert.True(runs[1].OutputTokens > 0);
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED", "RUN_STARTED", "RUN_FAILED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal(
            AideJson.Serialize(new { runId = runs[1].Id, agent = SdlcAgents.SpecGenerator, errorCode = "MISSING_SECTIONS", error = runs[1].Error, attempts = 1, deadLettered = false }),
            harness.Data.Events[3].Payload);
        Assert.Empty(harness.Store.Artifacts);
        Assert.Equal(runs.Sum(r => (long)r.InputTokens + r.OutputTokens), harness.Data.Project.TokensUsed);
    }

    [Fact]
    public async Task FR318_FR310_a_redelivered_spec_job_reuses_its_run_and_draft_without_a_call()
    {
        using var harness = new SdlcAgentStageHarness([null]);
        var job = SdlcAgentStageHarness.Job();
        await harness.RunAsync(job);

        await harness.RunAsync(job with { Attempt = 2 });

        Assert.Single(harness.Provider.Requests);
        Assert.Single(harness.Store.AgentRuns);
        Assert.Single(harness.Store.Artifacts);
        Assert.Single(harness.Data.Events, e => e.Type == "ARTIFACT_CREATED");
    }

    [Fact]
    public async Task FR318_a_draft_the_phase_no_longer_allows_is_skipped_and_the_run_stays_succeeded()
    {
        using var harness = new SdlcAgentStageHarness([null]);
        harness.Data.DraftAllowed = false;

        await harness.RunAsync(SdlcAgentStageHarness.Job());

        Assert.Equal(AgentRunStatus.Succeeded, Assert.Single(harness.Store.AgentRuns).Status);
        Assert.Empty(harness.Store.Artifacts);
    }
}
