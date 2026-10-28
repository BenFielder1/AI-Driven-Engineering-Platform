using System.ComponentModel.DataAnnotations;
using Aide.Api.Features.Activity;
using Aide.Api.Features.Projects;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;

namespace Aide.Api.Features.Versions;

/// <summary>
/// The lifecycle checkpoints of a release version (§4.10): <c>current</c> is the project's checkpoint for the version in
/// development (or the latest checkpoint recorded for any other version); <c>max</c> the highest checkpoint recorded in it
/// (above <c>current</c> after a rollback).
/// </summary>
public sealed record ReleaseVersionCheckpointsDto(int Current, int Max);

/// <summary>
/// A release version of a project (§4.10, FR-60–FR-62). <c>version</c> is the normalised semver (no leading <c>v</c>);
/// <c>tag</c> is the release tag once released (or the imported/migrated tag, e.g. v1's <c>v7-aide</c>); <c>baseTag</c> and
/// <c>baseCommitSha</c> are the released tag and merge commit the version started from (null for a first version);
/// <c>branchName</c> and <c>pullRequestUrl</c> are the version's working branch and PR once they exist; <c>request</c> is
/// what the version was started with (the project idea for a first version); <c>startedBy</c> is null for versions created
/// by migration or import. <c>presetId</c>/<c>presetVersion</c>/<c>presetName</c> are history (FR-129): the retired v2 preset
/// the version was pinned to before its migration to a composition, shown as "Preset &lt;id&gt;@&lt;version&gt; (retired,
/// migrated)"; all <c>null</c> for versions never pinned to a preset.
/// </summary>
public sealed record ReleaseVersionDto(
    Guid Id,
    string Version,
    ReleaseVersionStatus Status,
    string? Tag,
    string TagPrefix,
    string? BaseTag,
    string? BaseCommitSha,
    string? MergeCommitSha,
    string? BranchName,
    string? PullRequestUrl,
    ReleaseVersionCheckpointsDto Checkpoints,
    string? PresetId,
    string? PresetVersion,
    string? PresetName,
    string Request,
    ActorDto? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReleasedAt);

/// <summary>
/// FR-60: start a new release version. <c>version</c> is semver (a leading <c>v</c> is stripped, e.g. <c>v1.2.0</c> →
/// <c>1.2.0</c>); <c>request</c> describes the new features, improvements or fixes and becomes the project idea for this
/// version. <c>stack</c> (FR-117,
/// "Change stack") is the new version's stack composition, validated like project create (<c>STACK_INCOMPATIBLE</c>,
/// <c>VCS_MISMATCH</c>, <c>VALIDATION</c> on <c>stack.*</c>); omitted or <c>null</c>, the base version's composition is kept,
/// re-pinned to the current catalog (<c>STACK_INCOMPATIBLE</c> if the catalog no longer allows it). FR-129: the v2
/// <c>presetId</c> was removed (D-V4-4).
/// </summary>
public sealed record StartReleaseVersionRequest
{
    [Required]
    [StringLength(VersionLimits.VersionMaxLength, MinimumLength = 1)]
    public required string Version { get; init; }

    [Required]
    [StringLength(ProjectLimits.IdeaMaxLength, MinimumLength = 1)]
    public required string Request { get; init; }

    /// <summary>FR-117: the new version's stack composition (omitted: the base version's, re-pinned).</summary>
    public Stack.StackCompositionRequest? Stack { get; init; }
}

/// <summary>FR-60: the release version that was started and the project state (now refinement / active at checkpoint 1).</summary>
public sealed record StartReleaseVersionResultDto(ReleaseVersionDto ReleaseVersion, ProjectStateDto State);

/// <summary>
/// A lifecycle checkpoint of a release version (FR-44, FR-63), for the rollback target picker: the phase entered at that
/// checkpoint and when. <c>rollbackAllowed</c> says whether it is a valid rollback target now; when it isn't,
/// <c>rollbackBlockedReason</c> says why (<c>current_checkpoint</c>, <c>release_phase</c>, or <c>version_released</c> for
/// every checkpoint of a released version). Rate limits and running work (RATE_LIMITED, ROLLBACK_BLOCKED) are checked when
/// the rollback is requested.
/// </summary>
public sealed record CheckpointDto(
    int Checkpoint,
    Phase Phase,
    DateTimeOffset EnteredAt,
    bool RollbackAllowed,
    CheckpointBlock? RollbackBlockedReason);

/// <summary>Why a checkpoint can't be a rollback target (FR-44, FR-63, §4.8, §4.10).</summary>
public enum CheckpointBlock
{
    /// <summary>It is the current checkpoint: a rollback needs an earlier one.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("current_checkpoint")]
    CurrentCheckpoint = 0,

    /// <summary>The checkpoint is in the release phase; restoring it would leave an unrecoverable release/rolled_back state.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("release_phase")]
    ReleasePhase = 1,

    /// <summary>The release version is released and closed (D-V2-3): start a new version instead.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("version_released")]
    VersionReleased = 2,
}

/// <summary>Request limits of the versions feature.</summary>
public static class VersionLimits
{
    /// <summary>The version text, with room for a leading <c>v</c> and whitespace.</summary>
    public const int VersionMaxLength = SemVer.MaxLength + 8;
}
