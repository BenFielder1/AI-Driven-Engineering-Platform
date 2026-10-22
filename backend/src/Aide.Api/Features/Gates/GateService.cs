using System.Text.Json;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Gates;

/// <summary>
/// FR-13, FR-14: deciding gates. <see cref="GateRules"/> decides what a decision means; this service re-checks the lifecycle
/// preconditions through the state machine and writes the decision, its follow-up (transition, re-run job, review cycle,
/// pre-release gate or rollback) and the events in one transaction guarded by the project's and the gate's <c>xmin</c>.
/// </summary>
public sealed partial class GateService(
    AideDbContext db,
    LifecycleSnapshotLoader snapshots,
    LifecycleWriter writer,
    RollbackWriter rollbacks,
    JobEnqueuer jobs,
    WorkingCopyPreparer workingCopies,
    TimeProvider time,
    ILogger<GateService> logger)
{
    public async Task<Result<ApprovalGate>> ApproveAsync(Guid projectId, Guid gateId, DecideGateRequest request, Guid reviewerId, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (project, gate) = await LoadAsync(projectId, gateId, cancellationToken);
        if (project is null || gate is null)
        {
            return NotFound();
        }

        var stepBlocked = gate.Type == GateType.Escalation
            && (await GateReader.BlockedStepsAsync(db, project.Id, [gate.Id], cancellationToken)).ContainsKey(gate.Id);
        var resolved = GateRules.Approve(gate.Type, gate.Status, request.Decision, request.TargetCheckpoint, stepBlocked);
        if (resolved.IsFailure)
        {
            return Result.Failure<ApprovalGate>(resolved.Error!);
        }

        var resolution = resolved.Value;
        var now = time.GetUtcNow();
        var comment = Clean(request.Comment);
        var state = new LifecycleState(project.CurrentPhase, project.Status, project.Checkpoint);
        Decide(gate, resolution.NewStatus, resolution.Decision, resolution.TargetVersion, reviewerId, comment, now);
        project.UpdatedAt = now;

        // A rollback decision locks the project's jobs until the decision commits (RollbackWriter); a failure rolls it back.
        await using var transaction = resolution.FollowUp == GateFollowUp.Rollback
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        switch (resolution.FollowUp)
        {
            case GateFollowUp.ApplyTransition:
                {
                    var entry = GateRules.GuardedEntry(gate.Type);
                    if (PhaseOrder.Next(project.CurrentPhase) != entry)
                    {
                        return Result.Failure<ApprovalGate>(new DomainError(
                            ErrorCodes.WrongPhase,
                            $"The project is in {project.CurrentPhase.ToWire()}; this {gate.Type.ToWire()} gate guards entry to {entry?.ToWire()}."));
                    }

                    bool? prepared = null;
                    if (entry == Phase.Execution)
                    {
                        // FR-35, §4.1: the working copy and project branch are prepared before execution is entered; the
                        // outcome is the condition, so a failure leaves it unmet and the state machine reports it.
                        prepared = (await workingCopies.PrepareAsync(project.Id, cancellationToken)).IsSuccess;
                    }

                    var snapshot = await snapshots.LoadAsync(project, cancellationToken);
                    snapshot = snapshot with
                    {
                        EntryGateApproved = true,
                        PendingGates = [.. snapshot.PendingGates.Where(t => t != gate.Type)],
                        WorkingCopyPrepared = prepared ?? snapshot.WorkingCopyPrepared,
                    };
                    var advanceReason = await AdvanceReasonAsync(project.Id, gate.Id, cancellationToken);
                    var reason = GatedAdvanceReason(gate.Type, advanceReason, comment);
                    var applied = LifecycleStateMachine.Apply(state, TransitionRequest.Advance(reason), snapshot);
                    if (applied.IsFailure)
                    {
                        return Result.Failure<ApprovalGate>(applied.Error!);
                    }

                    await writer.ApplyAsync(project, applied.Value, actor, now, cancellationToken);
                    break;
                }

            case GateFollowUp.StartReviewCycle when project.CurrentPhase == Phase.Execution:
                await ContinueBlockedStepsAsync(project, gate, actor, now, cancellationToken);
                break;

            case GateFollowUp.StartReviewCycle:
                {
                    project.ExtraReviewCycles = ReviewCycleRules.ExtraAfterContinue(project.ExtraReviewCycles);

                    // FR-14: execution re-enters for the latest review's optimisation plan as soon as it is approved (with
                    // steps still to run). If it already is, the cycle starts now; if its draft is waiting, approving it
                    // starts the cycle (Decisions: review cycle); with no plan at all, the extra cycle is a new review.
                    var snapshot = await snapshots.LoadAsync(project, cancellationToken) with { EscalationOutcome = EscalationDecision.Continue };
                    var cycle = LifecycleStateMachine.Apply(state, TransitionRequest.ReviewCycle("Escalation: continue with another review cycle"), snapshot);
                    if (cycle.IsSuccess)
                    {
                        await writer.ApplyAsync(project, cycle.Value, actor, now, cancellationToken);
                        if (!project.ExecutionPaused)
                        {
                            await jobs.EnqueueAsync(JobTypes.Execution, new ExecutionJobPayload(project.Id, ExecutionJobPayload.ReviewCycle), project.Id, cancellationToken, now);
                        }
                    }
                    else if (!await snapshots.CyclePlanDraftPendingAsync(project, cancellationToken)
                        && !await Review.ReviewService.ActiveAsync(db, project.Id, cancellationToken))
                    {
                        // The escalated review proposed no optimisation plan to approve: the extra cycle is a new review.
                        await jobs.EnqueueAsync(
                            JobTypes.Review,
                            new LifecycleJobPayload(project.Id, project.Checkpoint, LifecycleJobPayload.UserRequested, "Escalation decision: continue with another review cycle."),
                            project.Id,
                            cancellationToken,
                            now);
                    }

                    break;
                }

            case GateFollowUp.MarkReleaseEligible:
                {
                    // FR-14 accept: release-eligible with the comment recorded on the gate; release still needs the
                    // pre_release gate (§4.2, every mode).
                    var preReleasePending = await db.ApprovalGates.AnyAsync(
                        g => g.ProjectId == project.Id && g.Type == GateType.PreRelease && g.Status == GateStatus.Pending,
                        cancellationToken);
                    if (project.CurrentPhase == Phase.Review && !preReleasePending)
                    {
                        writer.AddGate(project, GateType.PreRelease, null, actor, now);
                    }

                    break;
                }

            case GateFollowUp.Rollback:
                {
                    var target = resolution.TargetVersion!.Value;
                    var rolledBack = await rollbacks.ApplyAsync(
                        project,
                        target,
                        comment ?? $"Escalation decision: roll back to version {target}",
                        actor,
                        now,
                        keepGateId: gate.Id,
                        cancellationToken);
                    if (rolledBack.IsFailure)
                    {
                        return Result.Failure<ApprovalGate>(rolledBack.Error!);
                    }

                    break;
                }

            default:
                break;
        }

        db.AddEvent(
            EventTypes.GateApproved,
            project.Id,
            new { gateId = gate.Id, type = gate.Type, decision = gate.Decision, targetCheckpoint = gate.TargetCheckpoint, comment },
            actor,
            now);
        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<ApprovalGate>(saved.Error!);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        LogApproved(logger, project.Id, gate.Id, gate.Type, resolution.FollowUp);
        return Result.Success(gate);
    }

    /// <summary>
    /// FR-14 <c>continue</c> on the escalation of a blocked step (FR-33): the current plan's blocked steps go back to
    /// <c>pending</c> with fresh attempts (their violations stay as feedback) and, unless paused, execution is queued.
    /// </summary>
    private async Task ContinueBlockedStepsAsync(Project project, ApprovalGate gate, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await ExecutionQueries.CurrentPlanIdAsync(db, project.Id, cancellationToken) is not { } planId)
        {
            return;
        }

        var steps = await ExecutionQueries.CurrentStepsAsync(db, planId, track: true, cancellationToken);
        foreach (var step in steps)
        {
            var change = StepRules.Unblock(step.Status);
            if (!change.Changed)
            {
                continue;
            }

            step.Status = change.NewStatus;
            step.Attempt = 0;
            db.AddEvent(
                EventTypes.StepUnblocked,
                project.Id,
                new { stepId = step.Id, stepNumber = step.StepNumber, gateId = gate.Id },
                actor,
                now);
        }

        if (ExecutionRules.ShouldContinue([.. steps.Select(ExecutionQueries.ToState)], project.ExecutionPaused))
        {
            await jobs.EnqueueAsync(JobTypes.Execution, new ExecutionJobPayload(project.Id, ExecutionJobPayload.EscalationContinue), project.Id, cancellationToken, now);
        }
    }

    public async Task<Result<ApprovalGate>> RejectAsync(Guid projectId, Guid gateId, DecideGateRequest request, Guid reviewerId, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (project, gate) = await LoadAsync(projectId, gateId, cancellationToken);
        if (project is null || gate is null)
        {
            return NotFound();
        }

        var resolved = GateRules.Reject(gate.Type, gate.Status);
        if (resolved.IsFailure)
        {
            return Result.Failure<ApprovalGate>(resolved.Error!);
        }

        if (request.Decision is not null || request.TargetCheckpoint is not null)
        {
            return Result.Failure<ApprovalGate>(new DomainError(
                ErrorCodes.InvalidDecision,
                $"Rejecting a {gate.Type.ToWire()} gate takes no escalation decision or target version."));
        }

        var resolution = resolved.Value;
        var now = time.GetUtcNow();
        var comment = Clean(request.Comment);
        Decide(gate, resolution.NewStatus, null, null, reviewerId, comment, now);
        project.UpdatedAt = now;

        // FR-13 / D16: the phase stays; the producer of the gated artifact runs again with the comment as feedback.
        Guid? jobId = null;
        if (resolution.FollowUp == GateFollowUp.RerunAgent && resolution.RerunAgent is { } agent)
        {
            var job = await jobs.EnqueueAsync(
                JobTypes.ForAgent(agent),
                new LifecycleJobPayload(project.Id, project.Checkpoint, LifecycleJobPayload.GateRejected, comment, gate.Id),
                project.Id,
                cancellationToken,
                now);
            jobId = job.Id;
        }

        db.AddEvent(
            EventTypes.GateRejected,
            project.Id,
            new { gateId = gate.Id, type = gate.Type, comment, rerunAgent = resolution.RerunAgent, jobId },
            actor,
            now);
        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<ApprovalGate>(saved.Error!);
        }

        LogRejected(logger, project.Id, gate.Id, gate.Type);
        return Result.Success(gate);
    }

    private async Task<(Project? Project, ApprovalGate? Gate)> LoadAsync(Guid projectId, Guid gateId, CancellationToken cancellationToken)
    {
        var gate = await db.ApprovalGates.FirstOrDefaultAsync(g => g.Id == gateId && g.ProjectId == projectId, cancellationToken);
        if (gate is null)
        {
            return (null, null);
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        return (project, gate);
    }

    /// <summary>
    /// FR-12: the transition reason when approving a gate applies a held advance. The reason given with the advance (kept in
    /// the gate's <c>GATE_CREATED</c> payload) comes first, then the approval: "<c>{reason} ({type} gate approved: {comment})</c>";
    /// either part is left out when it wasn't given, e.g. "<c>spec_approval gate approved</c>".
    /// </summary>
    internal static string GatedAdvanceReason(GateType type, string? advanceReason, string? comment)
    {
        var approval = comment is null ? $"{type.ToWire()} gate approved" : $"{type.ToWire()} gate approved: {comment}";
        return advanceReason is null ? approval : $"{advanceReason} ({approval})";
    }

    /// <summary>The reason given with the advance that created <paramref name="gateId"/>, from its <c>GATE_CREATED</c> event.</summary>
    private async Task<string?> AdvanceReasonAsync(Guid projectId, Guid gateId, CancellationToken cancellationToken)
    {
        var match = JsonSerializer.Serialize(new { gateId });
        var payload = await db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Type == EventTypes.GateCreated && EF.Functions.JsonContains(e.Payload, match))
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Payload)
            .FirstOrDefaultAsync(cancellationToken);
        if (payload is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty(LifecycleWriter.GateCreatedReasonKey, out var reason)
            && reason.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(reason.GetString())
            ? reason.GetString()!.Trim()
            : null;
    }

    private static void Decide(ApprovalGate gate, GateStatus status, EscalationDecision? decision, int? targetVersion, Guid reviewerId, string? comment, DateTimeOffset now)
    {
        gate.Status = status;
        gate.Decision = decision;
        gate.TargetCheckpoint = targetVersion;
        gate.ReviewerId = reviewerId;
        gate.Comment = comment;
        gate.DecidedAt = now;
    }

    private static string? Clean(string? comment) => string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();

    private static Result<ApprovalGate> NotFound() =>
        Result.Failure<ApprovalGate>(new DomainError(ErrorCodes.NotFound, "The gate was not found."));

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: gate {GateId} ({GateType}) approved ({FollowUp})")]
    private static partial void LogApproved(ILogger logger, Guid projectId, Guid gateId, GateType gateType, GateFollowUp followUp);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: gate {GateId} ({GateType}) rejected")]
    private static partial void LogRejected(ILogger logger, Guid projectId, Guid gateId, GateType gateType);
}
