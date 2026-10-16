using System.ComponentModel.DataAnnotations;
using Aide.Api.Features.Activity;
using Aide.Domain.Artifacts;

namespace Aide.Api.Features.Artifacts;

/// <summary>
/// Who produced an artifact version (FR-16): a user (<c>user</c>, for drafts edited in the browser or uploaded via the
/// CLI) or an agent run (<c>agentRunId</c>). Exactly one of the two is set.
/// </summary>
public sealed record ArtifactAuthorDto(ActorDto? User, Guid? AgentRunId);

/// <summary>
/// One artifact version without its content (FR-16, FR-21, FR-59): the artifacts page cards and the history list.
/// <c>integrity</c> is the result of re-checking the content hash on this read (FR-20); <c>releaseVersionId</c> is the
/// release version it belongs to (§4.10) and <c>releaseVersion</c> its semver (FR-59). <c>restoredFromVersion</c> is the
/// version whose content a restore copied (FR-58), else <c>null</c>.
/// </summary>
public sealed record ArtifactSummaryDto(
    Guid Id,
    ArtifactType Type,
    int Version,
    ArtifactStatus Status,
    Guid ReleaseVersionId,
    ArtifactIntegrity Integrity,
    DateTimeOffset CreatedAt,
    ArtifactAuthorDto Author,
    string ReleaseVersion,
    int? RestoredFromVersion);

/// <summary>
/// One artifact version with its Markdown content (FR-16, §4.3). <c>contentHash</c> is the lowercase hex SHA-256 of the
/// normalised content; <c>integrity</c> is <c>corrupted</c> when it no longer matches (FR-20). <c>approvedBy</c> and
/// <c>approvedAt</c> are set once approved (status <c>locked</c>); <c>rejectionComment</c> once rejected (FR-17).
/// <c>releaseVersionId</c> and <c>checkpointAtCreation</c> say where it was created (§4.10; D-V2-11: was
/// <c>projectVersionAtCreation</c>); <c>releaseVersion</c> is that release version's semver (FR-59).
/// <c>restoredFromVersion</c> is the version whose content a restore copied (FR-58), else <c>null</c>.
/// </summary>
public sealed record ArtifactDto(
    Guid Id,
    ArtifactType Type,
    int Version,
    int? ParentVersion,
    string Content,
    string ContentHash,
    ArtifactStatus Status,
    Guid ReleaseVersionId,
    int CheckpointAtCreation,
    ArtifactAuthorDto Author,
    ActorDto? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? RejectionComment,
    DateTimeOffset CreatedAt,
    ArtifactIntegrity Integrity,
    string ReleaseVersion,
    int? RestoredFromVersion);

/// <summary>
/// One line of a diff between two versions (FR-21). <c>oldLine</c> is the 1-based line number in the <c>from</c> version
/// (null for <c>added</c>), <c>newLine</c> the line number in the <c>to</c> version (null for <c>removed</c>); <c>text</c>
/// is the line without its line ending. Lines are in order, so a side-by-side view pairs them by line numbers.
/// A <c>skipped</c> entry stands for <c>skipped</c> unchanged lines (more than 3 lines from any change) that are left
/// out: <c>oldLine</c>/<c>newLine</c> are the numbers of the first of them and <c>text</c> is empty; <c>skipped</c> is
/// null for every other kind.
/// </summary>
public sealed record DiffLineDto(DiffLineKind Kind, int? OldLine, int? NewLine, string Text, int? Skipped);

/// <summary>
/// A line diff between versions <c>from</c> and <c>to</c> of one artifact type (FR-21): <c>unifiedDiff</c> is the
/// unified-diff text (3 lines of context) and <c>lines</c> the same hunks as context/added/removed lines, with each
/// unchanged run outside them collapsed into one <c>skipped</c> entry, for side-by-side rendering. Versions with more
/// than 100,000 lines together are not diffed (400 <c>VALIDATION</c>).
/// </summary>
public sealed record ArtifactDiffDto(int From, int To, string UnifiedDiff, IReadOnlyList<DiffLineDto> Lines);

/// <summary>A new draft version of an artifact type from edited Markdown (FR-18).</summary>
public sealed record CreateArtifactDraftRequest
{
    [Required]
    [StringLength(ArtifactLimits.ContentMaxLength, MinimumLength = 1)]
    public required string Content { get; init; }

    /// <summary>
    /// The version the edit started from. When set, the save is refused with 409 <c>CONFLICT</c> (details
    /// <c>latestVersion: N</c>) unless it is still the latest version of the type, so a version saved meanwhile is
    /// never silently superseded. Omit it to always save (CLI uploads).
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? BaseVersion { get; init; }
}

/// <summary>Approve or reject a draft (FR-17). On reject, <c>comment</c> is recorded and fed to the re-run agent.</summary>
public sealed record DecideArtifactRequest
{
    [MaxLength(ArtifactLimits.CommentMaxLength)]
    public string? Comment { get; init; }
}

/// <summary>Request limits of the artifacts feature.</summary>
public static class ArtifactLimits
{
    /// <summary>Upper bound on draft Markdown, in characters.</summary>
    public const int ContentMaxLength = 1_000_000;

    public const int CommentMaxLength = 4000;
}
