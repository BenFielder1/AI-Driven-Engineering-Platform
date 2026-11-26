using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Llm;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// FR-359, §4.73: approval in the middle of a stage. A call to an offered destructive tool stops the tool loop; the runner
/// saves the loop (redacted, capped) on the agent run, raises a <c>tool_approval</c> gate and defers the job. Every later
/// delivery of the same job (same agent run id, FR-310) reads the saved loop before anything else and makes no model call
/// until the gate is decided; then the tool runs at most once (approve) or the model gets the denial (reject, or the
/// gate's 24-hour timer), and the loop resumes from the saved messages.
/// </summary>
public sealed partial class AgentStageRunner
{
    /// <summary>How long a call waits before trying again to raise its gate while the run's other one is pending.</summary>
    public static readonly TimeSpan GateQueuedRetry = TimeSpan.FromMinutes(1);

    /// <summary>
    /// FR-359 step 1–3: the loop stopped at a destructive call. Saves the redacted state (over the cap: the run fails with
    /// <c>TOOL_STATE_TOO_LARGE</c> and the tool never runs), raises the gate and returns the job's deferral.
    /// </summary>
    private async Task<Exception> SuspendAsync(
        AgentAttempt attempt, AgentStageExtension extension, LlmRequest request, AgentToolExecutor executor, LlmToolLoopSuspendedException suspended)
    {
        var agentRun = attempt.AgentRun!;
        var snapshot = suspended.Snapshot!;
        var tool = executor.ListedTool(snapshot.Waiting.Name)
            ?? throw new InvalidOperationException("Only a listed destructive tool suspends the tool loop.");
        ToolLoopState? state = null;
        string? json = null;
        if (RawLength(request, snapshot) <= 2L * ToolApprovalRules.MaxStateBytes)
        {
            try
            {
                state = ToolLoopStates.Create(request, snapshot, tool, executor.Calls, attempt.Llm!.ApiKey) with { JobId = attempt.Request.JobId };
                json = ToolLoopStates.Serialize(state);
            }
            catch (RegexMatchTimeoutException)
            {
                // Text the redaction can't finish in time is never stored; the call fails as too large.
                state = null;
            }
        }

        if (state is null || json is null || !ToolApprovalRules.FitsCap(json))
        {
            LogToolStateTooLarge(logger, attempt.Run.Id, attempt.Stage.Id, tool.Name);
            var failure = new AgentStageFailure(
                EngineErrorCodes.ToolStateTooLarge,
                $"The tool loop of {attempt.Stage.Id} can't be saved within the 1 MB cap, so the destructive tool '{tool.Name}' was not run.");
            await EndFailedCallAsync(attempt, extension, new AgentRunFailureDecision(failure));
            return Failed(failure);
        }

        agentRun.Status = AgentRunStatus.Running;
        agentRun.CompletedAt = null;
        agentRun.Error = null;
        return await RaiseToolGateAsync(attempt, state);
    }

    /// <summary>
    /// FR-359 step 2–3: raises the call's <c>tool_approval</c> gate with its payload and saves it with the state (and the
    /// run's row, so a concurrent cancel or rollback fails this save), then returns the deferral until the gate's timer.
    /// While the run's other <c>tool_approval</c> is pending (one per run), the state is saved without a gate and the job
    /// tries again in <see cref="GateQueuedRetry"/>.
    /// </summary>
    private async Task<JobDeferredException> RaiseToolGateAsync(AgentAttempt attempt, ToolLoopState state)
    {
        var (run, agentRun) = (attempt.Run, attempt.AgentRun!);
        var now = time.GetUtcNow();
        store.Touch(run);
        if (await store.PendingGateAsync(run, EngineGates.ToolApproval, CancellationToken.None) is { } other)
        {
            agentRun.ToolLoopState = ToolLoopStates.Serialize(state with { GateId = null });
            await store.SaveChangesAsync(CancellationToken.None);
            LogToolGateQueued(logger, run.Id, attempt.Stage.Id, agentRun.Id, other.Id);
            return new JobDeferredException($"A tool call waits for the run's pending tool_approval gate {other.Id}.", now + GateQueuedRetry);
        }

        var gate = new ApprovalGate
        {
            ProjectId = run.ProjectId,
            ReleaseVersionId = AgentStageScope.ReleaseVersionOf(run),
            PipelineRunId = run.Id,
            Type = EngineGates.ToolApproval,
            Status = GateStatus.Pending,
            CreatedAt = now,
            Payload = ToolLoopStates.GatePayload(state, agentRun.Id),
        };
        store.Add(gate);
        agentRun.ToolLoopState = ToolLoopStates.Serialize(state with { GateId = gate.Id });
        await hooks.OnGateCreatedAsync(run, gate, $"The destructive tool {state.Pending.Tool} needs approval.", CancellationToken.None);
        await store.SaveChangesAsync(CancellationToken.None);
        LogToolGateRaised(logger, run.Id, attempt.Stage.Id, agentRun.Id, gate.Id, state.Pending.Tool);
        return new JobDeferredException($"Waiting on tool_approval gate {gate.Id}.", ToolApprovalRules.WakeAt(now, now));
    }

