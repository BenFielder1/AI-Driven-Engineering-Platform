using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Tests.Agents;

/// <summary>
/// An extension of the fixture pipeline that records every hook call into <see cref="Calls"/> (shared with
/// <see cref="RecordingHooks.Calls"/> for one ordered list) and does what its properties say.
/// </summary>
internal sealed class RecordingExtension(List<string> calls, bool separate = false) : AgentStageExtension
{
    public List<string> Calls { get; set; } = calls;

    public override string PipelineId => "fixture-review";

    public override AgentRunPolicy Policy => separate ? AgentRunPolicy.Separate : AgentRunPolicy.Default;

    public AgentStageFailure? RefuseBeforeRun { get; set; }

    public AgentStageFailure? RefuseBeforeRender { get; set; }

    public AgentStageFailure? RefuseBeforeCall { get; set; }

    public Func<string, AgentRunVerdict, AgentStageFailure?, string?>? RejectedError { get; set; }

    public bool Veto { get; set; }

    /// <summary>The content transform observes its cancellation token (as a real store would).</summary>
    public bool ObserveStoreCancellation { get; set; }

    public Func<AgentStageRunOutput, string>? Serialize { get; set; }

    /// <summary>The failure each Answered commit carried, and whether any commit saw a cancelled token.</summary>
    public List<AgentStageFailure?> AnsweredFailures { get; } = [];

    public bool CommitSawCancellation { get; private set; }

    public AgentCallOptions Options { get; set; } = AgentCallOptions.None;

    public AgentRunMetadata Metadata { get; set; } = AgentRunMetadata.None;

    public Func<string, string>? Transform { get; set; }

    public bool KeepRunningOnTransient { get; set; }

    /// <summary>Asks to keep the run running whatever the failure (FR-315: the engine honours it only when transient).</summary>
    public bool ForceKeepRunning { get; set; }

    public AgentStageFailure? RewriteFailure { get; set; }

    public string? StopToolTurn { get; set; }

    public Func<AgentStageRequest, AgentStageOutcome, AgentStageRequest>? Retry { get; set; }

    public AgentStageFailure? Exhausted { get; set; }

    /// <summary>The run's status at each commit, with the point and verdict.</summary>
    public List<(AgentRunCommitPoint Point, AgentRunVerdict Verdict, AgentRunStatus Status)> Commits { get; } = [];

    public List<AgentAttempt> Attempts { get; } = [];

