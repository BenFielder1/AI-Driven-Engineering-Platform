using System.Globalization;
using Aide.Api.Features.Presets;
using Aide.Api.Jobs;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Jobs;
using Aide.Domain.Presets;
using Aide.Domain.Specs;
using Aide.Domain.Stack;

namespace Aide.Api.Agents;

/// <summary>
/// FR-27 (and the FR-17/FR-20 re-runs of the project request): one idea refinement round. Inputs: the project name and
/// idea, the draft to improve (the rejected draft on an artifact rejection, else the latest intact version), the answers
/// given for this round and any reviewer feedback. The reply (<c>{"projectRequest", "questions"}</c>) becomes a new
/// <c>project_request</c> draft; the questions stay in the run's output and the answers in its <c>user_feedback</c>
/// (and the job payload), where <c>GET /refinement</c> reads them. An unreadable reply is re-run once, then fails the run
/// with <c>INVALID_OUTPUT</c>.
/// <para>
/// FR-70, FR-72: in a version with existing code the agent also gets the codebase summary, the previous released
/// version's locked project request and spec (all listed as input artifacts) and the repo tools at the base commit. If
/// the summary doesn't exist yet the job waits for it (<see cref="SummaryWaiter"/>), then continues without it.
/// </para>
/// </summary>
public sealed class IdeaRefinementJobHandler(
    ArtifactAgentJob agentJob,
    AgentJobInputs inputs,
    IterationContextReader iterations,
    VersionPresetReader presets,
    TimeProvider time,
    ILogger<IdeaRefinementJobHandler> logger) : IJobHandler
{
    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = AgentJobInputs.Payload(job);
        var project = await inputs.ProjectAsync(payload.ProjectId, cancellationToken);
        if (project is null || !AgentJobInputs.ShouldRun(payload, ArtifactType.ProjectRequest, project))
        {
            AgentJobLog.Skipped(logger, job.Type, payload.Trigger, project?.CurrentPhase);
            return;
        }

        var iteration = await iterations.ReadAsync(project.Id, cancellationToken);
        var waitWarning = await SummaryWaiter.WaitAsync(iterations, iteration, project.Id, time, cancellationToken);
        var draft = await inputs.PreviousAsync(payload, ArtifactType.ProjectRequest, cancellationToken)
            ?? await inputs.LatestIntactAsync(project.Id, ArtifactType.ProjectRequest, cancellationToken);
        var answers = RefinementRules.FormatAnswers(payload.Answers);

        // FR-128: the composition and, for a version with existing code, the detector's evidence and mismatches.
        var stack = (await presets.CurrentAsync(project.Id, cancellationToken, detect: iteration.HasExistingCode)).ThrowIfUnresolvable();
        AgentRunRequest Request(string? feedback) => new()
        {
            ProjectId = project.Id,
            Agent = AgentKind.IdeaRefinement,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_NAME"] = project.Name,
                ["IDEA"] = project.Idea,
                ["CURRENT_DRAFT"] = draft?.Content,
                ["ANSWERS"] = answers,
                ["CODEBASE_SUMMARY"] = iteration.CodebaseSummary?.Content,
                ["PREVIOUS_PROJECT_REQUEST"] = iteration.BaselineProjectRequest?.Content,
                ["PREVIOUS_SPECIFICATION"] = iteration.BaselineSpecification?.Content,
                ["STACK_GUIDANCE"] = stack.Stack is { } resolution ? StackGuidance.Refinement(resolution, stack.Detection) : null,
                ["STACK_DETECTION"] = StackGuidance.DetectionInput(stack.Detection),
                [AgentRunner.FeedbackInput] = feedback,
            },
            Stack = stack.Stack,
            InputArtifacts = [.. new[] { draft }.OfType<Artifact>().Concat(iteration.Artifacts).Select(AgentJobInputs.Ref)],
            UserFeedback = RefinementRules.RunFeedback(payload.Answers, feedback),
            Repo = RepoScope.BaseCommit,
            Warnings = waitWarning is null ? [] : [waitWarning],
        };

        await agentJob.RunAsync(
            Request(payload.Feedback),
            new ArtifactAgentSpec(
                ArtifactType.ProjectRequest,
                text =>
                {
                    var parsed = RefinementRules.Parse(text);
                    return parsed.IsSuccess ? Result.Success(parsed.Value.ProjectRequest) : Result.Failure<string>(parsed.Error!);
                },
                (_, error) => Request(ArtifactAgentJob.JoinFeedback(
                    payload.Feedback,
                    $"Your previous reply was rejected: {error.Message} Respond with only the JSON object described above."))),
            cancellationToken);
    }
}