    /// <summary>
    /// §4.73: a delivery of a job whose agent run holds a saved destructive call. Pending: defer again (for
    /// <see cref="ToolApprovalRules.WaitPoll"/>), with no model call and nothing saved. Decided: resolve the key and options
    /// as for any call, settle the call (run it once after committing the claim, or send the denial), clear the state and
    /// resume the loop from the saved messages. A permanent failure before the loop resumes closes the call: its pending
    /// gate is cancelled, the state cleared and the run failed, so the stage can be retried.
    /// </summary>
    private async Task<AgentStageOutcome> ContinueToolLoopAsync(AgentAttempt attempt, AgentStageExtension extension, AgentRun saved, CancellationToken cancellationToken)
    {
        var run = attempt.Run;
        attempt.AgentRun = saved;
        var now = time.GetUtcNow();
        if (ToolLoopStates.Read(saved.ToolLoopState) is not { } state)
        {
            // Never guessed at: a state this build can't read is never run.
            await EndSavedCallAsync(saved, AgentRunStatus.Failed, $"{EngineErrorCodes.Validation}: The saved tool loop can't be read; its call was not run.", now);
            throw Permanent($"The saved tool loop of agent run {saved.Id} can't be read; its call was not run.");
        }

        // Defence in depth: only this call's own tool_approval gate counts; anything else closes the call. A claim already on
        // the gate (ranAt) means the call was claimed, whatever the saved phase says.
        var gate = state.GateId is { } gateId ? await store.FindGateAsync(run, gateId, cancellationToken) : null;
        if (gate is not null && (gate.Type != EngineGates.ToolApproval || ToolLoopStates.AgentRunOf(gate.Payload) != saved.Id))
        {
            LogToolGateMismatch(logger, run.Id, saved.Id, gate.Id);
            gate = null;
        }

        var phase = ToolLoopStates.HasRanAt(gate?.Payload) ? ToolApprovalPhase.Approved : state.Phase;
        var step = ToolApprovalRules.Next(phase, state.GateId is not null, gate?.Status, gate?.CreatedAt, now);
        LogToolApprovalStep(logger, run.Id, attempt.Stage.Id, saved.Id, step);
        switch (step)
        {
            case ToolApprovalStep.RaiseGate:
                throw await RaiseToolGateAsync(attempt, state);

            case ToolApprovalStep.Wait:
                throw new JobDeferredException($"Waiting on tool_approval gate {gate!.Id}.", ToolApprovalRules.WakeAt(now, gate.CreatedAt));

            case ToolApprovalStep.Closed:
                await EndSavedCallAsync(saved, AgentRunStatus.Cancelled, "The tool_approval gate was closed; the tool was not run.", now);
                throw new JobFailedException("The tool_approval gate was closed; the tool was not run.", transient: false, EngineErrorCodes.RunNotActive);
        }

        // The call is decided. The key and the host's options come first, so a run that can't go on never runs the tool.
        AgentToolExecutor executor;
        AgentCallOptions options;
        try
        {
            await StartRunAsync(attempt, extension, saved.PromptVersion, cancellationToken);
            await extension.OnRunStartedAsync(attempt, cancellationToken);
            if (!attempt.Llm!.IsConfigured)
            {
                throw new JobFailedException($"No API key is configured for {attempt.Llm.Provider.ToWire()}.", transient: false, EngineErrorCodes.ProviderNotConfigured);
            }

            options = await extension.CallOptionsAsync(attempt, cancellationToken);
            options = await extension.PrepareCallAsync(attempt, options, cancellationToken);
            if (options.Tools is not null)
            {
                throw Permanent($"The host replaced the tools of agent run {saved.Id}, so its saved tool loop can't be resumed.");
            }

            executor = AgentToolExecutor.ForStage(StageTools(attempt), ToolTierRules.MaxTierOf(attempt.Stage.ToolPolicy), options.ToolSession, logger);
            executor.Restore(state.Calls);
        }
        catch (JobFailedException ex) when (!ex.Transient)
        {
            await CloseSavedCallAsync(saved, gate, $"{ex.ErrorCode ?? EngineErrorCodes.Validation}: {ex.Message} The tool was not run.");
            throw;
        }

        var call = ToolLoopStates.WaitingCall(state);
        LlmToolResult decided;
        switch (step)
        {
            case ToolApprovalStep.TimeOut:
                // FR-359: no decision within the timer counts as reject. A human decision saved meanwhile wins: the gate's
                // concurrency token fails this save, and the job's retry reads that decision.
                gate!.Status = GateStatus.Rejected;
                gate.DecidedAt = now;
                gate.Comment = ToolApprovalRules.TimeoutComment;
                gate.ReviewerId = null;
                await store.SaveChangesAsync(CancellationToken.None);
                LogToolGateTimedOut(logger, run.Id, gate.Id);
                decided = executor.RecordUnrun(call, ToolApprovalRules.TimeoutDenial);
                break;

            case ToolApprovalStep.Deny:
                decided = executor.RecordUnrun(call, ToolApprovalRules.DenialText(gate!.ReviewerId?.ToString() ?? ToolApprovalRules.TimerUser, gate.Comment));
                break;

            case ToolApprovalStep.AlreadyClaimed:
                // At most once: an earlier delivery claimed the call and stopped before resuming; it may have run.
                decided = executor.RecordUnrun(call, ToolApprovalRules.OutcomeUnknown);
                break;

            default:
                // The tool's checks as they stand now (still offered, the session's limit) come before the claim, so a
                // refused call is never claimed and its gate never says it ran.
                if (executor.RefuseApproved(call) is { } refused)
                {
                    decided = refused;
                    break;
                }

                // FR-359 approve: the claim (phase approved, the gate's ranAt) is committed before the tool is invoked, with
                // the gate's and the run's rows, so a second delivery, a concurrent claim or a run moved since this job read
                // it never runs the tool again; a crash after the claim leaves it unrun (AlreadyClaimed), never run twice.
                saved.ToolLoopState = ToolLoopStates.Serialize(state with { Phase = ToolApprovalPhase.Approved, ClaimedAt = now });
                gate!.Payload = ToolLoopStates.WithRanAt(gate.Payload, now);
                store.Touch(run);
                await store.SaveChangesAsync(CancellationToken.None);
                LogToolRunning(logger, run.Id, gate.Id, state.Pending.Tool);
                decided = await executor.RunApprovedAsync(call, cancellationToken);
                break;
        }

        // The model's later calls in the paused turn aren't run: their arguments were saved redacted and nobody approved
        // them. Each gets an error result, so the model may call it again.
        IReadOnlyList<LlmToolResult> after = [.. ToolLoopStates.CallsAfterWaiting(state).Select(c => executor.RecordUnrun(c, ToolApprovalRules.NotRunAfterPause))];

        // §4.73 resumed: the state is cleared (saved with the run's next save) and the loop continues from the saved
        // messages, with no model call for the earlier turns.
        saved.ToolLoopState = null;
        var request = new LlmRequest
        {
            Model = saved.Model,
            System = state.System,
            Messages = ToolLoopStates.Messages(state),
            MaxTokens = options.MaxTokens,
            ContextTokens = options.ContextTokens,
            Agent = attempt.Agent.Key,
            ApiKey = attempt.Llm!.ApiKey,
        };
        var answer = await CallAsync(attempt, extension, request, executor, options, cancellationToken, ToolLoopStates.Resume(state, decided, after));
        var artifactDefinition = attempt.Definition.FindArtifact(attempt.Stage.Output);
        var content = AgentStagePrompt.Content(artifactDefinition?.Format ?? ArtifactFormat.Markdown, answer.Completion.Text);
        var user = state.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
        return await FinishAsync(attempt, extension, artifactDefinition, content, user, cancellationToken);
    }

