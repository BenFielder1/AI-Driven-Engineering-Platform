using Aide.Api.Agents;
using Aide.Domain.Agents;
using Aide.Domain.Execution;
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
/// FR-318, FR-314 (FR-29, FR-243): <c>plan_generator</c> on the engine runner records what 0.6.1's
/// <see cref="AgentRunner"/> and <see cref="ArtifactAgentJob"/> recorded for <see cref="PlanGenerationJobHandler"/>'s
/// request: the prompt, the run and events with 0.6.1's payload literals, the draft rendered by
/// <see cref="PlanDocument.RenderChecked"/> (with the <c>aide-plan</c> block and the added deploy-file steps), the one
/// corrective re-run with <c>PREVIOUS_PLAN</c> and 0.6.1's wording, <c>INVALID_PLAN</c>, and redelivery.
/// </summary>
public class SdlcPlanGeneratorStageTests
{
    private const int MaxFiles = 5;

    /// <summary>Step 1 depends on a step that doesn't exist (<c>INVALID_PLAN</c>).</summary>
    private const string Invalid = """{"steps":[{"number":1,"title":"A","task":"Do it.","files":[{"path":"a.txt","action":"create"}],"dependencies":[5],"covers":[],"agent":"generalist"}]}""";

    /// <summary>The plan job's request and spec, as <see cref="PlanGenerationJobHandler"/> builds them (no stack).</summary>
    private static SdlcAgentStageCall PlanCall(SdlcAgentStageHarness harness, JobExecution job, string? previousPlan = null, string? feedback = null, DeployFileCheck? deployFiles = null)
    {
        AgentRunRequest Request(string? previous, string? runFeedback) => new()
        {
            ProjectId = harness.Data.Project.Id,
            Agent = SdlcAgents.PlanGenerator,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_REQUEST"] = SdlcAgentStageHarness.ProjectRequest,
                ["TECHNICAL_SPECIFICATION"] = "## 1. Overview\nA todo API.",
                ["CODEBASE_SUMMARY"] = null,
                ["PREVIOUS_PLAN"] = previous,
                ["MAX_FILES_PER_STEP"] = "5",
                ["STACK_GUIDANCE"] = null,
            },
            UserFeedback = runFeedback,
            Repo = RepoScope.BaseCommit,
        };

