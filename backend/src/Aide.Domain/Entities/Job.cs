using Aide.Domain.Jobs;

namespace Aide.Domain.Entities;

/// <summary>Outbox + work-queue row (§4.9, D1). Retry and lease policy live in <c>JobPolicy</c>.</summary>
public sealed class Job
{
    /// <summary>Job type of execution jobs; at most one queued/running per project (§4.9), enforced by a partial unique index.</summary>
    public const string ExecutionType = "execution";

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Type { get; set; } = string.Empty;

    /// <summary>Job payload as raw JSON (schemaless).</summary>
    public string Payload { get; set; } = "{}";

    public Guid? ProjectId { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;

    public int Attempts { get; set; }

    public DateTimeOffset RunAfter { get; set; }

    public string? LockedBy { get; set; }

    /// <summary>When the current worker claimed the job; the 5-minute lease (NFR-REL) runs from here.</summary>
    public DateTimeOffset? LockedAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
