using System.Globalization;
using Aide.Api.Jobs;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Jobs;

namespace Aide.Api.Agents;

/// <summary>
/// FR-27 (and the FR-17/FR-20 re-runs of the project request): one idea refinement round. Inputs: the project name and
/// idea, the draft to improve (the rejected draft on an artifact rejection, else the latest intact version), the answers
/// given for this round and any reviewer feedback. The reply (<c>{"projectRequest", "questions"}</c>) becomes a new
/// <c>project_request</c> draft; the questions stay in the run's output and the answers in its <c>user_feedback</c>
/// (and the job payload), where <c>GET /refinement</c> reads them. An unreadable reply is re-run once, then fails the run
/// with <c>INVALID_OUTPUT</c>.
/// </summary>
public sealed class IdeaRefinementJobHandler(ArtifactAgentJob agentJob, AgentJobInputs inputs, ILogger<IdeaRefinementJobHandler> logger) : IJobHandler
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

        var draft = await inputs.PreviousAsync(payload, ArtifactType.ProjectRequest, cancellationToken)
            ?? await inputs.LatestIntactAsync(project.Id, ArtifactType.ProjectRequest, cancellationToken);
        var answers = RefinementRules.FormatAnswers(payload.Answers);
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
                [AgentRunner.FeedbackInput] = feedback,
            },
            InputArtifacts = draft is null ? [] : [AgentJobInputs.Ref(draft)],
            UserFeedback = RefinementRules.RunFeedback(payload.Answers, feedback),
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
/// given to revise. A reply missing any of the eleven sections is re-run once with the missing section names as feedback;
/// still missing → the run fails with <c>MISSING_SECTIONS</c> and no draft is created.
/// </summary>
public sealed class SpecGenerationJobHandler(ArtifactAgentJob agentJob, AgentJobInputs inputs, ILogger<SpecGenerationJobHandler> logger) : IJobHandler
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
        AgentRunRequest Request(string? previousSpec, string? feedback) => new()
        {
            ProjectId = project.Id,
            Agent = AgentKind.SpecGenerator,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_REQUEST"] = projectRequest?.Content,
                ["PREVIOUS_SPECIFICATION"] = previousSpec,
            },
            InputArtifacts = [.. new[] { projectRequest, previous }.OfType<Artifact>().Select(AgentJobInputs.Ref)],
            UserFeedback = feedback,
        };

        await agentJob.RunAsync(
            Request(previous?.Content, payload.Feedback),
            new ArtifactAgentSpec(
                ArtifactType.TechnicalSpecification,
                text =>
                {
                    var spec = AgentOutput.UnwrapFence(text);
                    var check = SpecSections.Check(spec);
                    return check.IsSuccess ? Result.Success(spec) : Result.Failure<string>(check.Error!);
                },
                (rejected, error) => Request(
                    AgentOutput.UnwrapFence(rejected),
                    ArtifactAgentJob.JoinFeedback(payload.Feedback, SpecSections.CorrectiveFeedback(error.Details)))),
            cancellationToken);
    }
}

/// <summary>
/// FR-29 (with the FR-13/FR-17/FR-20 re-runs): generates the implementation plan from the current approved project request
/// and specification. The reply is structured JSON, read and validated by the plan rules (<see cref="PlanValidator"/>, with
/// the project's max files per step, and no protected paths: a generated plan may not declare them, §4.6); the draft is the
/// plan rendered by <see cref="PlanDocument.RenderChecked"/> (Markdown plus the canonical <c>```aide-plan</c> block, proven
/// to read back to the same steps). An invalid plan is re-run once with every reason as feedback; still invalid → the run
/// fails with <c>INVALID_PLAN</c> and no draft is created.
/// </summary>
public sealed class PlanGenerationJobHandler(ArtifactAgentJob agentJob, AgentJobInputs inputs, ILogger<PlanGenerationJobHandler> logger) : IJobHandler
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
        var maxFiles = project.MaxFilesPerStep;
        AgentRunRequest Request(string? previousPlan, string? feedback) => new()
        {
            ProjectId = project.Id,
            Agent = AgentKind.PlanGenerator,
            Job = job,
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_REQUEST"] = projectRequest?.Content,
                ["TECHNICAL_SPECIFICATION"] = spec?.Content,
                ["PREVIOUS_PLAN"] = previousPlan,
                ["MAX_FILES_PER_STEP"] = maxFiles.ToString(CultureInfo.InvariantCulture),
            },
            InputArtifacts = [.. new[] { projectRequest, spec, previous }.OfType<Artifact>().Select(AgentJobInputs.Ref)],
            UserFeedback = feedback,
        };

        await agentJob.RunAsync(
            Request(previous is null ? null : PreviousPlanJson(previous.Content), payload.Feedback),
            new ArtifactAgentSpec(
                ArtifactType.ImplementationPlan,
                text =>
                {
                    var plan = PlanDocument.ParseAgentOutput(text, maxFiles);
                    return plan.IsSuccess
                        ? PlanDocument.RenderChecked(ArtifactType.ImplementationPlan, plan.Value, maxFiles)
                        : Result.Failure<string>(plan.Error!);
                },
                (rejected, error) => Request(
                    AgentOutput.ExtractJsonObject(rejected) ?? rejected,
                    ArtifactAgentJob.JoinFeedback(payload.Feedback, PlanDocument.CorrectiveFeedback(error)))),
            cancellationToken);
    }

    /// <summary>A stored plan as the agent's JSON (the canonical block), or the Markdown when it has no valid block.</summary>
    private static string PreviousPlanJson(string markdown)
    {
        var parsed = PlanDocument.ParseMarkdown(markdown, maxFilesPerStep: null);
        return parsed.IsSuccess ? PlanDocument.ToJson(parsed.Value) : markdown;
    }
}

public static class AgentJobServiceCollectionExtensions
{
    /// <summary>The refinement, spec and plan job handlers (FR-27–FR-29) and what they share.</summary>
    public static IServiceCollection AddAgentJobs(this IServiceCollection services)
    {
        services.AddScoped<AgentJobInputs>();
        services.AddScoped<ArtifactAgentJob>();
        services.AddJobHandler<IdeaRefinementJobHandler>(JobTypes.IdeaRefinement);
        services.AddJobHandler<SpecGenerationJobHandler>(JobTypes.SpecGeneration);
        services.AddJobHandler<PlanGenerationJobHandler>(JobTypes.PlanGeneration);
        return services;
    }
}
