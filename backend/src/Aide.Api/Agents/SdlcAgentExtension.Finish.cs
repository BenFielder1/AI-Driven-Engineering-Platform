using Aide.Domain.Agents;
using Aide.Domain.Common;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Pack.Sdlc;

namespace Aide.Api.Agents;

public sealed partial class SdlcAgentExtension
{
    /// <summary>FR-28, FR-29 (0.6.1): a rejected reply gets one corrective re-run, then fails the run with its code.</summary>
    private static readonly OnInvalidPolicy CorrectiveOnce = new(RetryWithFeedback: true, MaxAttempts: 2, Then: InvalidOutcome.Fail);

    /// <summary>FR-314: the stage's own policy, else 0.6.1's one corrective re-run.</summary>
    public override OnInvalidPolicy? OnInvalid(StageDefinition stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.OnInvalid ?? CorrectiveOnce;
    }

    /// <summary>FR-27–FR-29: the job's reply check (<see cref="ArtifactAgentSpec.ToContent"/>) on the reply as the provider sent it.</summary>
    public override Task<IReadOnlyList<string>> ValidateOutputAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (DefinitionChecks(attempt))
        {
            // FR-321: the stage's validate: guard runs this check (sdlc.plan-valid); checking again would repeat each reason.
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var error = Check(attempt).Error;
        return Task.FromResult<IReadOnlyList<string>>(error is null ? [] : [error.Message]);
    }

    /// <summary>0.6.1: the first, rejected run stays succeeded with no error; the corrective run's rejection is <c>CODE: message</c>.</summary>
    public override Task<string?> RejectedErrorAsync(AgentAttempt attempt, string findings, AgentRunVerdict verdict, AgentStageFailure? failure, CancellationToken cancellationToken) =>
        Task.FromResult(failure is null ? null : SdlcRunEvents.Truncate($"{failure.ErrorCode}: {failure.Message}"));

    /// <summary>FR-28, FR-29: the job fails with the check's code and message (e.g. <c>MISSING_SECTIONS</c>).</summary>
    public override Task<AgentStageFailure> AttemptsExhaustedAsync(AgentAttempt attempt, string findings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var error = Check(attempt).Error ?? new DomainError(ErrorCodes.Validation, findings);
        return Task.FromResult(new AgentStageFailure(error.Code, error.Message));
    }

    /// <summary>
    /// FR-314: the corrective re-run is the job's own (<see cref="ArtifactAgentSpec.Corrective"/>: the rejected reply and
    /// the job's feedback wording), under 0.6.1's run id (<see cref="AgentOutput.CorrectiveRunKey"/>).
    /// </summary>
    public override Task<AgentStageRequest> NextAttemptAsync(AgentStageRequest original, AgentStageRequest current, AgentStageOutcome invalid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(invalid);
        var call = current.HostState as SdlcAgentStageCall
            ?? throw new JobFailedException("The SDLC stage retry has no SDLC request.", transient: false, ErrorCodes.Validation);
        var text = ReplyOf(invalid.AgentRun);
        var error = call.Spec.ToContent(text).Error ?? new DomainError(ErrorCodes.Validation, invalid.Findings ?? string.Empty);
        if (!call.Spec.LogsRejection)
        {
            LogRejected(logger, call.Request.Agent, error.Code);
        }

        var correctiveRequest = call.Spec.Corrective(text, error);
        var corrective = new SdlcAgentStageCall(correctiveRequest with { RunKey = correctiveRequest.RunKey ?? AgentOutput.CorrectiveRunKey }, call.Spec.ForCorrective());
        var next = corrective.ToStageRequest(current.RunId, current.StageId, JobTriggers.ValidationFailed);
        return Task.FromResult(next with { Attempt = current.Attempt + 1 });
    }

    /// <summary>FR-313: the draft content is the job's (<see cref="ArtifactAgentSpec.ToContent"/>).</summary>
    public override Task<string> ToContentAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var checkedReply = Check(attempt);
        return checkedReply.IsSuccess
            ? Task.FromResult(checkedReply.Value)
            : throw new JobFailedException(checkedReply.Error!.Message, transient: false, checkedReply.Error.Code);
    }

    /// <summary>
    /// FR-27–FR-29, FR-313, step 8, the store override: the SDLC stores its draft itself, through 0.6.1's draft writer
    /// (phase check, <c>xmin</c>, superseding, events, dated by the run's completion), and returns it as the attempt's
    /// artifact. A draft the phase no longer allows is skipped (0.6.1's log; no artifact).
    /// </summary>
    public override async Task<AgentStoreOverride?> StoreOverrideAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var call = CallOf(attempt);
        var run = attempt.AgentRun!;
        if (!call.Spec.StoresDraft)
        {
            // The job decides what the accepted reply stores (FR-38: the review's optimisation plan, after scoring).
            return new AgentStoreOverride(null);
        }

        var (draft, added) = await data.AddDraftAsync(run.ProjectId, call.Spec.Type, content, run.Id, run.CompletedAt ?? run.CreatedAt, cancellationToken);
        if (draft is null)
        {
            LogDraftSkipped(logger, call.Spec.Type);
            return new AgentStoreOverride(null);
        }

        if (added && call.Spec.OnAdded is { } onAdded)
        {
            onAdded(draft, AgentRunner.Outcome(run));
        }

        SavingDraft = true;
        return new AgentStoreOverride(draft);
    }

    /// <summary>
    /// FR-321: whether the attempt's stage declares the guard that runs its job's check (the primary <c>plan_generator</c>
    /// on a definition with <c>validate: [{ guard: sdlc.plan-valid }]</c>). Runs pinned to an earlier definition keep the
    /// host check.
    /// </summary>
    private static bool DefinitionChecks(AgentAttempt attempt) =>
        !attempt.IsSecondaryAgent
        && attempt.Agent.Key == SdlcAgents.PlanGenerator
        && attempt.Stage.Validate.Any(v => v.Guard == SdlcGuardNames.PlanValid);

    /// <summary>The job's reply check on the attempt's reply.</summary>
    private static Result<string> Check(AgentAttempt attempt) => CallOf(attempt).Spec.ToContent(ReplyOf(attempt.AgentRun!));

    /// <summary>The reply as the provider sent it (0.6.1 checked the raw text, not the engine's trimmed content).</summary>
    private static string ReplyOf(AgentRun run) => AgentRunner.ReadOutput(run.Output)?.Text ?? string.Empty;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Agent} reply was rejected with {ErrorCode}; re-running once with the reasons as feedback")]
    private static partial void LogRejected(ILogger logger, string agent, string errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "No {ArtifactType} draft created: the project's phase no longer allows it")]
    private static partial void LogDraftSkipped(ILogger logger, string artifactType);
}
