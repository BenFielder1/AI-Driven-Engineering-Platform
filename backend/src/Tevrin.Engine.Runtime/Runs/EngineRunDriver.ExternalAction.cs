using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Stages;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>
/// FR-360 (D-0.8.0-31): the human gate before an external handler. The <c>stage.run</c> job prepares the handler's payload
/// (nothing is sent), raises an <c>external_action</c> gate holding exactly that payload and defers itself; every later
/// delivery of the same job reads the gate before anything else. Approve: the claim (<c>ranAt</c> on the gate) is committed,
/// then the stored payload is sent once, and <c>sentAt</c> is saved before the transition (a later delivery only finishes
/// the stage). Reject, or no decision within 24 hours: nothing is sent, and the stage follows its
/// <c>on_reject</c> or fails with <c>EXTERNAL_ACTION_REJECTED</c>. No approval mode skips the gate: nothing here reads it.
/// </summary>
public sealed partial class EngineRunDriver
{
    private async Task<(bool Done, string? Output)> RunExternalHandlerAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, StageExecution execution, PipelineDefinition definition, StageHandlerRequest request, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var gates = (await db.ApprovalGates
                .Where(g => g.PipelineRunId == run.Id && g.Type == EngineGates.ExternalAction)
                .ToListAsync(cancellationToken))
            .Select(g => (Gate: g, Payload: ExternalActionRules.ReadGatePayload(g.Payload)))
            .ToList();

        // A send of this execution already completed (sentAt, saved before the transition): never send or gate again, just
        // finish the stage (e.g. a redelivery after the transition's save conflicted, or a retry of that job).
        if (gates.Any(x => x.Payload is { SentAt: not null } p && p.StageExecutionId == execution.Id))
        {
            LogExternalAlreadySent(logger, run.Id, stage.Id);
            return (true, null);
        }

        // Only the job that raised a gate sends its payload; a gate of this execution raised by another job isn't this one's.
        var mine = gates
            .Where(x => x.Payload is { } p && p.StageExecutionId == execution.Id && p.JobId == job.Id && string.Equals(p.Handler, request.Handler, StringComparison.Ordinal))
            .OrderByDescending(x => x.Gate.CreatedAt)
            .FirstOrDefault();
        if (mine.Gate is null)
        {
            return await RaiseExternalGateAsync(job, run, stage, execution, request, gates.Select(x => x.Gate).ToList(), now, cancellationToken);
        }

        var (gate, payload) = (mine.Gate, mine.Payload!);
        var phase = payload.RanAt is null ? ToolApprovalPhase.PendingApproval : ToolApprovalPhase.Approved;
        var step = ToolApprovalRules.Next(phase, gateRaised: true, gate.Status, gate.CreatedAt, now);
        LogExternalStep(logger, run.Id, stage.Id, gate.Id, step);
        switch (step)
        {
            case ToolApprovalStep.Wait:
                throw new JobDeferredException($"Waiting on external_action gate {gate.Id}.", ToolApprovalRules.WakeAt(now, gate.CreatedAt));

            case ToolApprovalStep.AlreadyClaimed:
                // At most once: an earlier delivery claimed the send and stopped before its result was saved; it may have sent.
                throw new JobFailedException(ExternalActionRules.OutcomeUnknown(stage.Id), transient: false, EngineErrorCodes.Validation);

            case ToolApprovalStep.TimeOut:
                // No decision within the timer counts as reject. A human decision saved meanwhile wins: the gate's concurrency
                // token fails this save, and the job's retry reads that decision.
                gate.Status = GateStatus.Rejected;
                gate.DecidedAt = now;
                gate.Comment = ToolApprovalRules.TimeoutComment;
                gate.ReviewerId = null;
                LogExternalTimedOut(logger, run.Id, gate.Id);
                return await ExternalRejectedAsync(run, stage, execution, definition, gate, now, cancellationToken);

            case ToolApprovalStep.Deny:
                return await ExternalRejectedAsync(run, stage, execution, definition, gate, now, cancellationToken);

            case ToolApprovalStep.Run:
                break;

            default:
                // Closed: the gate was cancelled (the run moved on, or its job ended); nothing is sent.
                throw new JobFailedException("The external_action gate was closed; nothing was sent.", transient: false, EngineErrorCodes.RunNotActive);
        }

        // FR-360 approve: the handler is resolved, then the claim (the gate's ranAt) is committed with the run's row, so a
        // second delivery, a concurrent claim or a run moved since this job read it never sends again; then the stored
        // payload, exactly what the approver saw, is sent.
        var sent = await handlers.SendExternalAsync(
            request,
            new ExternalActionPayload(payload.Payload),
            async ct =>
            {
                gate.Payload = ToolLoopStates.WithRanAt(gate.Payload, now);
                EngineRunMoves.Touch(db, run);
                await db.SaveChangesAsync(ct);
                LogExternalSending(logger, run.Id, gate.Id, request.Handler);
            },
            cancellationToken);
        var result = sent.RequireRan(request.Handler);
        result.ThrowIfFailed(request.Handler);
        if (result.Outcome == StageHandlerOutcome.Waiting)
        {
            // A send that "continues elsewhere" would leave the stage open, and its next job would gate it again.
            throw new JobFailedException(
                $"External handler '{request.Handler}' answered Waiting; an external send can't wait. It may have been sent; check before retrying.",
                transient: false,
                EngineErrorCodes.Validation);
        }