        var spec = new ArtifactAgentSpec(
            SdlcArtifacts.ImplementationPlan,
            text => ToContent(text, deployFiles),
            (rejected, error) => Request(AgentOutput.ExtractJsonObject(rejected) ?? rejected, ArtifactAgentJob.JoinFeedback(feedback, PlanDocument.CorrectiveFeedback(error))));
        return new SdlcAgentStageCall(Request(previousPlan, feedback), spec);
    }

    /// <summary>0.6.1's content transform: the plan read and validated (deploy-file steps added), then rendered and proven.</summary>
    private static Result<string> ToContent(string text, DeployFileCheck? deployFiles)
    {
        var plan = PlanDocument.ParseAgentOutput(text, MaxFiles, null, deployFiles);
        return plan.IsSuccess ? PlanDocument.RenderChecked(SdlcArtifacts.ImplementationPlan, plan.Value, MaxFiles, null) : Result.Failure<string>(plan.Error!);
    }

    private static SdlcAgentStageHarness Harness(params string?[] replies)
    {
        var harness = new SdlcAgentStageHarness(SdlcAgents.PlanGenerator, [], replies);
        harness.Data.Project.CurrentPhase = Domain.Lifecycle.Phase.Planning;
        return harness;
    }

    [Fact]
    public async Task FR318_FR314_plan_generator_success_stores_0_6_1s_rendered_plan_with_its_aide_plan_block()
    {
        using var harness = Harness([null]);
        var job = SdlcAgentStageHarness.Job();
        var call = PlanCall(harness, job);

        await harness.RunCallAsync(call, "planning");

        var run = Assert.Single(harness.Store.AgentRuns);
        var template = new EmbeddedPromptLibrary().Template(SdlcAgents.PlanGenerator);
        var rendered = template.Render(call.PromptValues()).Value;
        var request = Assert.Single(harness.Provider.Requests);
        Assert.Equal((rendered.System, rendered.User), (request.System, request.Messages[^1].Content));
        Assert.Equal((job.Id, "planning", AgentRunStatus.Succeeded, (string?)null), (run.Id, run.Stage, run.Status, run.Error));
        var draft = Assert.Single(harness.Store.Artifacts);
        var expected = ToContent(AgentRunner.ReadOutput(run.Output)!.Text, null).Value;
        Assert.Equal((SdlcArtifacts.ImplementationPlan, (Guid?)job.Id, expected), (draft.Type, draft.AgentRunId, draft.Content));
        Assert.Contains("```aide-plan", draft.Content, StringComparison.Ordinal);
        Assert.Equal(4, PlanDocument.ParseMarkdown(draft.Content, MaxFiles).Value.Count);
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED", "ARTIFACT_CREATED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal(
            AideJson.Serialize(new { runId = job.Id, agent = SdlcAgents.PlanGenerator, inputTokens = run.InputTokens, outputTokens = run.OutputTokens, costEstimate = run.CostEstimate, durationMs = run.DurationMs, warnings = Array.Empty<string>() }),
            harness.Data.Events[1].Payload);
        Assert.Equal((2, 0), (harness.Data.ProjectReads, harness.Context.Reads));
    }

    [Fact]
    public async Task FR318_FR314_plan_generator_invalid_then_valid_reruns_once_with_PREVIOUS_PLAN_and_0_6_1s_wording()
    {
        using var harness = Harness([Invalid, null]);
        var job = SdlcAgentStageHarness.Job();

        await harness.RunCallAsync(PlanCall(harness, job, feedback: "Split step 4"), "planning");

        var corrective = AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey);
        var runs = harness.Store.AgentRuns;
        Assert.Equal([(job.Id, AgentRunStatus.Succeeded, (string?)null), (corrective, AgentRunStatus.Succeeded, null)], runs.Select(r => (r.Id, r.Status, r.Error)));
        var error = PlanDocument.ParseAgentOutput(Invalid, MaxFiles).Error!;
        Assert.Equal("INVALID_PLAN", error.Code);
        Assert.Equal(ArtifactAgentJob.JoinFeedback("Split step 4", PlanDocument.CorrectiveFeedback(error)), runs[1].UserFeedback);
        var retryPrompt = harness.Provider.Requests[1].Messages[^1].Content;
        Assert.Contains("\"dependencies\":[5]", retryPrompt, StringComparison.Ordinal); // the rejected plan as PREVIOUS_PLAN
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED", "RUN_STARTED", "RUN_COMPLETED", "ARTIFACT_CREATED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal((Guid?)corrective, Assert.Single(harness.Store.Artifacts).AgentRunId);
    }

    [Fact]
    public async Task FR318_FR314_plan_generator_invalid_twice_fails_with_INVALID_PLAN_and_no_draft()
    {
        using var harness = Harness([Invalid]);
        var job = SdlcAgentStageHarness.Job();

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunCallAsync(PlanCall(harness, job), "planning"));

        var error = PlanDocument.ParseAgentOutput(Invalid, MaxFiles).Error!;
        Assert.Equal(("INVALID_PLAN", error.Message, false), (ex.ErrorCode, ex.Message, ex.Transient));
        var runs = harness.Store.AgentRuns;
        Assert.Equal([AgentRunStatus.Succeeded, AgentRunStatus.Failed], runs.Select(r => r.Status));
        Assert.Equal($"INVALID_PLAN: {error.Message}", runs[1].Error);
        Assert.Equal(
            AideJson.Serialize(new { runId = runs[1].Id, agent = SdlcAgents.PlanGenerator, errorCode = "INVALID_PLAN", error = runs[1].Error, attempts = 1, deadLettered = false }),
            harness.Data.Events[^1].Payload);
        Assert.Empty(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR318_FR243_plan_generator_adds_0_6_1s_deploy_file_step_before_storing()
    {
        using var harness = Harness([null]);
        var job = SdlcAgentStageHarness.Job();
        var deployFiles = new DeployFileCheck(
            [new StackRequiredFile("deploy-vercel", "vercel", "vercel.json", [], "generalist", ["{}"])],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Optimisation: false);

        await harness.RunCallAsync(PlanCall(harness, job, deployFiles: deployFiles), "planning");

        var run = Assert.Single(harness.Store.AgentRuns);
        var draft = Assert.Single(harness.Store.Artifacts);
        Assert.Equal(ToContent(AgentRunner.ReadOutput(run.Output)!.Text, deployFiles).Value, draft.Content);
        var steps = PlanDocument.ParseMarkdown(draft.Content, MaxFiles).Value;
        Assert.Equal(5, steps.Count);
        Assert.Contains(steps, s => s.Files.Any(f => f.Path == "vercel.json"));
    }

    [Fact]
    public async Task FR318_FR310_plan_generator_redelivery_reuses_its_run_and_draft_without_a_call()
    {
        using var harness = Harness([Invalid, null]);
        var job = SdlcAgentStageHarness.Job();
        await harness.RunCallAsync(PlanCall(harness, job), "planning");

        await harness.RunCallAsync(PlanCall(harness, job with { Attempt = 2 }), "planning");

        Assert.Equal(2, harness.Provider.Requests.Count);
        Assert.Equal(2, harness.Store.AgentRuns.Count);
        Assert.Single(harness.Store.Artifacts);
        Assert.Single(harness.Data.Events, e => e.Type == "ARTIFACT_CREATED");
    }
}
