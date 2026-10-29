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
/// never rewritten. Must run inside a transaction: the project's queued and running jobs are locked (<c>FOR UPDATE</c>) so
/// none can be claimed between the decision and the commit. A release job that is running blocks the rollback (its Git work
/// can't be undone), as does a queued release retry whose attempt already merged; otherwise in-progress release attempts
/// whose job was queued fail as <c>RELEASE_CANCELLED</c>. Used by the escalation <c>rollback</c> decision and by
/// <c>POST /rollback</c>.
/// </summary>
public sealed class RollbackWriter(AideDbContext db, LifecycleWriter writer)
{
    /// <summary>
    /// Rolls the tracked <paramref name="project"/> back to checkpoint <paramref name="targetVersion"/> of its current release
    /// version (§4.10: only that version's transitions, artifacts and steps are read, so earlier versions' work is never
    /// touched). <paramref name="keepGateId"/> is a pending gate not to cancel (the escalation gate whose decision is this rollback).
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
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A rollback runs inside a database transaction: the project's job rows stay locked until it commits.");
        }

        var id = project.Id;

        // Lock the project's queued and running jobs until the rollback commits. Claiming takes queued rows with SKIP LOCKED,
        // so none of them can start meanwhile; a claim already in flight holds its rows, so this waits for it and then sees
        // the job running (PostgreSQL re-checks the row it waited for). The decision below and the cancellations use these rows.
        // Notification jobs (FR-47) are left alone: notifications about what happened before the rollback still go out.
        var activeJobs = await db.Jobs
            .FromSql($"""
                SELECT * FROM jobs
                WHERE project_id = {id} AND status IN ('queued', 'running') AND type <> {JobTypes.Notification}
                ORDER BY id
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        var releaseRunning = activeJobs.Any(j => j.Type == JobTypes.Release && j.Status == JobStatus.Running);

        // A release job waiting out a retry backoff whose attempt already merged: cancelling it would leave a merged,
        // untagged release behind (the merge can't be undone), so the rollback waits for the release instead.
        var releasePartiallyApplied = activeJobs.Any(j => j.Type == JobTypes.Release && j.Status == JobStatus.Queued)
            && await db.Releases.AsNoTracking().AnyAsync(
                r => r.ProjectId == id && r.Status == ReleaseStatus.InProgress && r.MergeCommitSha != null, cancellationToken);

        // §4.10: the rollback works on the release version in development only; a released (closed) version or one awaiting
        // its merge can't be rolled back at all.
        var versionId = project.CurrentReleaseVersionId ?? await db.CurrentReleaseVersionIdAsync(id, cancellationToken) ?? Guid.Empty;
        var versionStatus = await db.ReleaseVersions.AsNoTracking()
            .Where(v => v.Id == versionId)
            .Select(v => (ReleaseVersionStatus?)v.Status)
            .FirstOrDefaultAsync(cancellationToken);
        var transitions = await db.StateTransitions.AsNoTracking()
            .Where(t => t.ProjectId == id && t.ReleaseVersionId == versionId)
            .Select(t => new RecordedTransition(t.Checkpoint, t.ToPhase, t.CreatedAt))
            .ToListAsync(cancellationToken);
        var artifacts = await db.Artifacts.Where(a => a.ProjectId == id && a.ReleaseVersionId == versionId).ToListAsync(cancellationToken);
        var steps = await db.ExecutionSteps.Where(s => s.ProjectId == id && s.ReleaseVersionId == versionId).ToListAsync(cancellationToken);
        var runInProgress = await db.AgentRuns.AsNoTracking()
            .AnyAsync(r => r.ProjectId == id && (r.Status == AgentRunStatus.Queued || r.Status == AgentRunStatus.Running), cancellationToken);

        var planned = RollbackRules.Plan(new RollbackRequest(
            new LifecycleState(project.CurrentPhase, project.Status, project.Checkpoint),
            targetVersion,
            reason,
            transitions,
            [.. artifacts.Select(a => new ArtifactRef(a.Id, a.Type, a.Status, a.CheckpointAtCreation))],
            [.. steps.Select(s => new StepRef(s.Id, s.PlanArtifactId, s.Status))],
            runInProgress,
            now,
            project.LastRollbackAt)
        {
            ReleaseRunning = releaseRunning,
            ReleasePartiallyApplied = releasePartiallyApplied,
            VersionReleased = project.Status == ProjectStatus.Released || versionStatus is ReleaseVersionStatus.Released or ReleaseVersionStatus.Imported,
            AwaitingMerge = versionStatus == ReleaseVersionStatus.AwaitingMerge,
        });
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
                new { artifactId = artifact.Id, type = artifact.Type, version = artifact.Version, cause = "rollback", targetCheckpoint = targetVersion },
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
            gate.Comment = $"Cancelled by rollback to checkpoint {targetVersion}.";
            db.AddEvent(EventTypes.GateCancelled, id, new { gateId = gate.Id, type = gate.Type, cause = "rollback", targetCheckpoint = targetVersion }, actor, now);
        }

        var queuedJobs = activeJobs.Where(j => j.Status == JobStatus.Queued).ToList();
        foreach (var job in queuedJobs)
        {
            job.Status = JobPolicy.Cancel(job.Status);
            job.CompletedAt = now;
        }

        // A release whose job was still queued (now cancelled) will never run: its attempt fails as cancelled (FR-43, FR-44).
        var releases = await db.Releases.Where(r => r.ProjectId == id && r.Status == ReleaseStatus.InProgress).ToListAsync(cancellationToken);
        var gitProvider = releases.Count == 0 ? null : await Release.ReleaseRecords.ProviderAsync(db, id, cancellationToken);
        foreach (var release in releases)
        {
            Release.ReleaseRecords.Fail(
                db, release, ErrorCodes.ReleaseCancelled, $"Cancelled by the rollback to checkpoint {targetVersion}.", [], actor, now, provider: gitProvider);
        }

        var record = new RollbackEvent
        {
            ProjectId = id,
            ReleaseVersionId = versionId,
            FromCheckpoint = plan.FromVersion,
            TargetCheckpoint = plan.TargetVersion,
            RestoredPhase = plan.Transition.To,
            Reason = reason,
            InvalidatedArtifactIds = plan.InvalidatedArtifactIds,
            ResetStepIds = plan.ResetStepIds,
            RequeuedStepIds = plan.RequeuedStepIds,
            CancelledGateIds = [.. pendingGates.Select(g => g.Id)],
            CancelledJobIds = [.. queuedJobs.Select(j => j.Id)],
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
                releaseVersionId = versionId,
                fromCheckpoint = plan.FromVersion,
                targetCheckpoint = plan.TargetVersion,
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
