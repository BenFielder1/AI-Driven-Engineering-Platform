using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aide.Api.Features.Admin;

/// <summary>
/// FR-51: retrying a dead letter. In one transaction, with the dead job's row locked so two Admins can't both retry it:
/// the dead job becomes <c>retried</c>, a new queued job with the same type, payload and project is added
/// (<see cref="JobPolicy.RetryDeadLetter"/>), and a <c>DEAD_LETTER_RETRIED</c> audit event is written.
/// </summary>
public sealed partial class DeadLetterService(AideDbContext db, ICurrentUser currentUser, TimeProvider time, ILogger<DeadLetterService> logger)
{
    /// <summary>The new queued job; <c>NOT_FOUND</c> when the job doesn't exist or isn't dead, <c>CONFLICT</c> when the
    /// project already has an active execution job (§4.9).</summary>
    public async Task<Result<Job>> RetryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.Jobs
            .FromSql($"SELECT * FROM jobs WHERE id = {jobId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var dead = rows.SingleOrDefault();
        if (dead is null || dead.Status != JobStatus.Dead)
        {
            return Result.Failure<Job>(new DomainError(ErrorCodes.NotFound, "The dead letter was not found."));
        }

        var executionActive = dead.Type == JobTypes.Execution && dead.ProjectId is { } projectId
            && await db.Jobs.AnyAsync(
                j => j.ProjectId == projectId && j.Type == JobTypes.Execution && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
                cancellationToken);
        var now = time.GetUtcNow();
        var retry = JobPolicy.RetryDeadLetter(new JobSnapshot(dead.Id, dead.Type, dead.Payload, dead.ProjectId, dead.Status), now, executionActive);
        if (retry.IsFailure)
        {
            return Result.Failure<Job>(retry.Error!);
        }

        var replacement = retry.Value.Replacement;
        dead.Status = retry.Value.DeadJobStatus;
        var job = new Job
        {
            Type = replacement.Type,
            Payload = replacement.Payload,
            ProjectId = replacement.ProjectId,
            Status = replacement.Status,
            Attempts = replacement.Attempts,
            RunAfter = replacement.RunAfter,
            CreatedAt = now,
        };
        db.Jobs.Add(job);
        db.AddEvent(EventTypes.DeadLetterRetried, dead.ProjectId, new { deadJobId = dead.Id, jobId = job.Id, jobType = dead.Type }, currentUser.Actor, now);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent request queued an execution job for the project after the check (partial unique index, §4.9).
            return Result.Failure<Job>(new DomainError(
                ErrorCodes.Conflict,
                "An execution job is already queued or running for this project; retry this dead letter after it finishes."));
        }

        await transaction.CommitAsync(cancellationToken);
        LogRetried(logger, dead.Id, job.Id, dead.Type, dead.ProjectId);
        return Result.Success(job);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dead letter {DeadJobId} ({JobType}, project {ProjectId}) retried as job {NewJobId}")]
    private static partial void LogRetried(ILogger logger, Guid deadJobId, Guid newJobId, string jobType, Guid? projectId);
}
