using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// FR-310–FR-316: one attempt of an agent stage as the <see cref="AgentStageExtension"/> hooks see it. The definition parts
/// are known from the start; <see cref="Existing"/> is the agent run already stored under the caller's
/// <see cref="AgentStageRequest.AgentRunId"/> (read before step 1, §4.60), and <see cref="AgentRun"/> is the attempt's run
/// once it is created or loaded (step 3).
/// </summary>
public sealed class AgentAttempt
{
    internal AgentAttempt(AgentStageRequest request, PipelineRun run, string packId, PipelineDefinition definition, StageDefinition stage, AgentStageDefinition agent, AgentRun? existing)
    {
        Request = request;
        Run = run;
        PackId = packId;
        Definition = definition;
        Stage = stage;
        Agent = agent;
        Existing = existing;
    }

    public AgentStageRequest Request { get; }

    public PipelineRun Run { get; }

    public string PackId { get; }

    public PipelineDefinition Definition { get; }

    public StageDefinition Stage { get; }

    public AgentStageDefinition Agent { get; }

    /// <summary>
    /// D-0.7.0-42: whether <see cref="Agent"/> is one of the stage's <c>agents:</c> bindings rather than its primary agent.
    /// <see cref="Stage"/> is then a view of the stage with the binding's agent, inputs and output.
    /// </summary>
    public bool IsSecondaryAgent => !string.Equals(Definition.FindAnyStage(Stage.Id)?.Agent?.Key, Agent.Key, StringComparison.Ordinal);

    /// <summary>The agent run stored under the caller's id before this attempt started, or <c>null</c> (FR-310).</summary>
    public AgentRun? Existing { get; }

    /// <summary>Whether this attempt continues a run left <c>running</c> (e.g. by a lost lease, FR-310).</summary>
    public bool Resumed => Existing is { Status: AgentRunStatus.Running };

    /// <summary>The attempt's agent run (from step 3), or <c>null</c> before it.</summary>
    public AgentRun? AgentRun { get; internal set; }

    /// <summary>The provider and model the attempt resolved (from step 3).</summary>
    public ResolvedLlm? Llm { get; internal set; }
}

/// <summary>FR-316, FR-314, FR-315: why the engine refuses or fails an attempt: the job's error code and message.</summary>
public sealed record AgentStageFailure(string ErrorCode, string Message, bool Transient = false)
{
    /// <summary>The type name of an unexpected exception behind a failed provider call (<c>null</c>: none, or a provider error).</summary>
    public string? ExceptionType { get; init; }

    /// <summary>That exception's text, redacted (<see cref="Domain.Agents.SecretRedaction.SanitizeProviderError"/> with the call's key), for the host's logs.</summary>
    public string? ExceptionDetail { get; init; }
}

/// <summary>
/// FR-312: per-call request options from the host (<c>null</c>: the provider's default). <see cref="Warnings"/> are known
/// before the call and are stored in the run's output before the tool loop's own (duplicates and blanks dropped).
/// </summary>
public sealed record AgentCallOptions(int? MaxTokens = null, int? ContextTokens = null)
{
    public static AgentCallOptions None { get; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// FR-317: the host's tools for this call, used instead of the stage's <c>agent.tools</c> (<c>null</c>: the stage's
    /// tools, if any). An executor with no definitions (<see cref="AgentToolExecutor.None"/>) offers no tools: the call is
    /// a plain completion (0.6.1 without a repository snapshot).
    /// </summary>
    public ILlmToolExecutor? Tools { get; init; }

    /// <summary>
    /// FR-317, FR-319: the host's side of the stage's tool executor (limit, unknown-tool and invalid-argument results, tool
    /// exceptions, fallback context, log format); <c>null</c>: <see cref="AgentToolSession.Default"/>. Not used when
    /// <see cref="Tools"/> replaces the stage's tools.
    /// </summary>
    public AgentToolSession? ToolSession { get; init; }

    /// <summary>
    /// FR-317: the run's <c>tool_calls</c> log after the call (successful or failed), read instead of the stage tools' log;
    /// a <c>null</c> result keeps the run's current log. <c>null</c>: the stage tools' log (<c>[]</c> without tools).
    /// </summary>
    public Func<string?>? ToolCalls { get; init; }
}

/// <summary>
/// FR-312: what the host records on a new agent run besides the engine's own fields. <c>null</c> leaves the engine
/// value: <see cref="InputArtifacts"/> defaults to the locked inputs the stage collected (step 5).
/// </summary>
public sealed record AgentRunMetadata
{
    public static AgentRunMetadata None { get; } = new();

    public string? CatalogVersion { get; init; }

    public string? Packs { get; init; }

    public string? AgentKey { get; init; }

    public Guid? StepId { get; init; }

