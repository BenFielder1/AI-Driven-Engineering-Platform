using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Runtime.Jobs;

/// <summary>
/// The outbox side of §4.9: adds <c>jobs</c> rows to the current <see cref="IEngineDbContext"/> without saving, so a state
/// change, its events and its follow-up jobs commit together in the caller's <c>SaveChanges</c> (one transaction).
/// </summary>
public sealed class JobEnqueuer(IEngineDbContext db, TimeProvider time, IJobPayloadSerializer serializer)
{
    /// <summary>
    /// Adds a queued job created at <paramref name="now"/> (default: the clock) and runnable then, or at <paramref name="runAfter"/>. For <see cref="Job.ExecutionType"/> there is
    /// at most one queued or running job per project (§4.9, partial unique index): if one already exists (saved, or
    /// added in this unit of work) it is returned instead of adding another.
    /// </summary>
    public async Task<Job> EnqueueAsync(string type, object payload, Guid? projectId, CancellationToken cancellationToken, DateTimeOffset? now = null, DateTimeOffset? runAfter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(payload);

        if (type == Job.ExecutionType && projectId is { } id)
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
    /// As <see cref="EnqueueAsync"/> for job types other than <see cref="Job.ExecutionType"/> (no uniqueness check, so no
    /// query): adds the queued job to the unit of work.
    /// </summary>
    public Job Enqueue(string type, object payload, Guid? projectId, DateTimeOffset? now = null, DateTimeOffset? runAfter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(payload);
        if (type == Job.ExecutionType)
        {
            throw new InvalidOperationException("Execution jobs are unique per project: use EnqueueAsync.");
        }

        return Add(type, payload, projectId, now, runAfter);
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
}
