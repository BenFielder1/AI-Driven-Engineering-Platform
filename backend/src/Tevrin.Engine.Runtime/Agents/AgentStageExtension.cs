using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// FR-310–FR-316: a pack's or host's extension points around one attempt of an agent stage of pipeline
/// <see cref="PipelineId"/>, called in §4.60's order:
/// <list type="number">
/// <item><see cref="CheckBeforeRunAsync"/> (refuse before any run exists).</item>
/// <item>The budget (<see cref="IAgentStageHooks.CheckBudgetAsync"/>, new runs only); a refusal reaches <see cref="OnBudgetExceededAsync"/>.</item>
/// <item>Create the run (<see cref="DescribeRunAsync"/>) or load it by the caller's id; a run that already succeeded returns its stored output.</item>
/// <item><see cref="OnRunStartedAsync"/>.</item>
/// <item><see cref="CheckBeforeRenderAsync"/>; inputs, context and render (a retry's input comes from
/// <see cref="NextAttemptAsync"/>); <see cref="CheckBeforeCallAsync"/>; then the engine's key check.</item>
/// <item>The call with <see cref="CallOptionsAsync"/> (<see cref="CheckToolTurnAsync"/> between tool turns); a failure goes to <see cref="OnCallFailedAsync"/>.</item>
/// <item>Validate (<see cref="RejectedErrorAsync"/>, and <see cref="AttemptsExhaustedAsync"/> when attempts run out); every
/// answered attempt is committed through <see cref="CommitRunAsync"/> with its verdict: the host's run completed or run
/// failed event goes there.</item>
/// <item><see cref="ToContentAsync"/>, <see cref="ShouldStoreAsync"/>, store, <see cref="OnArtifactStoredAsync"/>.</item>
/// </list>
/// A permanent failure outside the step-7 commit calls <see cref="OnRunFailedAsync"/>. From the model's answer to the
/// step-7 commit, hooks get <see cref="CancellationToken.None"/> (the call was paid for). Every default keeps the engine's own behaviour, so a pipeline
/// without an extension runs as before. Changes a hook makes to the shared unit of work are saved with the run.
/// Register with <see cref="AgentStageServiceCollectionExtensions.AddAgentStageExtension{T}"/> (scoped).
/// </summary>
public abstract class AgentStageExtension
{
    /// <summary>The engine's behaviour, for pipelines without an extension.</summary>
    public static AgentStageExtension Default { get; } = new DefaultExtension();

    /// <summary>The pipeline whose agent stages this extends.</summary>
    public abstract string PipelineId { get; }

    /// <summary>FR-315: how the run is saved (default: with the stage's other changes).</summary>
    public virtual AgentRunPolicy Policy => AgentRunPolicy.Default;

    /// <summary>FR-316, step 1: a refusal fails the job with its code before any run is created or the model called.</summary>
    public virtual Task<AgentStageFailure?> CheckBeforeRunAsync(AgentAttempt attempt, CancellationToken cancellationToken) =>
        Task.FromResult<AgentStageFailure?>(null);

