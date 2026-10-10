using Aide.Domain.Entities;

namespace Aide.Infrastructure.Git;

/// <summary>The saved Git settings of a project, as a provider needs them (never includes the token).</summary>
public sealed record GitConnectionSettings(Guid ProjectId, string? RepoUrl, string DefaultBranch);

/// <summary>Outcome of a connection test (FR-10). <see cref="Reason"/> explains a failure in the provider's words.</summary>
public sealed record GitTestResult(bool Ok, bool Reachable, bool Writable, bool DefaultBranchExists, string? Reason)
{
    public static GitTestResult Failed(string reason, bool reachable = false, bool writable = false, bool defaultBranchExists = false) =>
        new(false, reachable, writable, defaultBranchExists, reason);
}

/// <summary>
/// One Git hosting provider (FR-35, D12): GitHub and Local in v1; GitLab can be added later (§11 Q1) by implementing
/// this interface. Tokens are passed in decrypted per call and never stored, logged or echoed in results.
/// </summary>
/// <remarks>
/// The working copy itself (reading files, writing and committing a step) is provider-agnostic: <see cref="IWorkingCopies"/>.
/// A provider puts the repository there (<see cref="PrepareWorkingCopyAsync"/>) and publishes the branch after each step
/// commit (<see cref="PublishBranchAsync"/>), and opens the project PR when execution completes
/// (<see cref="OpenPullRequestAsync"/>, FR-36). The release phase adds merging and tagging (FR-42, FR-43).
/// </remarks>
public interface IGitProvider
{
    GitProvider Kind { get; }

    /// <summary>Checks that the repository is reachable and writable and that the default branch exists. Changes nothing.</summary>
    Task<GitTestResult> TestConnectionAsync(GitConnectionSettings settings, string? token, CancellationToken cancellationToken);

    /// <summary>
    /// FR-35: makes the project's working copy a repository with <paramref name="branchName"/> created from the default
    /// branch and checked out. Idempotent: an existing repository and branch are reused (the branch is never rewritten, §4.8).
    /// </summary>
    Task<GitOutcome> PrepareWorkingCopyAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken);

    /// <summary>FR-34: publishes the project branch after a step commit (GitHub: push; Local: nothing to do).</summary>
    Task<GitOutcome> PublishBranchAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken);

    /// <summary>
    /// FR-36: opens the one project PR from <see cref="PullRequestRequest.HeadBranch"/> to the default branch, or reuses the
    /// open one for that branch (idempotent). Local: there is nothing to open; the "PR" is the recorded branch comparison.
    /// </summary>
    Task<PullRequestOutcome> OpenPullRequestAsync(GitConnectionSettings settings, PullRequestRequest request, string? token, CancellationToken cancellationToken);

    /// <summary>The ref in the working copy the project branch is compared with (FR-35/FR-36): the (remote) default branch.</summary>
    string ComparisonBaseRef(GitConnectionSettings settings);

    /// <summary>
    /// FR-42, FR-43: releases the project branch: merges it into the default branch (GitHub: merges the project PR through the
    /// API; Local: <c>git merge --no-ff</c> in the working copy) and creates the annotated tag (GitHub: pushed). Idempotent: a
    /// release tag of the version that already contains the branch is a no-op success; tags elsewhere are never moved, the
    /// release uses the first free <c>v&lt;N&gt;-aide.&lt;k&gt;</c> instead (<see cref="Domain.Releases.ReleaseRules.ChooseTag"/>). A
    /// merge conflict is returned with the conflicting files, never thrown. The working copy must be prepared first.
    /// </summary>
    Task<ReleaseOutcome> ReleaseAsync(GitConnectionSettings settings, ReleaseRequest request, string? token, CancellationToken cancellationToken);
}

