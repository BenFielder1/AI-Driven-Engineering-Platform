using Aide.Api.Features.Activity;
using Aide.Api.Features.Lifecycle;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Rollback;

/// <summary>
/// FR-44, FR-45, §4.8: the Owner's rollback (<see cref="RollbackWriter"/> in one transaction guarded by the project's
/// <c>xmin</c>) and the rollback history. The checkpoint picker is in <see cref="Versions.VersionService"/>.
/// </summary>
public sealed partial class RollbackService(AideDbContext db, RollbackWriter writer, TimeProvider time, ILogger<RollbackService> logger)
{
    /// <summary>
    /// Rolls the project back to checkpoint <paramref name="targetCheckpoint"/>. Checks, in order: <c>INVALID_TARGET_VERSION</c>,
    /// <c>ROLLBACK_BLOCKED</c>, <c>RATE_LIMITED</c> (with the retry-after time); a concurrent change is <c>CONFLICT</c>.
    /// </summary>
    public async Task<Result<RollbackDto>> RollbackAsync(Guid projectId, int targetCheckpoint, string reason, string actor, CancellationToken cancellationToken)
    {
        // The writer locks the project's jobs for the rest of this transaction; a failure disposes it (rolled back).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<RollbackDto>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        var applied = await writer.ApplyAsync(project, targetCheckpoint, reason, actor, time.GetUtcNow(), keepGateId: null, cancellationToken);
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
        LogRolledBack(logger, project.Id, record.FromCheckpoint, record.TargetCheckpoint, record.RestoredPhase);
        var actors = await ActorResolver.ForActorsAsync(db, [record.TriggeredBy], cancellationToken);
        return Result.Success(ToDto(record, actors));
    }

    /// <summary>Every rollback of the project, newest first.</summary>
    public async Task<IReadOnlyList<RollbackDto>> ListAsync(Guid projectId, CancellationToken cancellationToken)
    {
        // restored_phase is NOT NULL since the ReleaseVersions migration (backfilled there), so no read-time fallback is needed.
        var records = await db.RollbackEvents
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(cancellationToken);
        var actors = await ActorResolver.ForActorsAsync(db, records.Select(r => r.TriggeredBy), cancellationToken);
        return [.. records.Select(r => ToDto(r, actors))];
    }

    private static RollbackDto ToDto(RollbackEvent record, Func<string, ActorDto> actors) => new(
        record.Id,
        record.ReleaseVersionId,
        record.FromCheckpoint,
        record.TargetCheckpoint,
        record.RestoredPhase,
        record.Reason,
        record.InvalidatedArtifactIds,
        record.ResetStepIds,
        record.RequeuedStepIds,
        record.CancelledGateIds,
        record.CancelledJobIds,
        record.CreatedAt,
        actors(record.TriggeredBy));

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} rolled back from checkpoint {FromCheckpoint} to {TargetCheckpoint} ({Phase})")]
    private static partial void LogRolledBack(ILogger logger, Guid projectId, int fromCheckpoint, int targetCheckpoint, Aide.Domain.Lifecycle.Phase phase);
}
