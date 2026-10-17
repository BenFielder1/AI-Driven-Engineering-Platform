using Aide.Domain.Common;
using Aide.Domain.Lifecycle;

namespace Aide.Domain.Artifacts;

/// <summary>
/// What FR-58 needs to decide a restore of version <see cref="ChosenVersion"/> of <see cref="Type"/>.
/// </summary>
/// <param name="Type">The artifact type.</param>
/// <param name="ProjectPhase">The project's current phase.</param>
/// <param name="ProjectStatus">The project's status.</param>
/// <param name="DevelopmentReleaseVersionId">The release version in development, or <c>null</c> when nothing is.</param>
/// <param name="ChosenReleaseVersionId">The release version the chosen version belongs to.</param>
/// <param name="ChosenVersion">The version to restore.</param>
/// <param name="ChosenIntegrity">The chosen version's integrity, re-checked on this read (FR-20).</param>
/// <param name="LatestVersion">The highest version of the type (version numbers are project-wide).</param>
public sealed record RestoreFacts(
    ArtifactType Type,
    Phase ProjectPhase,
    ProjectStatus ProjectStatus,
    Guid? DevelopmentReleaseVersionId,
    Guid ChosenReleaseVersionId,
    int ChosenVersion,
    ArtifactIntegrity ChosenIntegrity,
    int LatestVersion);

/// <summary>The restored draft's numbering (FR-58): version = latest + 1, parent = latest, restored from the chosen version.</summary>
public sealed record RestorePlan(int Version, int ParentVersion, int RestoredFromVersion);

/// <summary>
/// FR-58, D-V2-15: restoring an older artifact version creates a new draft with its content; history is never rewritten.
/// </summary>
public static class RestoreRules
{
    /// <summary>
    /// §4.12 "Producing phase per artifact type": the phase in which a type can be restored, or <c>null</c> for
    /// <c>codebase_summary</c>, which is produced automatically in refinement and is never restorable.
    /// </summary>
    public static Phase? RestorablePhase(ArtifactType type) =>
        type == ArtifactType.CodebaseSummary ? null : ArtifactRules.ProducingPhase(type);

    /// <summary>
    /// Allowed only while the project is not <c>releasing</c> (<c>releasing</c>), is in the phase that produces the type
    /// (<c>stage_not_reached</c> before it, e.g. after a rollback; <c>stage_completed</c> after it), and the chosen version belongs to the release version in development
    /// (<c>other_release_version</c>) and is not corrupted (<c>corrupted</c>) — each <c>RESTORE_NOT_ALLOWED</c> with that
    /// reason. Restoring the latest version is <c>VALIDATION</c> (there is nothing to restore).
    /// </summary>
    public static Result<RestorePlan> Check(RestoreFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.ProjectStatus is ProjectStatus.Releasing or ProjectStatus.AwaitingMerge)
        {
            return NotAllowed(RestoreReasons.Releasing, "A release is in progress; versions can't be restored until it finishes.");
        }

        var phase = RestorablePhase(facts.Type);
        if (phase is null)
        {
            return NotAllowed(RestoreReasons.StageCompleted, $"The {facts.Type.ToWire()} is produced automatically and can't be restored.");
        }

        if (facts.ProjectPhase.IsBefore(phase.Value))
        {
            return NotAllowed(
                RestoreReasons.StageNotReached,
                $"The {facts.Type.ToWire()} is produced in {phase.Value.ToWire()}; the project is still in {facts.ProjectPhase.ToWire()}.");
        }

        if (facts.ProjectPhase != phase)
        {
            return NotAllowed(
                RestoreReasons.StageCompleted,
                $"The {facts.Type.ToWire()} can only be restored in {phase.Value.ToWire()}; the project is in {facts.ProjectPhase.ToWire()}. Roll back the project to change it.");
        }

        if (facts.DevelopmentReleaseVersionId is not { } development || facts.ChosenReleaseVersionId != development)
        {
            return NotAllowed(
                RestoreReasons.OtherReleaseVersion,
                $"{facts.Type.ToWire()} v{facts.ChosenVersion} doesn't belong to the release version in development.");
        }

        if (facts.ChosenVersion >= facts.LatestVersion)
        {
            return Result.Failure<RestorePlan>(new DomainError(
                ErrorCodes.Validation,
                $"{facts.Type.ToWire()} v{facts.ChosenVersion} is already the latest version; there is nothing to restore.")
            {
                Details = ["version: already the latest version"],
            });
        }

        if (facts.ChosenIntegrity == ArtifactIntegrity.Corrupted)
        {
            return NotAllowed(RestoreReasons.Corrupted, $"{facts.Type.ToWire()} v{facts.ChosenVersion} is corrupted and can't be restored.");
        }

        return Result.Success(new RestorePlan(facts.LatestVersion + 1, facts.LatestVersion, facts.ChosenVersion));
    }

    private static Result<RestorePlan> NotAllowed(string reason, string message) =>
        Result.Failure<RestorePlan>(new DomainError(ErrorCodes.RestoreNotAllowed, message) { Reason = reason });
}
