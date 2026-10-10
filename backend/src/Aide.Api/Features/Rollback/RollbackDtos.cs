using System.ComponentModel.DataAnnotations;
using Aide.Api.Features.Activity;
using Aide.Domain.Lifecycle;

namespace Aide.Api.Features.Rollback;

/// <summary>
/// Roll the project back to an earlier project version (FR-44, §4.8). <c>targetVersion</c> must be below the current
/// version, recorded, and not a release-phase version; <c>reason</c> is recorded on the rollback and its state transition.
/// </summary>
public sealed record RollbackRequest
{
    [Range(1, int.MaxValue)]
    public required int TargetVersion { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(RollbackLimits.ReasonMaxLength)]
    public required string Reason { get; init; }
}

/// <summary>
/// A recorded rollback (FR-44, §4.8): from <c>fromVersion</c> to <c>toVersion</c>, restoring <c>restoredPhase</c>.
/// <c>invalidatedArtifactIds</c> are the artifacts created after the target version; <c>resetStepIds</c> the steps of
/// invalidated plans (now <c>invalidated</c>); <c>requeuedStepIds</c> the blocked or rejected steps of surviving plans
/// (back to <c>pending</c>); <c>cancelledGateIds</c> and <c>cancelledJobIds</c> the pending gates and queued jobs it
/// cancelled. <c>createdBy</c> is the Owner (or the system, for an escalation decision recorded by a worker).
/// </summary>
public sealed record RollbackDto(
    Guid Id,
    int FromVersion,
    int ToVersion,
    Phase RestoredPhase,
    string Reason,
    IReadOnlyList<Guid> InvalidatedArtifactIds,
    IReadOnlyList<Guid> ResetStepIds,
    IReadOnlyList<Guid> RequeuedStepIds,
    IReadOnlyList<Guid> CancelledGateIds,
    IReadOnlyList<Guid> CancelledJobIds,
    DateTimeOffset CreatedAt,
    ActorDto CreatedBy);

/// <summary>
/// A project version from the state transitions (FR-44), for the rollback target picker: the phase entered at that version
/// and when. <c>rollbackAllowed</c> says whether it is a valid rollback target now; when it isn't,
/// <c>rollbackBlockedReason</c> says why (<c>current_version</c>, <c>release_phase</c>). Rate limits and running work
/// (RATE_LIMITED, ROLLBACK_BLOCKED) are not reflected here: they are checked when the rollback is requested.
/// </summary>
public sealed record ProjectVersionDto(
    int Version,
    Phase Phase,
    DateTimeOffset EnteredAt,
    bool RollbackAllowed,
    RollbackTargetBlock? RollbackBlockedReason);

/// <summary>Why a project version can't be a rollback target (FR-44, §4.8 and the release-phase decision).</summary>
public enum RollbackTargetBlock
{
    /// <summary>It is the current version: a rollback needs an earlier one.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("current_version")]
    CurrentVersion = 0,

    /// <summary>The version is in the release phase; restoring it would leave an unrecoverable release/rolled_back state.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("release_phase")]
    ReleasePhase = 1,
}

/// <summary>Request limits of the rollback feature.</summary>
public static class RollbackLimits
{
    public const int ReasonMaxLength = 500;
}