/// <summary>
/// A release to perform (FR-42): the project branch, the project version and name (the tag, its message and the merge title
/// come from <see cref="Domain.Releases.ReleaseRules"/> once the provider has chosen the tag), the project PR (GitHub), and
/// <see cref="OnMerged"/>, called with the merge commit as soon as the merge exists (before tagging), so the merge is on
/// record even if tagging fails. An exception from it ends the release (nothing is tagged).
/// </summary>
public sealed record ReleaseRequest(
    string Branch,
    int ProjectVersion,
    string ProjectName,
    int? PullRequestNumber,
    Func<string, CancellationToken, Task>? OnMerged = null);

/// <summary>How a release attempt ended at the provider.</summary>
public enum ReleaseOutcomeKind
{
    /// <summary>Merged and tagged (or the tag already held the release).</summary>
    Released = 0,

    /// <summary>The branch conflicts with the default branch (FR-43): nothing was merged.</summary>
    Conflict = 1,

    /// <summary>Another failure; <see cref="ReleaseOutcome.Transient"/> says whether a retry could help.</summary>
    Failed = 2,
}

/// <summary>
/// The outcome of <see cref="IGitProvider.ReleaseAsync"/>: the merge commit (the tagged commit), the release tag used and
/// whether it already existed; or the conflicting files; or an error code (<c>GIT_ERROR</c>) with a token-free reason.
/// </summary>
public sealed record ReleaseOutcome(
    ReleaseOutcomeKind Kind,
    string? MergeCommitSha,
    bool AlreadyTagged,
    IReadOnlyList<string> ConflictingFiles,
    string? ErrorCode,
    string? Reason,
    bool Transient)
{
    /// <summary>The release tag (base or suffixed) the release was recorded with; set when released.</summary>
    public string? Tag { get; init; }

    public static ReleaseOutcome Released(string mergeCommitSha, string tag, bool alreadyTagged) =>
        new(ReleaseOutcomeKind.Released, mergeCommitSha, alreadyTagged, [], null, null, false) { Tag = tag };

    public static ReleaseOutcome Conflicted(IReadOnlyList<string> files, string reason) =>
        new(ReleaseOutcomeKind.Conflict, null, false, files, Domain.Common.ErrorCodes.MergeConflict, reason, false);

    public static ReleaseOutcome Failed(string errorCode, string reason, bool transient) =>
        new(ReleaseOutcomeKind.Failed, null, false, [], errorCode, reason, transient);
}

/// <summary>The project PR to open (FR-36).</summary>
public sealed record PullRequestRequest(string HeadBranch, string Title, string Body);

/// <summary>
/// The outcome of opening the project PR: its number and web URL (GitHub; both <c>null</c> for Local), whether an open PR
/// was reused, or a failure <see cref="Reason"/> (never containing a token) and whether a retry could help.
/// </summary>
public sealed record PullRequestOutcome(bool Ok, int? Number, string? Url, bool Reused, string? Reason, bool Transient)
{
    public static PullRequestOutcome Opened(int? number, string? url, bool reused) => new(true, number, url, reused, null, false);

    public static PullRequestOutcome Failed(string reason, bool transient) => new(false, null, null, false, reason, transient);
}

/// <summary>
/// The outcome of a provider operation; <see cref="Reason"/> explains a failure (never contains a token) and
/// <see cref="Transient"/> tells whether a retry could help (a network error) or not (bad credentials, a rejected push).
/// </summary>
public sealed record GitOutcome(bool Ok, string? Reason, bool Transient = true)
{
    public static GitOutcome Success { get; } = new(true, null);

    public static GitOutcome Failed(string reason, bool transient = true) => new(false, reason, transient);
}

/// <summary>Picks the <see cref="IGitProvider"/> for a project's provider setting.</summary>
public interface IGitProviderResolver
{
    IGitProvider Resolve(GitProvider provider);
}

internal sealed class GitProviderResolver(IEnumerable<IGitProvider> providers) : IGitProviderResolver
{
    private readonly Dictionary<GitProvider, IGitProvider> _providers = providers.ToDictionary(p => p.Kind);

    public IGitProvider Resolve(GitProvider provider) =>
        _providers.TryGetValue(provider, out var implementation)
            ? implementation
            : throw new NotSupportedException($"Git provider {provider} is not available.");
}
