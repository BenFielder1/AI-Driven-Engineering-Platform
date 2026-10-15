using Aide.Domain.Entities;

namespace Aide.Api.Features.Release;

/// <summary>
/// A release attempt (FR-42 amended by FR-64, FR-43) of release version <c>releaseVersionId</c>, approved at checkpoint
/// <c>checkpoint</c>. <c>status</c> is <c>in_progress</c> while the release job is queued or running, then
/// <c>succeeded</c> (merged into the default branch and tagged <c>tag</c>, <c>mergeCommitSha</c> set) or <c>failed</c>.
/// A failed release carries <c>errorCode</c> (e.g. <c>MERGE_CONFLICT</c>) and <c>errorMessage</c>; on a merge conflict
/// <c>conflictingFiles</c> lists the files (empty otherwise). <c>pullRequestUrl</c> is the merged GitHub PR (<c>null</c>
/// for Local). <c>attempt</c> counts the release attempts at this checkpoint (1 for the first, +1 per retry).
/// <c>completedAt</c> is <c>null</c> while in progress. <c>tag</c> is the version's tag <c>&lt;tagPrefix&gt;&lt;version&gt;</c>
/// (e.g. <c>v1.2.0</c>; v1 releases keep their <c>v&lt;N&gt;-aide</c> tag). A tag that already exists on another commit fails
/// the release with <c>TAG_EXISTS</c>. <c>mergeCommitSha</c> is set as soon as the merge exists, before tagging.
/// </summary>
public sealed record ReleaseDto(
    Guid Id,
    Guid ReleaseVersionId,
    int Checkpoint,
    ReleaseStatus Status,
    string Tag,
    string? MergeCommitSha,
    string? PullRequestUrl,
    IReadOnlyList<string> ConflictingFiles,
    string? ErrorCode,
    string? ErrorMessage,
    int Attempt,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>The latest release attempt in the project state (FR-42, FR-43): enough for the overview's release banner.</summary>
public sealed record ReleaseSummaryDto(Guid Id, Guid ReleaseVersionId, int Checkpoint, ReleaseStatus Status, string Tag, DateTimeOffset StartedAt);
