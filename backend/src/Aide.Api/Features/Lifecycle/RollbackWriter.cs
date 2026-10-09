using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Rollback;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Lifecycle;

/// <summary>
/// FR-44, FR-45, §4.8, D5: plans a rollback with <see cref="RollbackRules"/> and writes it into the current unit of work
/// (nothing is saved here): the restored phase/version (status <c>rolled_back</c>), the state transition and
/// <c>PHASE_CHANGED</c> event, invalidated artifacts, reset steps (invalidated, or requeued when their plan survives), cancelled pending gates and queued jobs, review cycle
/// reset, <c>last_rollback_at</c>, the <c>rollback_events</c> row and a <c>ROLLBACK_COMPLETED</c> event. The Git branch is
/// never rewritten. Used by the escalation <c>rollback</c> decision now and by <c>POST /rollback</c> (phase 11).
/// </summary>
public sealed class RollbackWriter(AideDbContext db, LifecycleWriter writer)
{
    /// <summary>
    /// Rolls the tracked <paramref name="project"/> back to <paramref name="targetVersion"/>. <paramref name="keepGateId"/> is a
    /// pending gate not to cancel (the escalation gate whose decision is this rollback).
    /// </summary>
    public async Task<Result<RollbackEvent>> ApplyAsync(
        Project project,
        int targetVersion,
        string reason,
        string actor,
        DateTimeOffset now,
        Guid? keepGateId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var id = project.Id;

        var transitions = await db.StateTransitions.AsNoTracking()
            .Where(t => t.ProjectId == id)
            .Select(t => new RecordedTransition(t.ProjectVersion, t.ToPhase, t.CreatedAt))
            .ToListAsync(cancellationToken);
        var artifacts = await db.Artifacts.Where(a => a.ProjectId == id).ToListAsync(cancellationToken);
        var steps = await db.ExecutionSteps.Where(s => s.ProjectId == id).ToListAsync(cancellationToken);
        var runInProgress = await db.AgentRuns.AsNoTracking()
            .AnyAsync(r => r.ProjectId == id && (r.Status == AgentRunStatus.Queued || r.Status == AgentRunStatus.Running), cancellationToken);

        var planned = RollbackRules.Plan(new RollbackRequest(
            new LifecycleState(project.CurrentPhase, project.Status, project.ProjectVersion),
            targetVersion,
            reason,
            transitions,
            [.. artifacts.Select(a => new ArtifactRef(a.Id, a.Type, a.Status, a.ProjectVersionAtCreation))],
            [.. steps.Select(s => new StepRef(s.Id, s.PlanArtifactId, s.Status))],
            runInProgress,
            now,
            project.LastRollbackAt));
        if (planned.IsFailure)
        {
            return Result.Failure<RollbackEvent>(planned.Error!);
        }

        var plan = planned.Value;
        await writer.ApplyAsync(project, plan.Transition, actor, now, cancellationToken);
        project.ReviewCycle = plan.ReviewCycle;
        project.ExtraReviewCycles = 0; // §4.8: the review state is cleared, including cycles granted by escalation continue
        project.LastRollbackAt = now;

        var invalidated = plan.InvalidatedArtifactIds.ToHashSet();
        foreach (var artifact in artifacts.Where(a => invalidated.Contains(a.Id)))
        {
            artifact.Status = ArtifactStatus.Invalidated;
            db.AddEvent(
                EventTypes.ArtifactInvalidated,
                id,
                new { artifactId = artifact.Id, type = artifact.Type, version = artifact.Version, cause = "rollback", targetVersion },
                actor,
                now);
        }

        var reset = plan.ResetStepIds.ToHashSet();
        foreach (var step in steps.Where(s => reset.Contains(s.Id)))
        {
            step.Status = StepStatus.Invalidated;
        }

        // Steps of surviving plans that were blocked or rejected get fresh attempts (their escalation gate, if any, is
        // cancelled below), so re-entering execution can run them again.
        var requeued = plan.RequeuedStepIds.ToHashSet();
        foreach (var step in steps.Where(s => requeued.Contains(s.Id)))
        {
            step.Status = StepStatus.Pending;
            step.Attempt = 0;
            step.StartedAt = null;
            step.CompletedAt = null;
        }

        var pendingGates = await db.ApprovalGates
            .Where(g => g.ProjectId == id && g.Status == GateStatus.Pending && g.Id != keepGateId)
            .ToListAsync(cancellationToken);
        foreach (var gate in pendingGates)
        {
            gate.Status = GateRules.Cancel(gate.Status);
            gate.DecidedAt = now;
            gate.Comment = $"Cancelled by rollback to version {targetVersion}.";
            db.AddEvent(EventTypes.GateCancelled, id, new { gateId = gate.Id, type = gate.Type, cause = "rollback", targetVersion }, actor, now);
        }

        var queuedJobs = await db.Jobs.Where(j => j.ProjectId == id && j.Status == JobStatus.Queued).ToListAsync(cancellationToken);
        foreach (var job in queuedJobs)
        {
            job.Status = JobPolicy.Cancel(job.Status);
            job.CompletedAt = now;
        }

        var record = new RollbackEvent
        {
            ProjectId = id,
            FromVersion = plan.FromVersion,
            TargetVersion = plan.TargetVersion,
            Reason = reason,
            InvalidatedArtifactIds = plan.InvalidatedArtifactIds,
            ResetStepIds = [.. plan.ResetStepIds, .. plan.RequeuedStepIds],
            TriggeredBy = actor,
            CreatedAt = now,
        };
        db.RollbackEvents.Add(record);
        db.AddEvent(
            EventTypes.RollbackCompleted,
            id,
            new
            {
                rollbackId = record.Id,
                fromVersion = plan.FromVersion,
                targetVersion = plan.TargetVersion,
                phase = plan.Transition.To,
                reason,
                invalidatedArtifactIds = plan.InvalidatedArtifactIds,
                resetStepIds = plan.ResetStepIds,
                requeuedStepIds = plan.RequeuedStepIds,
                cancelledGateIds = pendingGates.Select(g => g.Id).ToList(),
                cancelledJobIds = queuedJobs.Select(j => j.Id).ToList(),
            },
            actor,
            now);
        return Result.Success(record);
    }
}