    /// <summary>The stage recorded on the run (default: the stage id), e.g. the host's current phase.</summary>
    public string? Stage { get; init; }

    public IReadOnlyList<InputArtifactRef>? InputArtifacts { get; init; }

    /// <summary>FR-312: when set, <see cref="UserFeedback"/> is stored as the run's <c>user_feedback</c> instead of the request's feedback.</summary>
    public bool SetsUserFeedback { get; init; }

    public string? UserFeedback { get; init; }
}

/// <summary>FR-315: where the runner saves the agent run (<see cref="AgentStageExtension.CommitRunAsync"/>).</summary>
public enum AgentRunCommitPoint
{
    /// <summary>Step 3–4: a new run was created (only with <see cref="AgentRunPolicy.CommitRunSeparately"/>).</summary>
    Started = 0,

    /// <summary>Step 6: the input is recorded and the model is about to be called (only with <see cref="AgentRunPolicy.CommitRunSeparately"/>).</summary>
    Calling = 1,

    /// <summary>
    /// Step 7: the model answered and the output was validated: every answered attempt, valid or rejected, with its
    /// verdict, before the artifact is stored. Hosts write their run completed or run failed event here (FR-311). Under the
    /// default policy <see cref="AgentRunCommit.Save"/> does nothing (the run is saved with the stage's changes).
    /// </summary>
    Answered = 2,

    /// <summary>A transient failure left the run <c>running</c> for the queue's retry.</summary>
    KeptRunning = 3,

    /// <summary>The run failed or was cancelled.</summary>
    Failed = 4,
}

/// <summary>What an answered attempt's validation decided (§4.55), passed to the <see cref="AgentRunCommitPoint.Answered"/> commit.</summary>
public enum AgentRunVerdict
{
    /// <summary>Not answered (any commit point but <see cref="AgentRunCommitPoint.Answered"/>).</summary>
    None = 0,

    /// <summary>The output is valid and will be stored.</summary>
    Valid = 1,

    /// <summary>The output is invalid and the stage retries with feedback.</summary>
    Retry = 2,

    /// <summary>The output is invalid and attempts ran out with <c>then: escalate</c> (the run stays succeeded).</summary>
    Escalate = 3,

    /// <summary>The output is invalid and attempts ran out with <c>then: fail</c> (the run is failed).</summary>
    Fail = 4,
}

/// <summary>
/// FR-315: one save of the agent run. <see cref="Save"/> writes the shared unit of work's changes; <see cref="Failure"/> is
/// the job failure of a <see cref="AgentRunVerdict.Fail"/> verdict (from <see cref="AgentStageExtension.AttemptsExhaustedAsync"/>).
/// </summary>
public sealed record AgentRunCommit(AgentRunCommitPoint Point, AgentRunVerdict Verdict, Func<CancellationToken, Task> Save)
{
    public AgentStageFailure? Failure { get; init; }
}

/// <summary>
/// FR-315: a host's transaction policy. The default saves the run with the stage's other changes at the end of the
/// attempt, and an attempt that fails before the call leaves no run. <see cref="CommitRunSeparately"/> (0.6.1's order)
/// saves the run when it starts and before the call, fails the stored run on any later failure, and commits the answered
/// run (and its tokens) through <see cref="AgentStageExtension.CommitRunAsync"/> before the artifact is stored.
/// </summary>
public sealed record AgentRunPolicy(bool CommitRunSeparately = false)
{
    public static AgentRunPolicy Default { get; } = new();

    public static AgentRunPolicy Separate { get; } = new(CommitRunSeparately: true);
}

/// <summary>
/// FR-315: what to do with a failed provider call. <see cref="KeepRunning"/> leaves the run <c>running</c> and rethrows
/// <see cref="Failure"/> to the queue; it is honoured only for a transient failure. Otherwise the run fails and the failed
/// hook fires.
/// </summary>
public sealed record AgentRunFailureDecision(AgentStageFailure Failure, bool KeepRunning = false);

/// <summary>
/// FR-311: a host's view of one attempt (e.g. its log scope and trace span), from before step 1 to the attempt's end
/// (<see cref="AgentStageExtension.ObserveAttempt"/>). Disposed when the attempt ends, after one of its callbacks.
/// </summary>
public interface IAgentAttemptObserver : IDisposable
{
    /// <summary>The attempt returned <paramref name="outcome"/> (valid, invalid, or failed with <c>then: fail</c>).</summary>
    void Completed(AgentStageOutcome outcome);

    /// <summary>The attempt threw <paramref name="exception"/> (rethrown after this).</summary>
    void Failed(Exception exception);
}

/// <summary>FR-313: what a host's store override stored (<see cref="Artifact"/>: <c>null</c> when it skipped the content).</summary>
public sealed record AgentStoreOverride(Artifact? Artifact);