    public override Task<AgentStageFailure?> CheckBeforeRunAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        Attempts.Add(attempt);
        Calls.Add("pre-run");
        return Task.FromResult(RefuseBeforeRun);
    }

    public override Task OnBudgetExceededAsync(AgentAttempt attempt, string message, CancellationToken cancellationToken)
    {
        Calls.Add(attempt.AgentRun is null ? "budget-exceeded" : "budget-exceeded:in-run");
        return Task.CompletedTask;
    }

    public override Task<AgentRunMetadata> DescribeRunAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        Calls.Add("describe");
        return Task.FromResult(Metadata);
    }

    public override Task OnRunStartedAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        Calls.Add(attempt.Resumed ? "started:resumed" : "started");
        return Task.CompletedTask;
    }

    public override Task<AgentStageFailure?> CheckBeforeRenderAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        Calls.Add("pre-render");
        return Task.FromResult(RefuseBeforeRender);
    }

    public override async Task<string?> RejectedErrorAsync(AgentAttempt attempt, string findings, AgentRunVerdict verdict, AgentStageFailure? failure, CancellationToken cancellationToken) =>
        RejectedError is null ? await base.RejectedErrorAsync(attempt, findings, verdict, failure, cancellationToken) : RejectedError(findings, verdict, failure);

    public override Task<bool> ShouldStoreAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken)
    {
        Calls.Add("should-store");
        return Task.FromResult(!Veto);
    }

    public override string SerializeOutput(AgentAttempt attempt, AgentStageRunOutput output) =>
        Serialize is null ? base.SerializeOutput(attempt, output) : Serialize(output);

    public override Task<AgentStageFailure?> CheckBeforeCallAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        Calls.Add("pre-call");
        return Task.FromResult(RefuseBeforeCall);
    }

    public override Task<AgentCallOptions> CallOptionsAsync(AgentAttempt attempt, CancellationToken cancellationToken)
    {
        Calls.Add("options");
        return Task.FromResult(Options);
    }

    public override Task<string?> CheckToolTurnAsync(AgentAttempt attempt, LlmTurnTokens soFar, CancellationToken cancellationToken) =>
        Task.FromResult(StopToolTurn);

    public override Task<AgentRunFailureDecision> OnCallFailedAsync(AgentAttempt attempt, AgentStageFailure failure, int? statusCode, CancellationToken cancellationToken)
    {
        Calls.Add($"call-failed:{failure.ErrorCode}");
        var decided = RewriteFailure ?? failure;
        return Task.FromResult(new AgentRunFailureDecision(decided, KeepRunning: ForceKeepRunning || (KeepRunningOnTransient && decided.Transient)));
    }

    public override Task CommitRunAsync(AgentAttempt attempt, AgentRunCommit commit, CancellationToken cancellationToken)
    {
        Calls.Add($"commit:{commit.Point}:{commit.Verdict}");
        Commits.Add((commit.Point, commit.Verdict, attempt.AgentRun!.Status));
        CommitSawCancellation |= cancellationToken.IsCancellationRequested;
        if (commit.Point == AgentRunCommitPoint.Answered)
        {
            AnsweredFailures.Add(commit.Failure);
        }

        return base.CommitRunAsync(attempt, commit, cancellationToken);
    }

    public override async Task<AgentStageRequest> NextAttemptAsync(AgentStageRequest original, AgentStageRequest current, AgentStageOutcome invalid, CancellationToken cancellationToken)
    {
        Calls.Add("next-attempt");
        return Retry is null ? await base.NextAttemptAsync(original, current, invalid, cancellationToken) : Retry(current, invalid);
    }

    public override async Task<AgentStageFailure> AttemptsExhaustedAsync(AgentAttempt attempt, string findings, CancellationToken cancellationToken)
    {
        Calls.Add("exhausted");
        return Exhausted ?? await base.AttemptsExhaustedAsync(attempt, findings, cancellationToken);
    }

    public override Task<string> ToContentAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken)
    {
        Calls.Add("transform");
        if (ObserveStoreCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Task.FromResult(Transform is null ? content : Transform(content));
    }

    public override Task OnArtifactStoredAsync(AgentAttempt attempt, Artifact artifact, CancellationToken cancellationToken)
    {
        Calls.Add($"stored:{artifact.Type}");
        return Task.CompletedTask;
    }

    public override Task OnRunFailedAsync(AgentAttempt attempt, AgentStageFailure failure, CancellationToken cancellationToken)
    {
        Calls.Add($"failed:{failure.ErrorCode}");
        return Task.CompletedTask;
    }
}

/// <summary>A provider that fails its first <paramref name="failures"/> calls, then answers; records every request.</summary>
internal sealed class FlakyProvider(string answer, int failures = 0, bool transient = true, int status = 503, Action? onAnswer = null) : ILlmProvider
{
    public LlmProvider Kind => LlmProvider.Fake;

    public List<LlmRequest> Requests { get; } = [];

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Requests.Count <= failures)
        {
            throw new LlmProviderException($"HTTP {status} from the provider", transient, status);
        }

        onAnswer?.Invoke();
        return Task.FromResult(new LlmCompletion(answer, 10, 5, "end_turn"));
    }
}

/// <summary>A provider that answers with <paramref name="answers"/> in order (the last repeats); records every request.</summary>
internal sealed class SequenceProvider(params string[] answers) : ILlmProvider
{
    public LlmProvider Kind => LlmProvider.Fake;

    public List<LlmRequest> Requests { get; } = [];

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new LlmCompletion(answers[Math.Min(Requests.Count, answers.Length) - 1], 10, 5, "end_turn"));
    }
}
