namespace Aide.Domain.Jobs;

public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Dead = 4,
    Cancelled = 5,

    /// <summary>A dead letter that an Admin retried; a new queued job replaced it (FR-51).</summary>
    Retried = 6,
}
