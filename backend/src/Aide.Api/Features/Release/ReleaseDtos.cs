using Aide.Api.Features.Git;
using Aide.Domain.Entities;

namespace Aide.Api.Features.Release;

/// <summary>
/// A release attempt (FR-42 amended by FR-64, FR-43) of release version <c>releaseVersionId</c>, approved at checkpoint
/// <c>checkpoint</c>. <c>status</c> is <c>in_progress</c> while the release job is queued or running, then
/// <c>succeeded</c> (merged into the default branch and tagged <c>tag</c>, <c>mergeCommitSha</c> set) or <c>failed</c>, or
/// <c>awaiting_merge</c> (FR-65) when the merge was refused or the strategy is <c>manual</c>: <c>awaitingMergeSince</c> is
/// when it started waiting and <c>mergeReasons</c> are the merge-readiness reasons (FR-84) recorded then (<c>null</c>
/// otherwise); the merge poll then completes it once the PR is merged on GitHub (§4.14).
/// A failed release carries <c>errorCode</c> (e.g. <c>MERGE_CONFLICT</c>) and <c>errorMessage</c>; on a merge conflict
/// <c>conflictingFiles</c> lists the files (empty otherwise). <c>pullRequestUrl</c> is the merged GitHub PR (<c>null</c>
/// for Local). <c>attempt</c> counts the release attempts at this checkpoint (1 for the first, +1 per retry).
/// <c>completedAt</c> is <c>null</c> while in progress. <c>tag</c> is the version's tag <c>&lt;tagPrefix&gt;&lt;version&gt;</c>
/// (e.g. <c>v1.2.0</c>; v1 releases keep their <c>v&lt;N&gt;-aide</c> tag). A tag that already exists on another commit fails
/// the release with <c>TAG_EXISTS</c>. <c>mergeCommitSha</c> is set as soon as the merge exists, before tagging.
/// Failure codes include <c>MERGE_CONFLICT</c>, <c>TAG_EXISTS</c>, <c>GIT_ERROR</c>, <c>RELEASE_CANCELLED</c>,
/// <c>GITHUB_RATE_LIMITED</c>, and while awaiting the merge <c>PR_CLOSED</c> (closed without merging) and
/// <c>MERGE_TIMEOUT</c> (not merged within 14 days); after either the project stays in <c>release</c> and an Owner can
/// retry with <c>POST /release</c>. <c>githubReleaseUrl</c> is the host release created for the tag: the GitHub Release
/// (FR-64) or the GitLab Release (FR-140); <c>null</c> for Local or before it exists. A GitLab merge refused because the
/// merge request's head moved since review (409) fails with <c>GIT_ERROR</c>. <c>mergeStrategy</c> is the project's current merge strategy (FR-65).
/// <c>productionDeployment</c> is the release's production deployment (FR-91: the newest for this release attempt, including
/// redeploys; <c>null</c> when deployment isn't configured or not created yet). A failed one doesn't undo the release.
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
    DateTimeOffset? CompletedAt,
    string? GithubReleaseUrl,
    DateTimeOffset? AwaitingMergeSince,
    IReadOnlyList<MergeReadinessReasonDto>? MergeReasons,
    MergeStrategy MergeStrategy,
    Deployments.DeploymentDto? ProductionDeployment);

/// <summary>
/// The latest release attempt in the project state (FR-42, FR-43, FR-64, FR-65): enough for the overview's release banner,
/// including "Awaiting merge" with the PR link and reasons, and the GitHub Release link. Fields as in <c>ReleaseDto</c>.
/// </summary>
public sealed record ReleaseSummaryDto(
    Guid Id,
    Guid ReleaseVersionId,
    int Checkpoint,
    ReleaseStatus Status,
    string Tag,
    DateTimeOffset StartedAt,
    string? PullRequestUrl,
    string? GithubReleaseUrl,
    DateTimeOffset? AwaitingMergeSince,
    IReadOnlyList<MergeReadinessReasonDto>? MergeReasons,
    MergeStrategy MergeStrategy);
