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
    /// <summary>
    /// FR-160, FR-162, FR-71: the output limit (the request's, else the model's), the context window, and the repo tools
    /// over the snapshot, opened now (0.6.1: after the key check; a missing snapshot is the <c>snapshot_unavailable</c>
    /// warning). The budget the tool turns are checked against is read after the snapshot, as 0.6.1.
    /// </summary>
    public override async Task<AgentCallOptions> CallOptionsAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var request = CallOf(attempt).Request;
        var run = attempt.AgentRun!;
        var maxOutputTokens = request.MaxTokens ?? await outputLimits.ResolveAsync(run.Provider, run.Model, attempt.Llm!.ApiKey, cancellationToken);
        LogCalling(logger, run.Provider, run.Model, run.Attempts + 1, maxOutputTokens);

        var session = request.Repo == RepoScope.None ? null : await tools.OpenAsync(cancellationToken);
        int? contextTokens = ContextWindow(run) is > 0 and var window ? window : null;
        var project = await data.ProjectAsync(run.ProjectId, cancellationToken);
        _callBudget = (project?.TokenBudget, project?.TokensUsed ?? 0);
        return new AgentCallOptions(maxOutputTokens, contextTokens)
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
        LogAttemptFailed(logger, run.Attempts, failure.Transient, statusCode, failure.ErrorCode);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider attempt {Attempt} failed (transient: {Transient}, status {StatusCode}) with {ErrorCode}")]
    private static partial void LogAttemptFailed(ILogger logger, int attempt, bool transient, int? statusCode, string errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent run failed with {ErrorCode} after {Attempts} provider attempts (dead letter: {DeadLetter})")]
    private static partial void LogFailed(ILogger logger, string errorCode, int attempts, bool deadLetter);
}
