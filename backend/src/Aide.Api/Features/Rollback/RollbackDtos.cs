using System.ComponentModel.DataAnnotations;
using Aide.Api.Features.Activity;
using Aide.Domain.Lifecycle;

namespace Aide.Api.Features.Rollback;

/// <summary>
/// Roll the project back to an earlier checkpoint (FR-44, FR-63, §4.8, §4.10). <c>targetCheckpoint</c> must be below the
/// current checkpoint, recorded in the release version in development, and not a release-phase checkpoint; <c>reason</c> is
/// recorded on the rollback and its state transition.
/// </summary>
public sealed record RollbackRequest
{
    [Range(1, int.MaxValue)]
    public required int TargetCheckpoint { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(RollbackLimits.ReasonMaxLength)]
    public required string Reason { get; init; }
}

/// <summary>
/// A recorded rollback (FR-44, §4.8): from checkpoint <c>fromCheckpoint</c> to <c>toCheckpoint</c> of release version
/// <c>releaseVersionId</c>, restoring <c>restoredPhase</c>. <c>invalidatedArtifactIds</c> are the artifacts created after the target checkpoint; <c>resetStepIds</c> the steps of
/// invalidated plans (now <c>invalidated</c>); <c>requeuedStepIds</c> the blocked or rejected steps of surviving plans
/// (back to <c>pending</c>); <c>cancelledGateIds</c> and <c>cancelledJobIds</c> the pending gates and queued jobs it
/// cancelled. <c>createdBy</c> is the Owner (or the system, for an escalation decision recorded by a worker).
/// </summary>
public sealed record RollbackDto(
    Guid Id,
    Guid ReleaseVersionId,
    int FromCheckpoint,
    int ToCheckpoint,
    Phase RestoredPhase,
    string Reason,
    IReadOnlyList<Guid> InvalidatedArtifactIds,
    IReadOnlyList<Guid> ResetStepIds,
    IReadOnlyList<Guid> RequeuedStepIds,
    IReadOnlyList<Guid> CancelledGateIds,
    IReadOnlyList<Guid> CancelledJobIds,
    DateTimeOffset CreatedAt,
    ActorDto CreatedBy);

/// <summary>Request limits of the rollback feature.</summary>
public static class RollbackLimits
{
    public const int ReasonMaxLength = 500;
}