    /// <summary>FR-311, FR-26: the budget refused the run (step 2, or between tool turns when <see cref="AgentAttempt.AgentRun"/> is set).</summary>
    public virtual Task OnBudgetExceededAsync(AgentAttempt attempt, string message, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>FR-312, step 3: what a new run records besides the engine's fields.</summary>
    public virtual Task<AgentRunMetadata> DescribeRunAsync(AgentAttempt attempt, CancellationToken cancellationToken) =>
        Task.FromResult(AgentRunMetadata.None);

    /// <summary>FR-311, step 4: the run started (<see cref="AgentAttempt.Resumed"/>: a run left <c>running</c> continues).</summary>
    public virtual Task OnRunStartedAsync(AgentAttempt attempt, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>FR-316, start of step 5 (before inputs and render): a refusal fails the run (and the job) with its code.</summary>
    public virtual Task<AgentStageFailure?> CheckBeforeRenderAsync(AgentAttempt attempt, CancellationToken cancellationToken) =>
        Task.FromResult<AgentStageFailure?>(null);

    /// <summary>FR-316, step 5 after render and before the engine's key check: a refusal fails the run (and the job) with its code.</summary>
    public virtual Task<AgentStageFailure?> CheckBeforeCallAsync(AgentAttempt attempt, CancellationToken cancellationToken) =>
        Task.FromResult<AgentStageFailure?>(null);

    /// <summary>FR-312, step 6: the per-call output token limit and context window.</summary>
    public virtual Task<AgentCallOptions> CallOptionsAsync(AgentAttempt attempt, CancellationToken cancellationToken) =>
        Task.FromResult(AgentCallOptions.None);

    /// <summary>
    /// FR-312, step 6 after the run is recorded and saved before the call (with <see cref="AgentRunPolicy.CommitRunSeparately"/>,
    /// the attempt is counted): the options of <see cref="CallOptionsAsync"/> completed with what the host prepares just
    /// before the call, e.g. its tools (default: unchanged). A failure here leaves the run as saved.
    /// </summary>
    public virtual Task<AgentCallOptions> PrepareCallAsync(AgentAttempt attempt, AgentCallOptions options, CancellationToken cancellationToken) =>
        Task.FromResult(options);

    /// <summary>
    /// FR-311: observes one attempt of <paramref name="request"/> from before step 1 to its end, e.g. a log scope or a
    /// trace span (default: none). Called once the pipeline is known.
    /// </summary>
    public virtual IAgentAttemptObserver? ObserveAttempt(AgentStageRequest request) => null;

    /// <summary>
    /// FR-313, step 8: the host stores the (transformed) content itself instead of the engine, and returns what it stored
    /// (default <c>null</c>: the engine stores it, after <see cref="ShouldStoreAsync"/>). The artifact it returns is the
    /// attempt's <see cref="AgentStageOutcome.Artifact"/>; <see cref="OnArtifactStoredAsync"/> isn't called for it.
    /// D-0.7.0-42: it is also called for a secondary agent with no output (<see cref="AgentAttempt.IsSecondaryAgent"/>),
    /// and again on every redelivery of such a run that already succeeded (the engine can't tell whether the host's save
    /// happened), so for those it must be idempotent.
    /// </summary>
    public virtual Task<AgentStoreOverride?> StoreOverrideAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken) =>
        Task.FromResult<AgentStoreOverride?>(null);

    /// <summary>
    /// FR-319, step 5 after render (before the pre-call check and the key check): the prompt as sent, e.g. FR-121's
    /// specialist composition of the base prompt (default: unchanged). The recorded input is the transformed prompt; a
    /// <see cref="Jobs.JobFailedException"/> fails the run as a refused render does.
    /// </summary>
    public virtual Task<RenderedPrompt> TransformPromptAsync(AgentAttempt attempt, RenderedPrompt prompt, CancellationToken cancellationToken) =>
        Task.FromResult(prompt);

    /// <summary>FR-26: between tool-loop turns (tokens so far in this call): a message stops the loop with <c>BUDGET_EXCEEDED</c>.</summary>
    public virtual Task<string?> CheckToolTurnAsync(AgentAttempt attempt, LlmTurnTokens soFar, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// FR-315: a provider call failed (<paramref name="statusCode"/>: its HTTP status, if any). The default fails the run
    /// with the failure as classified, except that a transient failure of a run under the caller's id stays <c>running</c>
    /// (so the queue's retry continues it) unless <see cref="AgentStageRequest.JobAttempt"/> says it is the queue's last
    /// attempt. Without <see cref="AgentStageRequest.JobAttempt"/> a host must fail such a run itself or override this hook;
    /// a host may also rewrite the failure or keep the run <c>running</c>.
    /// </summary>
    public virtual Task<AgentRunFailureDecision> OnCallFailedAsync(AgentAttempt attempt, AgentStageFailure failure, int? statusCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(failure);
        var retried = attempt.Request.JobAttempt is not { } jobAttempt
            || AgentRunRules.OnProviderFailure(failure.Transient, jobAttempt).RunStatus == AgentRunStatus.Running;
        return Task.FromResult(new AgentRunFailureDecision(failure, KeepRunning: failure.Transient && attempt.Request.AgentRunId is not null && retried));
    }

    /// <summary>
    /// FR-314: the <c>error</c> stored on a rejected attempt's run (<c>null</c>: none). The default is the engine's
    /// rejection marker (D-0.7.0-33); with a <see cref="AgentRunVerdict.Fail"/> verdict a host usually stores
    /// <c>"CODE: message"</c> of <paramref name="failure"/>, so a redelivery fails with that code.
    /// </summary>
    public virtual Task<string?> RejectedErrorAsync(AgentAttempt attempt, string findings, AgentRunVerdict verdict, AgentStageFailure? failure, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(StageValidationRules.RejectedError(findings));

    /// <summary>
    /// FR-314, step 7: the host's own checks of an answered output (<paramref name="content"/>, as the schema sees it),
    /// added after the <c>validate:</c> guards' reasons. Any reason makes the output invalid (default: none).
    /// </summary>
    public virtual Task<IReadOnlyList<string>> ValidateOutputAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// FR-314, §4.55: the <c>on_invalid</c> policy of <paramref name="stage"/> for this pipeline's runs (default: the
    /// definition's), e.g. for a host check (<see cref="ValidateOutputAsync"/>) on a stage that declares none.
    /// </summary>
    public virtual OnInvalidPolicy? OnInvalid(StageDefinition stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.OnInvalid;
    }

    /// <summary>FR-312: the run's <c>output</c> JSON (default: web JSON without nulls).</summary>
    public virtual string SerializeOutput(AgentAttempt attempt, AgentStageRunOutput output) => AgentStageRunner.SerializeOutput(output);

    /// <summary>FR-315: saves the run at <see cref="AgentRunCommit.Point"/>; a host may wrap the save in its own transaction.</summary>
    public virtual Task CommitRunAsync(AgentAttempt attempt, AgentRunCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return commit.Save(cancellationToken);
    }

    /// <summary>
    /// FR-314, §4.55: the request of the retry after <paramref name="invalid"/>. The default appends the findings to the
    /// original feedback (trigger <c>validation_failed</c>) and, when the caller gave a run id, derives the next one from it.
    /// </summary>
    public virtual Task<AgentStageRequest> NextAttemptAsync(AgentStageRequest original, AgentStageRequest current, AgentStageOutcome invalid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(invalid);
        var next = current.Attempt + 1;
        return Task.FromResult(current with
        {
            Trigger = JobTriggers.ValidationFailed,
            Feedback = StageValidationRules.RetryFeedback(original.Feedback, invalid.Findings!),
            Attempt = next,
            AgentRunId = AgentStageRunIds.ForAttempt(original.AgentRunId, next),
        });
    }

    /// <summary>FR-314: the job failure when attempts run out with <c>then: fail</c> (default: <c>VALIDATION</c>).</summary>
    public virtual Task<AgentStageFailure> AttemptsExhaustedAsync(AgentAttempt attempt, string findings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return Task.FromResult(new AgentStageFailure(
            EngineErrorCodes.Validation,
            $"The {attempt.Stage.Id} output failed validation after {attempt.Request.Attempt} attempt(s): {findings}"));
    }

    /// <summary>FR-313, step 8: the stored artifact content of a valid output (default: the output itself).</summary>
    public virtual Task<string> ToContentAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken) => Task.FromResult(content);

    /// <summary>
    /// Step 8: whether to store the (transformed) content; <c>false</c> skips storing it silently (the attempt still ends
    /// valid, with no artifact).
    /// </summary>
    public virtual Task<bool> ShouldStoreAsync(AgentAttempt attempt, string content, CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>FR-316, step 8: after the artifact version is added (before it is saved).</summary>
    public virtual Task OnArtifactStoredAsync(AgentAttempt attempt, Artifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// FR-311: a stored run failed for good before or during the call (<see cref="AgentAttempt.AgentRun"/> is failed; it is
    /// saved after this). Not called for a <see cref="AgentRunVerdict.Fail"/> verdict (that is the step-7 commit), nor under
    /// the default policy for a new run that never reached the call (it isn't stored).
    /// </summary>
    public virtual Task OnRunFailedAsync(AgentAttempt attempt, AgentStageFailure failure, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The extension registered for <paramref name="pipelineId"/>, else <see cref="Default"/>.</summary>
    internal static AgentStageExtension For(IEnumerable<AgentStageExtension> extensions, string pipelineId) =>
        extensions.FirstOrDefault(e => e.PipelineId == pipelineId) ?? Default;

    private sealed class DefaultExtension : AgentStageExtension
    {
        public override string PipelineId => string.Empty;
    }
}

/// <summary>FR-310: the agent run ids of a stage's attempts when the caller supplies the first one.</summary>
public static class AgentStageRunIds
{
    /// <summary>
    /// Attempt <paramref name="attempt"/>'s run id: the caller's id for attempt 1, then one derived from it (stable, so a
    /// redelivered job finds every attempt's run again); <c>null</c> without a caller id (a new id each attempt).
    /// </summary>
    public static Guid? ForAttempt(Guid? first, int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return first is not { } id ? null : attempt == 1 ? id : AgentRunRules.RunId(id, $"attempt-{attempt}");
    }
}
