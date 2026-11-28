using Tevrin.Domain.Agents;
using Tevrin.Engine.Domain.Jobs;

namespace Tevrin.Api.Jobs;

/// <summary>
/// Payload of agent jobs started by a user (a refinement round, FR-27), by the lifecycle (FR-28, FR-29, FR-38), by a gate
/// rejection (FR-13), by an artifact rejection (FR-17) or by a failed integrity check (FR-20): the project, the project
/// version it was queued at, the reviewer's feedback, the gate or artifact that triggered the re-run, and the answers to
/// clarifying questions given for a refinement round.
/// <see cref="Trigger"/> is one of <see cref="JobTriggers"/> (FR-273), or another string a job of its own writes (e.g. the
/// release's <c>retried</c>). A stored 0.6.1 payload that says <c>phase_entered</c> reads as <see cref="StageEntered"/>.
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
    /// <summary>FR-273: the trigger, with the 0.6.1 <c>phase_entered</c> read as <see cref="StageEntered"/> (so a queued
    /// 0.6.1 job runs as before, and is written back with the generic name when it is re-serialised).</summary>
    public string Trigger
    {
        get => _trigger;
        init => _trigger = JobTriggers.Read(value);
    }

    private readonly string _trigger = JobTriggers.Read(Trigger);

    /// <summary>FR-27: a writer started a refinement round (<see cref="Answers"/> are the answers given).</summary>
    public const string UserRequested = JobTriggers.UserRequested;

    /// <summary>FR-28, FR-29, FR-38, FR-42: the project entered the phase whose job this is (0.6.1: <c>phase_entered</c>).</summary>
    public const string StageEntered = JobTriggers.StageEntered;

    /// <summary>FR-13: an Owner rejected the gate on the agent's artifact (<see cref="GateId"/>, <see cref="Feedback"/>).</summary>
    public const string GateRejected = JobTriggers.GateRejected;

    /// <summary>FR-17: an Owner rejected the agent's draft; <see cref="Feedback"/> is the comment.</summary>
    public const string ArtifactRejected = JobTriggers.ArtifactRejected;

    /// <summary>FR-20: the artifact's content no longer matched its hash; regenerate it.</summary>
    public const string ArtifactCorrupted = JobTriggers.ArtifactCorrupted;
}
