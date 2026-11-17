using Aide.Domain.Agents;
using Aide.Domain.Jobs;
using Tevrin.Engine.Domain.Common;

namespace Aide.Api.Jobs;

/// <summary>
/// Payload of agent jobs started by a user (a refinement round, FR-27), by the lifecycle (FR-28, FR-29, FR-38), by a gate
/// rejection (FR-13), by an artifact rejection (FR-17) or by a failed integrity check (FR-20): the project, the project
/// version it was queued at, the reviewer's feedback, the gate or artifact that triggered the re-run, and the answers to
/// clarifying questions given for a refinement round.
/// </summary>
public sealed record LifecycleJobPayload(
    Guid ProjectId,
    int ProjectVersion,
    string Trigger,
    string? Feedback = null,
    Guid? GateId = null,
    Guid? ArtifactId = null,
    IReadOnlyList<RefinementAnswer>? Answers = null,
    DateTimeOffset? ChecksWaitSince = null,
    DateTimeOffset? PreviewWaitSince = null)
{
    /// <summary>FR-27: a writer started a refinement round (<see cref="Answers"/> are the answers given).</summary>
    public const string UserRequested = "user_requested";

    public const string PhaseEntered = "phase_entered";
    public const string GateRejected = "gate_rejected";

    /// <summary>The trigger as the domain enum (<see cref="AgentJobTrigger"/> has the same wire values), or <c>null</c>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public AgentJobTrigger? TriggerKind => EnumStrings.TryParse<AgentJobTrigger>(Trigger, out var kind) ? kind : null;

    /// <summary>FR-17: an Owner rejected the agent's draft; <see cref="Feedback"/> is the comment.</summary>
    public const string ArtifactRejected = "artifact_rejected";

    /// <summary>FR-20: the artifact's content no longer matched its hash; regenerate it.</summary>
    public const string ArtifactCorrupted = "artifact_corrupted";
}