    /// <summary>
    /// A saved call that can't go on: its gate, if still pending, is cancelled, the state cleared and the run failed (one
    /// save), so nothing waits on it and the stage can be retried.
    /// </summary>
    private async Task CloseSavedCallAsync(AgentRun saved, ApprovalGate? gate, string error)
    {
        var now = time.GetUtcNow();
        if (gate is { Status: GateStatus.Pending })
        {
            gate.Status = GateStatus.Cancelled;
            gate.DecidedAt = now;
        }

        await EndSavedCallAsync(saved, AgentRunStatus.Failed, error, now);
    }

    /// <summary>Ends a saved call that will never run: the run ends <paramref name="status"/> and the state is cleared.</summary>
    private async Task EndSavedCallAsync(AgentRun saved, AgentRunStatus status, string error, DateTimeOffset now)
    {
        saved.Status = status;
        saved.Error = Truncate(error);
        saved.ToolLoopState = null;
        saved.CompletedAt = now;
        await store.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The characters of everything the state would hold, before redacting (text far past the cap isn't redacted).</summary>
    private static long RawLength(LlmRequest request, LlmToolLoopSnapshot snapshot)
    {
        static long Of(LlmToolExchange exchange) =>
            exchange.AssistantText.Length
            + exchange.Calls.Sum(c => (long)c.Name.Length + c.Id.Length + c.ArgumentsJson.Length)
            + exchange.Results.Sum(r => (long)r.Content.Length);

        return request.System.Length + request.Messages.Sum(m => (long)m.Content.Length) + snapshot.Exchanges.Sum(Of) + Of(snapshot.Current);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} stage {StageId}: agent run {AgentRunId} waits on tool_approval gate {GateId} for {Tool}")]
    private static partial void LogToolGateRaised(ILogger logger, Guid pipelineRunId, string stageId, Guid agentRunId, Guid gateId, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} stage {StageId}: agent run {AgentRunId} waits for the run's pending tool_approval gate {GateId}")]
    private static partial void LogToolGateQueued(ILogger logger, Guid pipelineRunId, string stageId, Guid agentRunId, Guid gateId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} stage {StageId}: agent run {AgentRunId} saved tool call: {Step}")]
    private static partial void LogToolApprovalStep(ILogger logger, Guid pipelineRunId, string stageId, Guid agentRunId, ToolApprovalStep step);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline run {PipelineRunId}: agent run {AgentRunId}'s saved call names gate {GateId}, which is not its tool_approval gate; the call is closed")]
    private static partial void LogToolGateMismatch(ILogger logger, Guid pipelineRunId, Guid agentRunId, Guid gateId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId}: tool_approval gate {GateId} had no decision within its timer; rejected")]
    private static partial void LogToolGateTimedOut(ILogger logger, Guid pipelineRunId, Guid gateId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline run {PipelineRunId}: approved tool_approval gate {GateId} claimed; running {Tool} once")]
    private static partial void LogToolRunning(ILogger logger, Guid pipelineRunId, Guid gateId, string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline run {PipelineRunId} stage {StageId}: TOOL_STATE_TOO_LARGE; {Tool} was not run")]
    private static partial void LogToolStateTooLarge(ILogger logger, Guid pipelineRunId, string stageId, string tool);
}
