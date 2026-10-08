using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Jobs;

/// <summary>
/// The outbox side of §4.9: adds <c>jobs</c> rows to the current <see cref="AideDbContext"/> without saving, so a state
/// change, its events and its follow-up jobs commit together in the caller's <c>SaveChanges</c> (one transaction).
/// </summary>
public sealed class JobEnqueuer(AideDbContext db, TimeProvider time)
{
    /// <summary>
    /// Adds a queued job created at <paramref name="now"/> (default: the clock) and runnable then, or at <paramref name="runAfter"/>. For <see cref="JobTypes.Execution"/> there is
    /// at most one queued or running job per project (§4.9, partial unique index): if one already exists (saved, or
    /// added in this unit of work) it is returned instead of adding another.
    /// </summary>
    public async Task<Job> EnqueueAsync(string type, object payload, Guid? projectId, CancellationToken cancellationToken, DateTimeOffset? now = null, DateTimeOffset? runAfter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(payload);

        if (type == JobTypes.Execution && projectId is { } id)
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

        var createdAt = now ?? time.GetUtcNow();
        var job = new Job
        {
            Type = type,
            Payload = AideJson.Serialize(payload),
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

/// <summary>
/// Payload of agent jobs started by the lifecycle (FR-28, FR-29, FR-38), by a gate rejection (FR-13), by an artifact
/// rejection (FR-17) or by a failed integrity check (FR-20): the project, the project version it was queued at, the
/// reviewer's feedback, and the gate or artifact that triggered the re-run.
/// </summary>
public sealed record LifecycleJobPayload(
    Guid ProjectId,
    int ProjectVersion,
    string Trigger,
    string? Feedback = null,
    Guid? GateId = null,
    Guid? ArtifactId = null)
{
    public const string PhaseEntered = "phase_entered";
    public const string GateRejected = "gate_rejected";

    /// <summary>FR-17: an Owner rejected the agent's draft; <see cref="Feedback"/> is the comment.</summary>
    public const string ArtifactRejected = "artifact_rejected";

    /// <summary>FR-20: the artifact's content no longer matched its hash; regenerate it.</summary>
    public const string ArtifactCorrupted = "artifact_corrupted";
}
