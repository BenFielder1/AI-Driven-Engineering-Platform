using Aide.Api.Agents;
using Aide.Domain.Agents;
using Aide.Domain.Stack;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.UnitTests.Stack;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Pack.Sdlc;

namespace Aide.UnitTests.Agents;

/// <summary>
/// FR-318: <c>stack_analyst</c> on the engine runner records what 0.6.1's <c>AgentRunner</c> and
/// <c>ArtifactAgentJob</c> recorded for <see cref="StackAnalysisJobHandler"/>'s request (no summary, detector
/// profile from the base commit, the catalog's options): the prompt, the run, the events in 0.6.1's order with its payload
/// literals (including <c>STACK_PROFILE_CREATED</c> after <c>ARTIFACT_CREATED</c>), the reconciled draft, the corrective
/// re-run with the allowed options, and <c>STACK_PROFILE_INVALID</c>.
/// </summary>
public class SdlcStackAnalystStageTests
{
    private const string BaseCommit = "0123456789abcdef0123456789abcdef01234567";

    private static readonly HashSet<string> BaseFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "README.md", "frontend/package.json", "frontend/index.html", "frontend/src/main.ts",
    };

    private static readonly Guid ReleaseVersionId = Guid.CreateVersion7();

    /// <summary>The stack analysis job's request and spec, as <see cref="StackAnalysisJobHandler"/> builds them.</summary>
    private static SdlcAgentStageCall StackCall(SdlcAgentStageHarness harness, JobExecution job, string versionRequest)
    {
        var catalog = StackTestCatalog.Catalog;
        var context = new StackProfileContext(catalog, StackPinning.GitHubVcs, BaseFiles);
        var detectorProfile = StackProfile.FromDetection(null, StackPinning.GitHubVcs, BaseCommit);
        AgentRunRequest Request(string? feedback) => new()
        {
            ProjectId = harness.Data.Project.Id,
            Agent = SdlcAgents.StackAnalyst,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_NAME"] = harness.Data.Project.Name,
                ["VERSION_REQUEST"] = versionRequest,
                ["BASE"] = $"commit {BaseCommit}",
                ["CODEBASE_SUMMARY"] = null,
                ["STACK_DETECTION"] = StackGuidance.DetectionInput(null),
                ["DETECTOR_PROFILE"] = StackProfile.ToJson(detectorProfile),
                ["CATALOG_OPTIONS"] = "- frontend: fe-vue (Vue + Vite)",
                ["PREVIOUS_PROFILE"] = null,
                [SdlcAgentRuns.FeedbackInput] = feedback,
            },
            UserFeedback = feedback,
            Repo = RepoScope.BaseCommit,
            Warnings = [AgentRunWarnings.SummaryUnavailable],
        };

        var spec = new ArtifactAgentSpec(
            SdlcArtifacts.StackProfile,
            text =>
            {
                var reply = StackProfile.CheckReply(text, context);
                return reply.IsFailure
                    ? Result.Failure<string>(reply.Error!)
                    : Result.Success(StackProfile.Render(reply.Value.Narrative, StackProfile.Reconcile(reply.Value.Document, null, StackPinning.GitHubVcs, BaseCommit)));
            },
            (_, error) => Request(ArtifactAgentSpec.JoinFeedback(null, StackProfile.CorrectiveFeedback(error, catalog))))
        {
            OnAdded = (draft, run) => harness.Data.AddEvent(
                "STACK_PROFILE_CREATED",
                harness.Data.Project.Id,
                new { artifactId = draft.Id, version = draft.Version, releaseVersionId = ReleaseVersionId, agentRunId = run.RunId, unresolvedLayers = StackProfile.Unresolved(StackProfile.Parse(draft.Content).Value.Document) },
                run.CompletedAt),
        };
        return new SdlcAgentStageCall(Request(null), spec);
    }

    private static SdlcAgentStageHarness Harness()
    {
        var harness = new SdlcAgentStageHarness(SdlcAgents.StackAnalyst, [], [null]);
        harness.Data.Project.CurrentPhase = Domain.Lifecycle.Phase.Analysis;
        return harness;
    }

    [Fact]
    public async Task FR318_stack_analyst_success_records_the_0_6_1_prompt_run_events_and_reconciled_draft()
    {
        using var harness = Harness();
        var job = SdlcAgentStageHarness.Job();
        var call = StackCall(harness, job, "Add a dark mode.");

        await harness.RunCallAsync(call, "analysis");

        var run = Assert.Single(harness.Store.AgentRuns);
        var template = new EmbeddedPromptLibrary().Template(SdlcAgents.StackAnalyst);
        var rendered = template.Render(call.PromptValues()).Value;
        var request = Assert.Single(harness.Provider.Requests);
        Assert.Equal((rendered.System, rendered.User), (request.System, request.Messages[^1].Content));
        Assert.Equal((job.Id, "analysis", AgentRunStatus.Succeeded, (string?)null, (string?)null), (run.Id, run.Stage, run.Status, run.Error, run.UserFeedback));
        Assert.Equal(["RUN_STARTED", "RUN_COMPLETED", "ARTIFACT_CREATED", "STACK_PROFILE_CREATED"], harness.Data.Events.Select(e => e.Type));
        Assert.Equal(
            AideJson.Serialize(new { runId = job.Id, agent = SdlcAgents.StackAnalyst, inputTokens = run.InputTokens, outputTokens = run.OutputTokens, costEstimate = run.CostEstimate, durationMs = run.DurationMs, warnings = new[] { AgentRunWarnings.SummaryUnavailable } }),
            harness.Data.Events[1].Payload);
        var draft = Assert.Single(harness.Store.Artifacts);
        Assert.Equal((SdlcArtifacts.StackProfile, (Guid?)job.Id, call.Spec.ToContent(SdlcAgentRuns.ReadOutput(run.Output)!.Text).Value), (draft.Type, draft.AgentRunId, draft.Content));
        Assert.Equal(
            AideJson.Serialize(new { artifactId = draft.Id, version = draft.Version, releaseVersionId = ReleaseVersionId, agentRunId = job.Id, unresolvedLayers = StackProfile.Unresolved(StackProfile.Parse(draft.Content).Value.Document) }),
            harness.Data.Events[3].Payload);
        Assert.Equal((2, 0), (harness.Data.ProjectReads, harness.Context.Reads));
    }

    [Fact]
    public async Task FR318_stack_analyst_a_profile_naming_an_option_outside_the_catalog_is_rerun_once_with_the_allowed_options()
    {
        using var harness = Harness();
        var job = SdlcAgentStageHarness.Job();

        await harness.RunCallAsync(StackCall(harness, job, $"Port it. {FakeStackAnalystResponder.DjangoMarker}"), "analysis");

        var corrective = AgentRunRules.RunId(job.Id, AgentOutput.CorrectiveRunKey);
        var runs = harness.Store.AgentRuns;
        Assert.Equal([(job.Id, AgentRunStatus.Succeeded, (string?)null), (corrective, AgentRunStatus.Succeeded, null)], runs.Select(r => (r.Id, r.Status, r.Error)));
        var error = StackProfile.CheckReply(SdlcAgentRuns.ReadOutput(runs[0].Output)!.Text, new StackProfileContext(StackTestCatalog.Catalog, StackPinning.GitHubVcs, BaseFiles)).Error!;
        Assert.Equal(StackProfile.CorrectiveFeedback(error, StackTestCatalog.Catalog), runs[1].UserFeedback);
        Assert.Contains("Your previous stack profile was rejected", harness.Provider.Requests[1].Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal(
            ["RUN_STARTED", "RUN_COMPLETED", "RUN_STARTED", "RUN_COMPLETED", "ARTIFACT_CREATED", "STACK_PROFILE_CREATED"],
            harness.Data.Events.Select(e => e.Type));
        Assert.Equal((Guid?)corrective, Assert.Single(harness.Store.Artifacts).AgentRunId);
    }

    [Fact]
    public async Task FR318_stack_analyst_still_invalid_after_the_rerun_fails_with_STACK_PROFILE_INVALID_and_no_draft()
    {
        using var harness = Harness();
        var job = SdlcAgentStageHarness.Job();

        var ex = await Assert.ThrowsAsync<JobFailedException>(() =>
            harness.RunCallAsync(StackCall(harness, job, $"Port it. {FakeStackAnalystResponder.InvalidMarker}"), "analysis"));

        Assert.Equal(("STACK_PROFILE_INVALID", false), (ex.ErrorCode, ex.Transient));
        var runs = harness.Store.AgentRuns;
        Assert.Equal([AgentRunStatus.Succeeded, AgentRunStatus.Failed], runs.Select(r => r.Status));
        Assert.Equal($"STACK_PROFILE_INVALID: {ex.Message}", runs[1].Error);
        Assert.Equal(
            AideJson.Serialize(new { runId = runs[1].Id, agent = SdlcAgents.StackAnalyst, errorCode = "STACK_PROFILE_INVALID", error = runs[1].Error, attempts = 1, deadLettered = false }),
            harness.Data.Events[^1].Payload);
        Assert.Empty(harness.Store.Artifacts);
    }

    [Fact]
    public async Task FR318_FR277_an_unregistered_uncached_pinned_hash_fails_fast_with_DEFINITION_UNAVAILABLE()
    {
        using var harness = Harness();
        var run = harness.Run;
        harness.Store.Runs.Remove(run);
        harness.Store.Runs.Add(new PipelineRun
        {
            Id = run.Id,
            ProjectId = run.ProjectId,
            PipelineId = run.PipelineId,
            DefinitionHash = "sha256:nowhere",
            SubjectId = run.SubjectId,
            CurrentStage = run.CurrentStage,
            PackStatus = run.PackStatus,
        });

        var ex = await Assert.ThrowsAsync<JobFailedException>(() => harness.RunCallAsync(StackCall(harness, SdlcAgentStageHarness.Job(), "x"), "analysis"));

        Assert.Equal(("DEFINITION_UNAVAILABLE", false), (ex.ErrorCode, ex.Transient));
        Assert.Empty(harness.Provider.Requests);
    }
}
