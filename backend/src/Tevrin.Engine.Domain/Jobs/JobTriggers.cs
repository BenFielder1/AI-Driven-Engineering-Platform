using System.Diagnostics.CodeAnalysis;

namespace Tevrin.Engine.Domain.Jobs;

/// <summary>
/// FR-273: why an agent job was queued, as stored in its payload's <c>trigger</c>. These strings replace the 0.6.1
/// <c>AgentJobTrigger</c> enum with the same values, except that a job queued on entering a stage writes
/// <see cref="StageEntered"/> where 0.6.1 wrote <see cref="LegacyPhaseEntered"/>; a stored payload that says
/// <see cref="LegacyPhaseEntered"/> is read as <see cref="StageEntered"/> (<see cref="Read"/>). Packs may use other
/// trigger strings for their own jobs; those are stored as written.
/// </summary>
public static class JobTriggers
{
    /// <summary>A user started the job (e.g. a refinement round, FR-27).</summary>
    public const string UserRequested = "user_requested";

    /// <summary>The project entered the stage that runs the agent (FR-28, FR-29, FR-38, FR-42).</summary>
    public const string StageEntered = "stage_entered";

    /// <summary>An Owner rejected the gate on the agent's artifact (FR-13).</summary>
    public const string GateRejected = "gate_rejected";

    /// <summary>An Owner rejected the agent's draft (FR-17).</summary>
    public const string ArtifactRejected = "artifact_rejected";

    /// <summary>The agent's artifact failed its integrity check and is regenerated (FR-20).</summary>
    public const string ArtifactCorrupted = "artifact_corrupted";

    /// <summary>The agent's output failed validation and the agent is re-run.</summary>
    public const string ValidationFailed = "validation_failed";

    /// <summary>The 0.6.1 value of <see cref="StageEntered"/>, still found in stored payloads; never written.</summary>
    public const string LegacyPhaseEntered = "phase_entered";

    /// <summary>Every generic trigger (the values new payloads write).</summary>
    public static IReadOnlyList<string> All { get; } =
        [UserRequested, StageEntered, GateRejected, ArtifactRejected, ArtifactCorrupted, ValidationFailed];

    /// <summary>
    /// The trigger a stored payload means: <see cref="LegacyPhaseEntered"/> reads as <see cref="StageEntered"/>; every
    /// other value (and <c>null</c>) is returned unchanged.
    /// </summary>
    [return: NotNullIfNotNull(nameof(trigger))]
    public static string? Read(string? trigger) =>
        string.Equals(trigger, LegacyPhaseEntered, StringComparison.Ordinal) ? StageEntered : trigger;
}
