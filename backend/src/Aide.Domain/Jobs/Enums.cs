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

/// <summary>
/// Why an agent job was queued (FR-13, FR-17, FR-20, FR-27–FR-29). The wire values are stored in job payloads
/// (<c>trigger</c>), so they never change once used.
/// </summary>
public enum AgentJobTrigger
{
    /// <summary>A writer started a refinement round (FR-27).</summary>
    UserRequested = 0,

    /// <summary>The project entered the agent's phase (FR-28, FR-29).</summary>
    PhaseEntered = 1,

    /// <summary>An Owner rejected the gate on the agent's artifact (FR-13).</summary>
    GateRejected = 2,

    /// <summary>An Owner rejected the agent's draft (FR-17).</summary>
    ArtifactRejected = 3,

    /// <summary>The agent's artifact failed its integrity check and is regenerated (FR-20).</summary>
    ArtifactCorrupted = 4,
}
