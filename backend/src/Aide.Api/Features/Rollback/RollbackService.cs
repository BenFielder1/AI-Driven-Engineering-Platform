using Aide.Api.Features.Activity;
using Aide.Api.Features.Lifecycle;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Rollback;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Rollback;

/// <summary>
/// FR-44, FR-45, §4.8: the Owner's rollback (<see cref="RollbackWriter"/> in one transaction guarded by the project's
/// <c>xmin</c>), the rollback history and the version picker (<see cref="RollbackRules.Versions"/>).
/// </summary>
public sealed partial class RollbackService(AideDbContext db, RollbackWriter writer, TimeProvider time, ILogger<RollbackService> logger)
{
    /// <summary>
    /// Rolls the project back to <paramref name="targetVersion"/>. Checks, in order: <c>INVALID_TARGET_VERSION</c>,
    /// <c>ROLLBACK_BLOCKED</c>, <c>RATE_LIMITED</c> (with the retry-after time); a concurrent change is <c>CONFLICT</c>.
    /// </summary>
    public async Task<Result<RollbackDto>> RollbackAsync(Guid projectId, int targetVersion, string reason, string actor, CancellationToken cancellationToken)
    {
        // The writer locks the project's jobs for the rest of this transaction; a failure disposes it (rolled back).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<RollbackDto>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        var applied = await writer.ApplyAsync(project, targetVersion, reason, actor, time.GetUtcNow(), keepGateId: null, cancellationToken);
        if (applied.IsFailure)
        {
            return Result.Failure<RollbackDto>(applied.Error!);
        }

        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<RollbackDto>(saved.Error!);
        }

        await transaction.CommitAsync(cancellationToken);
        var record = applied.Value;
        LogRolledBack(logger, project.Id, record.FromVersion, record.TargetVersion, record.RestoredPhase);
        var actors = await ActorResolver.ForActorsAsync(db, [record.TriggeredBy], cancellationToken);
        return Result.Success(ToDto(record, actors));
    }

    /// <summary>Every rollback of the project, newest first.</summary>
    public async Task<IReadOnlyList<RollbackDto>> ListAsync(Guid projectId, CancellationToken cancellationToken)
    {
        // restored_phase is nullable in the database for the rolling-update window (pods without this column may insert rows);
        // such a row reads the phase from the rollback's own state transition (same timestamp), else the project's phase —
        // the same fallbacks the migration's backfill uses. The entity property itself is always set by this code.
        var records = await db.RollbackEvents
            .FromSql($"""
                SELECT r.id, r.project_id, r.from_version, r.target_version,
                       COALESCE(
                           r.restored_phase,
                           (SELECT t.to_phase FROM state_transitions AS t
                            WHERE t.project_id = r.project_id AND t.created_at = r.created_at
                            ORDER BY t.id DESC LIMIT 1),
                           (SELECT p.current_phase FROM projects AS p WHERE p.id = r.project_id)) AS restored_phase,
                       r.reason, r.invalidated_artifact_ids, r.reset_step_ids, r.requeued_step_ids, r.cancelled_gate_ids,
                       r.cancelled_job_ids, r.triggered_by, r.created_at
                FROM rollback_events AS r
                WHERE r.project_id = {projectId}
                """)
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(cancellationToken);
        var actors = await ActorResolver.ForActorsAsync(db, records.Select(r => r.TriggeredBy), cancellationToken);
        return [.. records.Select(r => ToDto(r, actors))];
    }

    /// <summary>The project's versions up to the current one, newest first, with whether each is a rollback target now.</summary>
    public async Task<IReadOnlyList<ProjectVersionDto>?> VersionsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.ProjectVersion, p.CurrentPhase, p.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return null;
        }

        var transitions = await db.StateTransitions.AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Select(t => new RecordedTransition(t.ProjectVersion, t.ToPhase, t.CreatedAt))
            .ToListAsync(cancellationToken);
        return [.. RollbackRules.Versions(transitions, project.ProjectVersion, project.CurrentPhase, project.CreatedAt)
            .Select(v => new ProjectVersionDto(
                v.Version,
                v.Phase,
                v.EnteredAt,
                v.RollbackAllowed,
                v.Block switch
                {
                    VersionBlock.CurrentVersion => RollbackTargetBlock.CurrentVersion,
                    VersionBlock.ReleasePhase => RollbackTargetBlock.ReleasePhase,
                    _ => null,
                }))];
    }

    private static RollbackDto ToDto(RollbackEvent record, Func<string, ActorDto> actors) => new(
        record.Id,
        record.FromVersion,
        record.TargetVersion,
        record.RestoredPhase,
        record.Reason,
        record.InvalidatedArtifactIds,
        record.ResetStepIds,
        record.RequeuedStepIds,
        record.CancelledGateIds,
        record.CancelledJobIds,
        record.CreatedAt,
        actors(record.TriggeredBy));

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} rolled back from version {FromVersion} to {TargetVersion} ({Phase})")]
    private static partial void LogRolledBack(ILogger logger, Guid projectId, int fromVersion, int targetVersion, Aide.Domain.Lifecycle.Phase phase);
}
