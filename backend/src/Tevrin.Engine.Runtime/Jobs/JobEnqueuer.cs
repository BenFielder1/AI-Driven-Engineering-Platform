using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Runtime.Jobs;

/// <summary>
/// The outbox side of §4.9: adds <c>jobs</c> rows to the current <see cref="IEngineDbContext"/> without saving, so a state
/// change, its events and its follow-up jobs commit together in the caller's <c>SaveChanges</c> (one transaction).
/// FR-272: only a type with a registered handler (<see cref="JobTypeRegistry"/>) can be enqueued; any other type is logged
/// as an error and throws, so the mistake surfaces at enqueue rather than as a job no worker ever claims.
/// </summary>
public sealed partial class JobEnqueuer(
    IEngineDbContext db, TimeProvider time, IJobPayloadSerializer serializer, JobTypeRegistry types, ILogger<JobEnqueuer> logger)
{
    /// <summary>
    /// Adds a queued job created at <paramref name="now"/> (default: the clock) and runnable then, or at <paramref name="runAfter"/>.
    /// For a one-per-project type (<see cref="JobHandlerRegistration.OnePerProject"/>, §4.9, enforced by a partial unique
    /// index on the host side) there is at most one queued or running job per project: if one already exists (saved, or
    /// added in this unit of work) it is returned instead of adding another.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="type"/> has no registered handler (FR-272).</exception>
    public async Task<Job> EnqueueAsync(string type, object payload, Guid? projectId, CancellationToken cancellationToken, DateTimeOffset? now = null, DateTimeOffset? runAfter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(payload);
        EnsureRegistered(type);

        if (types.IsOnePerProject(type) && projectId is { } id)
        {
            var existing = db.ChangeTracker.Entries<Job>()
                .Where(e => e.State == EntityState.Added && e.Entity.Type == type && e.Entity.ProjectId == id)
                .Select(e => e.Entity)
                .FirstOrDefault()
                ?? await db.Jobs.FirstOrDefaultAsync(
                    j => j.ProjectId == id && j.Type == type && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
                    cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        return Add(type, payload, projectId, now, runAfter);
    }

    /// <summary>
    /// As <see cref="EnqueueAsync"/> for job types that aren't one per project (no uniqueness check, so no query): adds the
    /// queued job to the unit of work.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="type"/> has no registered handler (FR-272), or is one
    /// per project (use <see cref="EnqueueAsync"/>).</exception>
    public Job Enqueue(string type, object payload, Guid? projectId, DateTimeOffset? now = null, DateTimeOffset? runAfter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(payload);
        EnsureRegistered(type);
        if (types.IsOnePerProject(type))
        {
            throw new InvalidOperationException($"Jobs of type '{type}' are unique per project: use EnqueueAsync.");
        }

        return Add(type, payload, projectId, now, runAfter);
    }

    private void EnsureRegistered(string type)
    {
        if (!types.IsRegistered(type))
        {
            LogUnregistered(logger, type);
            throw new InvalidOperationException($"No handler is registered for job type '{type}', so it can't be enqueued.");
        }
    }

    private Job Add(string type, object payload, Guid? projectId, DateTimeOffset? now, DateTimeOffset? runAfter)
    {
        var createdAt = now ?? time.GetUtcNow();
        var job = new Job
        {
            Type = type,
            Payload = serializer.Serialize(payload),
            ProjectId = projectId,
            Status = JobStatus.Queued,
            Attempts = 0,
            RunAfter = runAfter ?? createdAt,
            CreatedAt = createdAt,
        };
        db.Jobs.Add(job);
        return job;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Can't enqueue a {JobType} job: no handler is registered for that job type")]
    private static partial void LogUnregistered(ILogger logger, string jobType);
}
