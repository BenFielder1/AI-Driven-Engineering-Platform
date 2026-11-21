using Aide.Domain.Common;
using Aide.Domain.Projects;
using Aide.Infrastructure.Persistence.Conversion;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Llm;

namespace Aide.Api.Agents;

public sealed partial class SdlcAgentExtension
{
    /// <summary>FR-160, FR-162: the output limit (the request's, else the model's) and the context window.</summary>
    public override async Task<AgentCallOptions> CallOptionsAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var request = CallOf(attempt).Request;
        var run = attempt.AgentRun!;
        var maxOutputTokens = request.MaxTokens ?? await outputLimits.ResolveAsync(run.Provider, run.Model, attempt.Llm!.ApiKey, cancellationToken);
        int? contextTokens = ContextWindow(run) is > 0 and var window ? window : null;
        return new AgentCallOptions(maxOutputTokens, contextTokens);
    }

    /// <summary>
    /// FR-71, as 0.6.1 after the run and its attempt are saved: the repo tools over the snapshot (a missing one is the
    /// <c>snapshot_unavailable</c> warning, after the request's own), then the budget the tool turns are checked against.
    /// </summary>
    public override async Task<AgentCallOptions> PrepareCallAsync(AgentAttempt attempt, AgentCallOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(options);
        var request = CallOf(attempt).Request;
        var run = attempt.AgentRun!;
        LogCalling(logger, run.Provider, run.Model, run.Attempts, options.MaxTokens ?? 0);
        var session = request.Repo == RepoScope.None ? null : await tools.OpenAsync(cancellationToken);
        var project = await data.ProjectAsync(run.ProjectId, cancellationToken);
        _callBudget = (project?.TokenBudget, project?.TokensUsed ?? 0);
        return options with
        {
            Warnings = [.. request.Warnings.Concat(tools.Warning is { } warning ? [warning] : [])],
            Tools = session,
            ToolCalls = request.Repo == RepoScope.None ? null : tools.ToolCallsJson,
        };
    }

    /// <summary>FR-26, FR-71: the budget between tool turns, as of the call's start plus this run's tokens.</summary>
    public override Task<string?> CheckToolTurnAsync(AgentAttempt attempt, LlmTurnTokens soFar, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var run = attempt.AgentRun!;
        var runTokens = (long)run.InputTokens + run.OutputTokens + soFar.InputTokens + soFar.OutputTokens;
        if (TokenBudget.CheckCanContinue(_callBudget.TokenBudget, _callBudget.TokensUsed, runTokens).IsSuccess)
        {
            return Task.FromResult<string?>(null);
        }

        var used = _callBudget.TokensUsed + runTokens;
        return Task.FromResult<string?>(TokenBudget.CheckCanStart(_callBudget.TokenBudget, used).Error?.Message ?? "The project's token budget is used up.");
    }

    /// <summary>
    /// FR-78: a rejected bound key invalidates the connection and fails the run with <c>CREDENTIAL_INVALID</c>. FR-25:
    /// otherwise a transient failure with job attempts left keeps the run running; the last attempt fails it dead-lettered.
    /// </summary>
    public override async Task<AgentRunFailureDecision> OnCallFailedAsync(AgentAttempt attempt, AgentStageFailure failure, int? statusCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(failure);
        var run = attempt.AgentRun!;
        LogAttempt(run, failure, statusCode);
        if (LlmConnectionRules.RunFailureInvalidatesKey(statusCode) && run.CredentialId is { } credentialId)
        {
            var rejected = $"The bound {run.Provider.ToWire()} connection's key was rejected (HTTP {statusCode}); its owner must replace it.";
            await data.InvalidateCredentialAsync(run, credentialId);
            _deadLetter = false;
            return new AgentRunFailureDecision(new AgentStageFailure(ErrorCodes.CredentialInvalid, rejected));
        }

        var outcome = AgentRunRules.OnProviderFailure(failure.Transient, CallOf(attempt).Request.Job.Attempt);
        _deadLetter = outcome.DeadLetter;
        return new AgentRunFailureDecision(failure, KeepRunning: outcome.RunStatus == AgentRunStatus.Running);
    }

    /// <summary>FR-311: <c>RUN_FAILED</c> of a run that failed before or during the call (0.6.1's fields and time).</summary>
    public override Task OnRunFailedAsync(AgentAttempt attempt, AgentStageFailure failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(failure);
        var run = attempt.AgentRun!;
        var now = time.GetUtcNow();
        SdlcRunEvents.Fail(run, failure.ErrorCode, failure.Message, now);
        data.AddEvent(EventTypes.RunFailed, run.ProjectId, SdlcRunEvents.RunFailed(run, failure.ErrorCode, _deadLetter), now);
        LogFailed(logger, failure.ErrorCode, run.Attempts, _deadLetter);
        return Task.CompletedTask;
    }

    /// <summary>FR-312: 0.6.1's <c>output</c> JSON (<see cref="RunOutput"/> in <see cref="AideJson"/>).</summary>
    public override string SerializeOutput(AgentAttempt attempt, AgentStageRunOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return AideJson.Serialize(new RunOutput(output.Text, output.StopReason)
        {
            Warnings = output.Warnings,
            MaxOutputTokens = output.MaxOutputTokens,
            Continuations = output.Continuations,
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Calling {Provider} model {Model} (attempt {Attempt}, max output tokens {MaxOutputTokens})")]
    private static partial void LogCalling(ILogger logger, LlmProvider provider, string model, int attempt, int maxOutputTokens);

    /// <summary>0.6.1's failed-attempt logs: a provider error with its (already redacted) reason, else the exception type and its redacted text.</summary>
    private void LogAttempt(AgentRun run, AgentStageFailure failure, int? statusCode)
    {
        if (failure.ExceptionType is { } exceptionType)
        {
            LogUnexpectedFailure(logger, run.Attempts, failure.Transient, exceptionType, failure.ExceptionDetail ?? string.Empty);
            return;
        }

        LogAttemptFailed(logger, run.Attempts, failure.Transient, statusCode, failure.Message);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider attempt {Attempt} failed (transient: {Transient}, status {StatusCode}): {Reason}")]
    private static partial void LogAttemptFailed(ILogger logger, int attempt, bool transient, int? statusCode, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider attempt {Attempt} failed unexpectedly (transient: {Transient}) with {ExceptionType}: {Reason}")]
    private static partial void LogUnexpectedFailure(ILogger logger, int attempt, bool transient, string exceptionType, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent run failed with {ErrorCode} after {Attempts} provider attempts (dead letter: {DeadLetter})")]
    private static partial void LogFailed(ILogger logger, string errorCode, int attempts, bool deadLetter);
}
