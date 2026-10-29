using Aide.Domain.Releases;

namespace Aide.Domain.GitHosts;

/// <summary>What a refused or failed merge call means for the release (FR-140).</summary>
public enum HostMergeFailureKind
{
    /// <summary>Not mergeable (GitLab 405, Bitbucket 400) or not allowed (403): the attempt becomes <c>awaiting_merge</c>
    /// with the readiness reasons (FR-65).</summary>
    AwaitingMerge = 0,

    /// <summary>The head moved since review (GitLab 409 on <c>sha</c>, Bitbucket 409 refs changed): re-review is required.</summary>
    HeadMoved = 1,

    /// <summary>The merge failed for good (GitLab 422, other 4xx).</summary>
    Failed = 2,

    /// <summary>Transient (5xx, Bitbucket 555 timeout): retry the job.</summary>
    Retry = 3,

    /// <summary>Rate limited (429): re-queued at the reset without counting an attempt (§4.19).</summary>
    RateLimited = 4,

    /// <summary>The host rejected the token (401): refresh once, then the connection is invalid (FR-131, FR-136).</summary>
    Unauthorized = 5,
}

/// <summary>FR-140: a merge call's failure, and the readiness reason it implies when it awaits a merge.</summary>
public sealed record HostMergeFailure(HostMergeFailureKind Kind, MergeReadinessReasonCode? Reason, string Message);

/// <summary>FR-140, §4.19: GitLab merge, release and tag-limit rules. Pure.</summary>
public static class GitLabMergeResult
{
    /// <summary>§4.19: GitLab.com's tag creation limit: 100 tags per 30 minutes.</summary>
    public const int TagCreationLimit = 100;

    /// <summary>§4.19: the tag creation limit's window.</summary>
    public static readonly TimeSpan TagCreationWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// FR-140: the merged commit: <c>merge_commit_sha</c>, else <c>squash_commit_sha</c>, else the head <c>sha</c>
    /// (fast-forward). Blank values count as absent.
    /// </summary>
    public static string? Sha(string? mergeCommitSha, string? squashCommitSha, string? headSha) =>
        new[] { mergeCommitSha, squashCommitSha, headSha }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();

    /// <summary>
    /// FR-140, §4.19: <c>PUT …/merge</c> failures: 405 (and GitLab's older 406) not mergeable → awaiting merge; 403 →
    /// awaiting merge with <c>no_permission</c> (merging into the protected default branch needs Maintainer); 409 the head
    /// moved → re-review; 422 → failed; 401 → unauthorized; 429 → rate limited; 5xx → retry; anything else → failed.
    /// </summary>
    public static HostMergeFailure ClassifyMergeError(int status) => status switch
    {
        405 or 406 => new(HostMergeFailureKind.AwaitingMerge, null, "GitLab says the merge request isn't mergeable yet."),
        403 => new(HostMergeFailureKind.AwaitingMerge, MergeReadinessReasonCode.NoPermission, "AIDE's GitLab connection may not merge into the target branch (it needs the Maintainer role)."),
        409 => new(HostMergeFailureKind.HeadMoved, null, "The merge request's head moved since it was reviewed; it needs a new review."),
        422 => new(HostMergeFailureKind.Failed, null, "GitLab couldn't merge the merge request."),
        401 => new(HostMergeFailureKind.Unauthorized, null, "GitLab rejected the connection's token."),
        429 => new(HostMergeFailureKind.RateLimited, null, "GitLab is rate limiting requests."),
        >= 500 => new(HostMergeFailureKind.Retry, null, "GitLab returned a server error."),
        _ => new(HostMergeFailureKind.Failed, null, "GitLab refused the merge."),
    };

    /// <summary>
    /// §4.19: when a tag (or release) may be created again: <c>null</c> when fewer than 100 tags were created in the last
    /// 30 minutes, else when the oldest of the last 100 leaves the window.
    /// </summary>
    public static DateTimeOffset? NextTagAllowedAt(IEnumerable<DateTimeOffset> tagsCreatedAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tagsCreatedAt);
        var inWindow = tagsCreatedAt.Where(t => t > now - TagCreationWindow && t <= now).OrderDescending().ToList();
        return inWindow.Count < TagCreationLimit ? null : inWindow[TagCreationLimit - 1] + TagCreationWindow;
    }

    /// <summary>
    /// FR-140: a tag-creation-limited response (429) is retried at the reset: <c>Retry-After</c>, else
    /// <c>RateLimit-Reset</c>, else after the 30-minute window. Other statuses aren't limits (<c>null</c>).
    /// </summary>
    public static DateTimeOffset? TagLimitRetryAt(int status, string? retryAfter, string? rateLimitReset, DateTimeOffset now) =>
        status != 429 ? null : GitHostPollBudget.ResetAt(retryAfter, rateLimitReset, now) ?? now + TagCreationWindow;
}

/// <summary>FR-140, §4.19: Bitbucket merge rules. Pure.</summary>
public static class BitbucketMergeResult
{
    /// <summary>§4.19: Bitbucket's merge timeout status (the merge may still complete): retried.</summary>
    public const int MergeTimeoutStatus = 555;

    /// <summary>
    /// FR-140: <c>POST …/merge</c> failures: 400 not mergeable → awaiting merge; 403 → awaiting merge with
    /// <c>no_permission</c>; 409 refs changed → re-review; 555 and 5xx → retry; 401; 429; anything else → failed. A 202 is
    /// not a failure: follow <c>merge/task-status</c>.
    /// </summary>
    public static HostMergeFailure ClassifyMergeError(int status) => status switch
    {
        400 => new(HostMergeFailureKind.AwaitingMerge, null, "Bitbucket says the pull request isn't mergeable yet."),
        403 => new(HostMergeFailureKind.AwaitingMerge, MergeReadinessReasonCode.NoPermission, "AIDE's Bitbucket connection may not merge into the destination branch."),
        409 => new(HostMergeFailureKind.HeadMoved, null, "The pull request's branch changed since it was reviewed; it needs a new review."),
        401 => new(HostMergeFailureKind.Unauthorized, null, "Bitbucket rejected the connection's token."),
        429 => new(HostMergeFailureKind.RateLimited, null, "Bitbucket is rate limiting requests."),
        >= 500 => new(HostMergeFailureKind.Retry, null, status == MergeTimeoutStatus ? "Bitbucket timed out merging the pull request." : "Bitbucket returned a server error."),
        _ => new(HostMergeFailureKind.Failed, null, "Bitbucket refused the merge."),
    };
}