/// <summary>
/// FR-28 (with the FR-13/FR-17/FR-20 re-runs): generates the technical specification from the current approved project
/// request (none → the run fails with <c>MISSING_INPUT</c>). On a rejection re-run the rejected version and the comment are
/// given to revise. The reply is validated by <see cref="SpecValidator"/> (FR-75, §4.13) in the version's context
/// (<see cref="IterationContext.SpecContext"/>: first version, new version against the baseline spec's ids, or an import's
/// first version); a failure (<c>MISSING_SECTIONS</c>, <c>MISSING_REQUIREMENT_IDS</c>, <c>INVALID_REQUIREMENT_TAGS</c>) is
/// re-run once with every problem as feedback; still failing → the run fails with that code and no draft is created.
/// Iterations also get the codebase summary, the baseline spec and the repo tools at the base commit. FR-75: with a preset
/// the prompt gets its stack description, extra sections with guidance and conventions (<see cref="PresetGuidance.Spec"/>),
/// and validation requires those sections (a missing one is named in the corrective re-run).
/// </summary>
public sealed class SpecGenerationJobHandler(
    ArtifactAgentJob agentJob,
    AgentJobInputs inputs,
    IterationContextReader iterations,
    VersionPresetReader presets,
    ILogger<SpecGenerationJobHandler> logger) : IJobHandler
{
    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = AgentJobInputs.Payload(job);
        var project = await inputs.ProjectAsync(payload.ProjectId, cancellationToken);
        if (project is null || !AgentJobInputs.ShouldRun(payload, ArtifactType.TechnicalSpecification, project))
        {
            AgentJobLog.Skipped(logger, job.Type, payload.Trigger, project?.CurrentPhase);
            return;
        }

        var projectRequest = await inputs.CurrentAsync(project.Id, ArtifactType.ProjectRequest, cancellationToken);
        var previous = await inputs.PreviousAsync(payload, ArtifactType.TechnicalSpecification, cancellationToken);
        var iteration = await iterations.ReadAsync(project.Id, cancellationToken);

        // FR-123, FR-128: with a composition its required sections (with "Stack Migration" for a stack change, or when the
        // imported code doesn't match it) replace the preset's.
        var stack = (await presets.CurrentAsync(project.Id, cancellationToken, detect: iteration.HasExistingCode)).ThrowIfUnresolvable();
        var mismatches = stack.Detection?.Mismatches;
        if (stack.Stack is { } composed)
        {
            iteration = iteration with { StackSections = [.. StackGuidance.SpecSections(composed, mismatches).Select(s => s.Title)] };
        }

        var context = iteration.SpecContext;
        var guidance = stack.Stack is { } resolution ? StackGuidance.Spec(resolution, mismatches) : PresetGuidance.Spec(iteration.Preset);
        AgentRunRequest Request(string? previousSpec, string? feedback) => new()
        {
            ProjectId = project.Id,
            Agent = AgentKind.SpecGenerator,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_REQUEST"] = projectRequest?.Content,
                ["SPEC_MODE"] = context.Mode,
                ["BASELINE_SPECIFICATION"] = iteration.BaselineSpecification?.Content,
                ["CODEBASE_SUMMARY"] = iteration.CodebaseSummary?.Content,
                ["PREVIOUS_SPECIFICATION"] = previousSpec,
                ["PRESET_GUIDANCE"] = guidance,
            },
            Stack = stack.Stack,
            InputArtifacts = [.. new[] { projectRequest, previous, iteration.CodebaseSummary, iteration.BaselineSpecification }.OfType<Artifact>().Select(AgentJobInputs.Ref)],
            UserFeedback = feedback,
            Repo = RepoScope.BaseCommit,
        };

        await agentJob.RunAsync(
            Request(previous?.Content, payload.Feedback),
            new ArtifactAgentSpec(
                ArtifactType.TechnicalSpecification,
                text => SpecValidator.Check(AgentOutput.UnwrapFence(text), context),
                (rejected, error) => Request(
                    AgentOutput.UnwrapFence(rejected),
                    ArtifactAgentJob.JoinFeedback(payload.Feedback, SpecValidator.CorrectiveFeedback(error, context)))),
            cancellationToken);
    }
}

