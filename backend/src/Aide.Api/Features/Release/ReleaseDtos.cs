using Aide.Domain.Entities;

namespace Aide.Api.Features.Release;

/// <summary>
/// A release attempt (FR-42, FR-43). <c>status</c> is <c>in_progress</c> while the release job is queued or running, then
/// <c>succeeded</c> (merged into the default branch and tagged <c>tag</c>, <c>mergeCommitSha</c> set) or <c>failed</c>.
/// A failed release carries <c>errorCode</c> (e.g. <c>MERGE_CONFLICT</c>) and <c>errorMessage</c>; on a merge conflict
/// <c>conflictingFiles</c> lists the files (empty otherwise). <c>pullRequestUrl</c> is the merged GitHub PR (<c>null</c>
/// for Local). <c>attempt</c> counts the release attempts for this project version (1 for the first, +1 per retry).
/// <c>completedAt</c> is <c>null</c> while in progress. <c>tag</c> is <c>v&lt;version&gt;-aide</c> until the release succeeds,
/// then the tag it was recorded with: <c>v&lt;version&gt;-aide.&lt;k&gt;</c> when the version number was reused after a rollback
/// and the base tag holds an earlier release; several attempts of one version may succeed (re-releases after rollbacks).
/// <c>mergeCommitSha</c> is set as soon as the merge exists, before tagging.
/// </summary>
public sealed record ReleaseDto(
    Guid Id,
    int ProjectVersion,
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
public sealed record ReleaseSummaryDto(Guid Id, int ProjectVersion, ReleaseStatus Status, string Tag, DateTimeOffset StartedAt);