        // The send completed: recorded on the gate before the transition, so a redelivery only finishes the stage.
        gate.Payload = ExternalActionRules.WithSentAt(gate.Payload, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    /// <summary>
    /// FR-360: prepares the payload (nothing is sent), checks it and raises the stage's <c>external_action</c> gate with it,
    /// with the run's row (a concurrent move fails this save), then defers the job until its next check. While the run's
    /// one pending <c>external_action</c> belongs to something else, the job tries again later; while another job of this
    /// execution waits on its own gate, this one stops (only that job sends).
    /// </summary>
    private async Task<(bool Done, string? Output)> RaiseExternalGateAsync(
        JobExecution job,
        PipelineRun run,
        StageDefinition stage,
        StageExecution execution,
        StageHandlerRequest request,
        IReadOnlyList<ApprovalGate> gates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (gates.FirstOrDefault(g => g.Status == GateStatus.Pending) is { } pending)
        {
            if (ExternalActionRules.ReadGatePayload(pending.Payload)?.StageExecutionId == execution.Id)
            {
                LogStale(logger, run.Id, execution.Id, "another job waits on this stage's external_action gate");
                return (false, null);
            }

            throw new JobDeferredException($"Waiting for the run's pending external_action gate {pending.Id}.", now + AgentStageRunner.GateQueuedRetry);
        }

        var prepared = await handlers.PrepareExternalAsync(request, cancellationToken);
        prepared.Dispatch.RequireRan(request.Handler);
        var content = prepared.Payload?.Content;
        if (ExternalActionRules.PayloadProblem(content) is { } problem)
        {
            // Never logged or stored with the payload; nothing was sent.
            throw new JobFailedException($"{problem} Nothing was sent.", transient: false, EngineErrorCodes.Validation);
        }

        EngineRunMoves.Touch(db, run);
        var gate = new ApprovalGate
        {
            ProjectId = run.ProjectId,
            ReleaseVersionId = run.SubjectId,
            PipelineRunId = run.Id,
            Type = EngineGates.ExternalAction,
            Status = GateStatus.Pending,
            CreatedAt = now,
            Payload = ExternalActionRules.GatePayload(request.Handler, stage.Id, execution.Id, job.Id, content!),
        };
        db.ApprovalGates.Add(gate);
        await db.SaveChangesAsync(cancellationToken);
        LogExternalGateRaised(logger, run.Id, stage.Id, gate.Id, request.Handler);
        throw new JobDeferredException($"Waiting on external_action gate {gate.Id}.", ToolApprovalRules.WakeAt(now, now));
    }

    /// <summary>
    /// FR-360 reject (or the timer): nothing was sent. A top-level stage with <c>on_reject</c> re-enters its target as a
    /// rejected entry gate does (a backward transition, counted against <c>limits.revisions</c>; one save with the
    /// decision); otherwise, or past the target's cap, the stage fails with <c>EXTERNAL_ACTION_REJECTED</c> and the run
    /// stays active, so a retry raises a new gate.
    /// </summary>
    private async Task<(bool Done, string? Output)> ExternalRejectedAsync(
        PipelineRun run, StageDefinition stage, StageExecution execution, PipelineDefinition definition, ApprovalGate gate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (execution.Branch is null && stage.OnReject is { } onReject)
        {
            var cap = onReject.MaxIterations is { } limit ? await RejectCapAsync(run, limit, cancellationToken) : null;
            if (RunDriverRules.OnGateRejected(definition, State(run), EngineGates.ExternalAction, cap) is GateRejection.Reenter reenter)
            {
                LogExternalOnReject(logger, run.Id, stage.Id, reenter.State.Stage);
                var move = RunDriverRules.Revise(definition, reenter.State, run.Revisions);
                await EngineRunMoves.BackAsync(db, jobs, run, definition, move, PipelineTransitionKinds.Loop, now, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return (false, null);
            }
        }

        // The timer's decision, if it made one, is saved before the stage fails.
        await db.SaveChangesAsync(cancellationToken);
        throw new JobFailedException(ExternalActionRules.RejectedMessage(stage.Id, gate.ReviewerId, gate.Comment), transient: false, EngineErrorCodes.ExternalActionRejected);
    }

    /// <summary>The <c>on_reject</c> cap: its value, or the pack's run setting it names (as a rejected entry gate's).</summary>
    private async Task<int?> RejectCapAsync(PipelineRun run, IterationCap cap, CancellationToken cancellationToken)
    {
        if (cap.Value is { } value)
        {
            return value;
        }

        var provider = services.GetServices<IRunSettingsProvider>().FirstOrDefault(s => s.PipelineId == run.PipelineId);
        return provider is null || cap.Setting is null ? null : await provider.GetSettingAsync(run.ProjectId, cap.Setting, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} stage {StageId}: waits on external_action gate {GateId} for {Handler}")]
    private static partial void LogExternalGateRaised(ILogger logger, Guid pipelineRunId, string stageId, Guid gateId, string handler);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} stage {StageId}: its external action was already sent; finishing the stage")]
    private static partial void LogExternalAlreadySent(ILogger logger, Guid pipelineRunId, string stageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} stage {StageId}: external_action gate {GateId}: {Step}")]
    private static partial void LogExternalStep(ILogger logger, Guid pipelineRunId, string stageId, Guid gateId, ToolApprovalStep step);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId}: external_action gate {GateId} had no decision within its timer; rejected")]
    private static partial void LogExternalTimedOut(ILogger logger, Guid pipelineRunId, Guid gateId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline run {PipelineRunId}: approved external_action gate {GateId} claimed; {Handler} sends once")]
    private static partial void LogExternalSending(ILogger logger, Guid pipelineRunId, Guid gateId, string handler);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} stage {StageId}: external action rejected; on_reject to {Target}")]
    private static partial void LogExternalOnReject(ILogger logger, Guid pipelineRunId, string stageId, string target);
}