/// <summary>
/// FR-29 (with the FR-13/FR-17/FR-20 re-runs): generates the implementation plan from the current approved project request
/// and specification. The reply is structured JSON, read and validated by the plan rules (<see cref="PlanValidator"/>, with
/// the project's max files per step, and no protected paths: a generated plan may not declare them, §4.6); the draft is the
/// plan rendered by <see cref="PlanDocument.RenderChecked"/> (Markdown plus the canonical <c>```aide-plan</c> block, proven
/// to read back to the same steps). An invalid plan is re-run once with every reason as feedback; still invalid → the run
/// fails with <c>INVALID_PLAN</c> and no draft is created. Steps carry <c>agent</c> and <c>covers</c> (FR-76); an
/// iteration plan that leaves a <c>[new]</c>/<c>[changed]</c> requirement uncovered is a warning, shown with the plan
/// (<see cref="PlanScope"/>, §4.13). Iterations also get the codebase summary and the repo tools at the base commit.
/// FR-76: with a preset the prompt gets its agent catalogue and plan rules (<see cref="PresetGuidance.Plan"/>), and the plan
/// is validated with them (<see cref="PlanAgents.For"/>: catalogue keys, ordering/exclusive/file-limit rules, scaffold first
/// on an empty repository, the protected-path ownership exception); its warnings are shown with the draft.
/// </summary>
public sealed class PlanGenerationJobHandler(
    ArtifactAgentJob agentJob,
    AgentJobInputs inputs,
    IterationContextReader iterations,
    VersionPresetReader presets,
    ILogger<PlanGenerationJobHandler> logger) : IJobHandler
{
    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = AgentJobInputs.Payload(job);
        var project = await inputs.ProjectAsync(payload.ProjectId, cancellationToken);
        if (project is null || !AgentJobInputs.ShouldRun(payload, ArtifactType.ImplementationPlan, project))
        {
            AgentJobLog.Skipped(logger, job.Type, payload.Trigger, project?.CurrentPhase);
            return;
        }

        var projectRequest = await inputs.CurrentAsync(project.Id, ArtifactType.ProjectRequest, cancellationToken);
        var spec = await inputs.CurrentAsync(project.Id, ArtifactType.TechnicalSpecification, cancellationToken);
        var previous = await inputs.PreviousAsync(payload, ArtifactType.ImplementationPlan, cancellationToken);
        var summary = (await iterations.ReadAsync(project.Id, cancellationToken)).CodebaseSummary;
        var maxFiles = project.MaxFilesPerStep;
        var preset = (await presets.CurrentAsync(project.Id, cancellationToken)).ThrowIfUnresolvable();
        var agents = preset.PlanAgents(ArtifactType.ImplementationPlan);

        // FR-124: the composition's specialists and composed rules, else the preset's (FR-76), else none (generalist).
        var guidance = preset.Stack is { } stack ? StackGuidance.Plan(stack, preset.RepoEmpty) : PresetGuidance.Plan(preset.Preset, preset.RepoEmpty);
        AgentRunRequest Request(string? previousPlan, string? feedback) => new()
        {
            ProjectId = project.Id,
            Agent = AgentKind.PlanGenerator,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_REQUEST"] = projectRequest?.Content,
                ["TECHNICAL_SPECIFICATION"] = spec?.Content,
                ["CODEBASE_SUMMARY"] = summary?.Content,
                ["PREVIOUS_PLAN"] = previousPlan,
                ["MAX_FILES_PER_STEP"] = maxFiles.ToString(CultureInfo.InvariantCulture),
                ["PRESET_GUIDANCE"] = guidance,
            },
            Stack = preset.Stack,
            InputArtifacts = [.. new[] { projectRequest, spec, previous, summary }.OfType<Artifact>().Select(AgentJobInputs.Ref)],
            UserFeedback = feedback,
            Repo = RepoScope.BaseCommit,
        };

        await agentJob.RunAsync(
            Request(previous is null ? null : PreviousPlanJson(previous.Content, agents), payload.Feedback),
            new ArtifactAgentSpec(
                ArtifactType.ImplementationPlan,
                text =>
                {
                    var plan = PlanDocument.ParseAgentOutput(text, maxFiles, agents);
                    return plan.IsSuccess
                        ? PlanDocument.RenderChecked(ArtifactType.ImplementationPlan, plan.Value, maxFiles, agents)
                        : Result.Failure<string>(plan.Error!);
                },
                (rejected, error) => Request(
                    AgentOutput.ExtractJsonObject(rejected) ?? rejected,
                    ArtifactAgentJob.JoinFeedback(payload.Feedback, PlanDocument.CorrectiveFeedback(error)))),
            cancellationToken);
    }

    /// <summary>A stored plan as the agent's JSON (the canonical block), or the Markdown when it has no valid block.</summary>
    private static string PreviousPlanJson(string markdown, PlanAgents agents)
    {
        var parsed = PlanDocument.ParseMarkdown(markdown, maxFilesPerStep: null, agents);
        return parsed.IsSuccess ? PlanDocument.ToJson(parsed.Value) : markdown;
    }
}

