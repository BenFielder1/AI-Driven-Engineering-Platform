namespace Tevrin.Domain.Entities;

/// <summary>Git provider of a project (FR-35, FR-82, FR-136). Wire values: <c>github_pat</c> (0.1.0 <c>github</c>), <c>local</c>,
/// <c>github_app</c>, <c>gitlab</c>, <c>bitbucket</c>.</summary>
public enum GitProvider
{
    GithubPat = 0,
    Local = 1,
    GithubApp = 2,
    Gitlab = 3,
    Bitbucket = 4,
}

/// <summary>Outcome of a release attempt (FR-42, FR-43).</summary>
public enum ReleaseStatus
{
    Succeeded = 0,
    Failed = 1,

    /// <summary>The release job is queued or running (stored as <c>in_progress</c>; no schema change: enums are strings).</summary>
    InProgress = 2,

    /// <summary>FR-65: the merge was refused (branch protection, reviews, checks); the merge poll completes it.</summary>
    AwaitingMerge = 3,
}

/// <summary>
/// FR-178, §5 <c>release_versions.stack_source</c>: how a version's stack is chosen. <c>manual</c> (default; the wizard,
/// Settings → Stack, or no stack) or <c>infer</c> (the Stack Analyst writes a <c>stack_profile</c> in the <c>analysis</c> phase).
/// </summary>
public enum StackSource
{
    Manual = 0,
    Infer = 1,
}

/// <summary>Status of a release version (§4.10). Wire values: <c>in_development</c>, <c>awaiting_merge</c>, <c>released</c>, <c>imported</c>.</summary>
public enum ReleaseVersionStatus
{
    InDevelopment = 0,
    AwaitingMerge = 1,
    Released = 2,

    /// <summary>A baseline from import or migration; never developed in Tevrin.</summary>
    Imported = 3,
}

/// <summary>
/// FR-65 merge strategy. Wire values: <c>aide_merges</c>, <c>manual</c>; FR-140 (Bitbucket only): <c>fast_forward</c> and
/// <c>squash</c>, which are <c>aide_merges</c> with Bitbucket's <c>merge_strategy</c> of that name (<c>aide_merges</c> sends
/// <c>merge_commit</c>).
/// </summary>
public enum MergeStrategy
{
    AideMerges = 0,
    Manual = 1,

    /// <summary>FR-140: Tevrin merges with Bitbucket's <c>fast_forward</c> merge strategy (Bitbucket projects only).</summary>
    FastForward = 2,

    /// <summary>FR-140: Tevrin merges with Bitbucket's <c>squash</c> merge strategy (Bitbucket projects only).</summary>
    Squash = 3,
}

/// <summary>Outcome of an execution step attempt (FR-57). Wire values: <c>accepted</c>, <c>rejected</c>, <c>error</c>.</summary>
public enum StepAttemptOutcome
{
    Accepted = 0,
    Rejected = 1,
    Error = 2,
}