/// <summary>
/// FR-70: a refinement run in a version with existing code waits for the Repository Analyst's summary: the job is
/// deferred (<see cref="JobDeferredException"/>, re-queued without counting an attempt) while the analyst is queued or
/// running, up to 10 minutes after the version started (<see cref="CodebaseSummaryWait"/>); then the run goes on
/// without it and records the warning.
/// </summary>
internal static class SummaryWaiter
{
    public const string WaitingMessage = "Waiting for the Repository Analyst's codebase summary (FR-70).";

    /// <summary>The warning to record (<c>null</c>: none), or throws <see cref="JobDeferredException"/> to wait.</summary>
    public static async Task<string?> WaitAsync(IterationContextReader reader, IterationContext iteration, Guid projectId, TimeProvider time, CancellationToken cancellationToken)
    {
        if (iteration.Version is not { } version || !iteration.HasExistingCode || iteration.CodebaseSummary is not null)
        {
            return null;
        }

        var state = await reader.AnalysisStateAsync(projectId, version.Id, cancellationToken);
        var decision = CodebaseSummaryWait.Decide(hasExistingCode: true, summaryExists: false, state, version.CreatedAt, time.GetUtcNow());
        return decision.Wait ? throw new JobDeferredException(WaitingMessage, decision.RetryAt!.Value) : decision.Warning;
    }
}

public static class AgentJobServiceCollectionExtensions
{
    /// <summary>The refinement, spec and plan job handlers (FR-27–FR-29) and what they share.</summary>
    public static IServiceCollection AddAgentJobs(this IServiceCollection services)
    {
        services.AddScoped<AgentJobInputs>();
        services.AddScoped<IterationContextReader>();
        services.AddScoped<ArtifactAgentJob>();
        services.AddJobHandler<IdeaRefinementJobHandler>(JobTypes.IdeaRefinement);
        services.AddJobHandler<SpecGenerationJobHandler>(JobTypes.SpecGeneration);
        services.AddJobHandler<PlanGenerationJobHandler>(JobTypes.PlanGeneration);
        return services;
    }
}
