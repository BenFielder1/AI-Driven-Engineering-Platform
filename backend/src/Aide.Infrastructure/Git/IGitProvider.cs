using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Domain.Releases;

namespace Aide.Infrastructure.Git;

/// <summary>
/// The saved Git settings of a project, as a provider needs them (never includes the token). For <c>github_app</c>
/// (FR-82), <see cref="InstallationId"/> and <see cref="RepoId"/> identify the repository and <see cref="RepoUrl"/> is its
/// <c>https://github.com/{owner}/{name}</c> URL (derived from the stored full name). For <c>gitlab</c> (FR-136),
/// <see cref="InstanceId"/>, <see cref="ExternalRepoId"/> and <see cref="RepoFullName"/> identify the repository and
/// <see cref="GitConnectionId"/> is the binding Owner's connection whose token all Git work uses (<c>null</c>: unbound,
/// so Git work fails with <c>GIT_NOT_CONNECTED</c>).
/// </summary>
public sealed record GitConnectionSettings(Guid ProjectId, string? RepoUrl, string DefaultBranch)
{
    public long? InstallationId { get; init; }

    public long? RepoId { get; init; }

    /// <summary>FR-136: the Git host instance (GitLab, Bitbucket).</summary>
    public Guid? InstanceId { get; init; }

    /// <summary>FR-136: the binding Owner's connection (GitLab, Bitbucket); <c>null</c> when the binding was cleared.</summary>
    public Guid? GitConnectionId { get; init; }

    /// <summary>FR-136: the host's repo id (GitLab project id, Bitbucket repository UUID).</summary>
    public string? ExternalRepoId { get; init; }

    /// <summary>FR-136: <c>group/sub/project</c> (GitLab) or <c>workspace/slug</c> (Bitbucket).</summary>
    public string? RepoFullName { get; init; }

    /// <summary>The connection for a project's stored Git settings (<paramref name="defaultBranch"/> overrides the stored one).</summary>
    public static GitConnectionSettings From(ProjectGitSettings settings, string? defaultBranch = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new GitConnectionSettings(
            settings.ProjectId,
            Domain.Projects.ProjectSettingsRules.RepoWebUrl(settings.Provider, settings.RepoUrl, settings.RepoFullName),
            defaultBranch ?? settings.DefaultBranch)
        {
            InstallationId = settings.Provider == GitProvider.GithubApp ? settings.InstallationId : null,
            RepoId = settings.Provider == GitProvider.GithubApp ? settings.RepoId : null,
            InstanceId = Hosted(settings) ? settings.InstanceId : null,
            GitConnectionId = Hosted(settings) ? settings.GitConnectionId : null,
            ExternalRepoId = Hosted(settings) ? settings.ExternalRepoId : null,
            RepoFullName = Hosted(settings) ? settings.RepoFullName : null,
        };
    }

    private static bool Hosted(ProjectGitSettings settings) => Domain.Projects.ProjectSettingsRules.IsGitHost(settings.Provider);
}

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
    /// FR-42 amended by FR-64, FR-43: releases the version branch: merges it into the default branch (GitHub: merges the PR
    /// through the API; Local: <c>git merge --no-ff</c> in the working copy) and creates the annotated tag
    /// <see cref="ReleaseRequest.Tag"/> on the merge commit (GitHub: pushed). Idempotent: the tag already holding the branch is
    /// a no-op success; the tag on another commit fails with <c>TAG_EXISTS</c> before anything is merged
    /// (<see cref="Domain.Releases.ReleaseRules.DecideTag"/>). A merge conflict is returned with the conflicting files, never
    /// thrown. The working copy must be prepared first.
    /// </summary>
    Task<ReleaseOutcome> ReleaseAsync(GitConnectionSettings settings, ReleaseRequest request, string? token, CancellationToken cancellationToken);

    /// <summary>
    /// FR-61: whether the tag <paramref name="tag"/> exists in the repository (GitHub: through the API; Local: in the
    /// repository on the data volume, which may not exist yet). Changes nothing.
    /// </summary>
    Task<TagLookup> FindTagAsync(GitConnectionSettings settings, string tag, string? token, CancellationToken cancellationToken);

    // §4.19 common operations (v4). User-scoped ones take the caller's own connection (already checked to be theirs);
    // project-scoped ones the project's settings (the binding connection). A provider that doesn't implement one answers
    // NotSupported (GitHub keeps its v2 services for the picker and import; Local has no host).

    /// <summary>§4.19, FR-132: the repos the connection's user can develop in (search, keyset cursor).</summary>
    Task<Result<GitHostRepoPage>> ListReposAsync(Guid connectionId, string? search, string? cursor, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<GitHostRepoPage>(GitHostOperations.NotSupported(Kind, "listing repositories")));

    /// <summary>§4.19, FR-132: where the connection's user can create a repo (namespace and groups, workspaces).</summary>
    Task<Result<IReadOnlyList<GitHostOwner>>> ListOwnersAsync(Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<IReadOnlyList<GitHostOwner>>(GitHostOperations.NotSupported(Kind, "listing owners")));

    /// <summary>§4.19, FR-132: creates a repo whose default branch exists (README).</summary>
    Task<Result<GitHostRepo>> CreateRepoAsync(Guid connectionId, GitHostCreateRepo request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<GitHostRepo>(GitHostOperations.NotSupported(Kind, "creating repositories")));

    /// <summary>§4.19, FR-141: a repo's default branch, releases and tags, with the import's suggested released version.</summary>
    Task<Result<GitHostReleaseRefs>> FindReleaseRefsAsync(Guid connectionId, string repoId, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<GitHostReleaseRefs>(GitHostOperations.NotSupported(Kind, "reading releases and tags")));

    /// <summary>§4.19, FR-138 (slice 2b): the version's merge/pull request readiness.</summary>
    Task<Result<HostMergeReadiness>> GetMergeReadinessAsync(GitConnectionSettings settings, int number, MergeStrategy strategy, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<HostMergeReadiness>(GitHostOperations.NotSupported(Kind, "merge readiness")));

    /// <summary>§4.19, FR-139 (slice 2b): the commit's CI statuses as review observations.</summary>
    Task<Result<IReadOnlyList<CheckObservation>>> GetCommitStatusesAsync(GitConnectionSettings settings, string sha, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<IReadOnlyList<CheckObservation>>(GitHostOperations.NotSupported(Kind, "commit statuses")));

    /// <summary>§4.19, FR-140 (slice 2b): publishes the host release (GitLab Release; Bitbucket annotated tag); its URL.</summary>
    Task<Result<string?>> PublishReleaseAsync(GitConnectionSettings settings, HostReleaseRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<string?>(GitHostOperations.NotSupported(Kind, "publishing releases")));
}

/// <summary>§4.19: a repo on a Git host, as the picker shows it (<see cref="Id"/>: GitLab project id, Bitbucket UUID).</summary>
public sealed record GitHostRepo(
    string Id,
    string FullName,
    string Name,
    string? Path,
    string? DefaultBranch,
    string? WebUrl,
    string? OwnerPath,
    string? Visibility,
    DateTimeOffset? LastActivityAt);

/// <summary>A page of repos; <see cref="NextCursor"/> is <c>null</c> on the last page.</summary>
public sealed record GitHostRepoPage(IReadOnlyList<GitHostRepo> Items, string? NextCursor);

/// <summary>§4.19: a place a repo can be created (<see cref="Kind"/>: <c>user</c>, <c>group</c>, <c>workspace</c>).</summary>
public sealed record GitHostOwner(long Id, string Kind, string FullPath, string Name, bool CanCreate, string? Reason);

/// <summary>§4.19: a repo to create under <see cref="Owner"/> (full path); <see cref="Project"/> is Bitbucket's project key.</summary>
public sealed record GitHostCreateRepo(string Owner, string Name, string? Path, string? Description, bool Private, string? Project = null);

/// <summary>A host release (FR-141): its tag, name, release time, and whether it is the host's latest.</summary>
public sealed record GitHostRelease(string TagName, string? Name, DateTimeOffset? ReleasedAt, bool IsLatest);

/// <summary>A tag with its commit and semantic version (<c>null</c> when it isn't one).</summary>
public sealed record GitHostTag(string Name, string Sha, string? Semver);

/// <summary>
/// FR-141: what the import needs about a repo: its full path, default branch (and head), whether it is empty, its releases
/// and tags, and the suggestion (<see cref="Domain.GitHosts.GitLabProjectRules.SuggestRelease"/>).
/// </summary>
public sealed record GitHostReleaseRefs(
    string FullName,
    string DefaultBranch,
    string? HeadSha,
    bool Empty,
    IReadOnlyList<GitHostRelease> Releases,
    IReadOnlyList<GitHostTag> Tags,
    ReleaseRefSuggestion Suggestion);

/// <summary>FR-140 (slice 2b): a host release to publish on <see cref="Commit"/>.</summary>
public sealed record HostReleaseRequest(string Tag, string Commit, string Name, string TagMessage, string Notes);

/// <summary>Helpers for <see cref="IGitProvider"/>'s §4.19 operations.</summary>
public static class GitHostOperations
{
    /// <summary>The failure of an operation a provider doesn't implement (<c>GIT_ERROR</c>, never retried).</summary>
    public static DomainError NotSupported(GitProvider provider, string operation) =>
        new(ErrorCodes.GitError, $"The {provider.ToWire()} Git provider doesn't support {operation}.");
}

/// <summary>The outcome of <see cref="IGitProvider.FindTagAsync"/>: whether the tag exists, or a token-free failure reason.</summary>
public sealed record TagLookup(bool Ok, bool Exists, string? Reason, bool Transient)
{
    public static TagLookup Found { get; } = new(true, true, null, false);

    public static TagLookup Missing { get; } = new(true, false, null, false);

    public static TagLookup Failed(string reason, bool transient) => new(false, false, reason, transient);
}

/// <summary>
/// A release to perform (FR-42, FR-64): the version branch, the release tag (<c>&lt;tagPrefix&gt;&lt;version&gt;</c>) and version,
/// the project name (the tag message and the merge title come from <see cref="Domain.Releases.ReleaseRules"/>), the version's
/// PR (GitHub), and <see cref="OnMerged"/>, called with the merge commit as soon as the merge exists (before tagging), so the
/// merge is on record even if tagging fails. An exception from it ends the release (nothing is tagged).
/// </summary>
public sealed record ReleaseRequest(
    string Branch,
    string Tag,
    string Version,
    string ProjectName,
    int? PullRequestNumber,
    Func<string, CancellationToken, Task>? OnMerged = null)
{
    /// <summary>
    /// FR-65: whether AIDE may merge (<c>aide_merges</c>). <c>false</c> (<c>manual</c>, and the merge poll): never merge —
    /// a PR (or Local branch) already merged by someone else is tagged on its merge commit and released; one not merged yet
    /// is <see cref="ReleaseOutcomeKind.AwaitingMerge"/> (GitHub: a draft PR is marked ready for review first).
    /// </summary>
    public bool Merge { get; init; } = true;
}

/// <summary>How a release attempt ended at the provider.</summary>
public enum ReleaseOutcomeKind
{
    /// <summary>Merged and tagged (or the tag already held the release).</summary>
    Released = 0,

    /// <summary>The branch conflicts with the default branch (FR-43): nothing was merged.</summary>
    Conflict = 1,

    /// <summary>Another failure; <see cref="ReleaseOutcome.Transient"/> says whether a retry could help.</summary>
    Failed = 2,

    /// <summary>
    /// FR-65: not merged yet, and AIDE won't (or can't) merge it: the strategy is <c>manual</c>, or GitHub refused the merge
    /// (branch protection, required reviews or checks, missing permission). <see cref="ReleaseOutcome.Reason"/> says why.
    /// Nothing was merged or tagged.
    /// </summary>
    AwaitingMerge = 3,
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
    /// <summary>The release tag the release was recorded with; set when released.</summary>
    public string? Tag { get; init; }

    public static ReleaseOutcome Released(string mergeCommitSha, string tag, bool alreadyTagged) =>
        new(ReleaseOutcomeKind.Released, mergeCommitSha, alreadyTagged, [], null, null, false) { Tag = tag };

    public static ReleaseOutcome Conflicted(IReadOnlyList<string> files, string reason) =>
        new(ReleaseOutcomeKind.Conflict, null, false, files, Domain.Common.ErrorCodes.MergeConflict, reason, false);

    public static ReleaseOutcome Failed(string errorCode, string reason, bool transient) =>
        new(ReleaseOutcomeKind.Failed, null, false, [], errorCode, reason, transient);

    /// <summary>FR-65: waiting for a merge by someone else; <see cref="MergeRefused"/> when GitHub refused AIDE's merge.</summary>
    public static ReleaseOutcome Awaiting(string reason, bool refused) =>
        new(ReleaseOutcomeKind.AwaitingMerge, null, false, [], null, reason, false) { MergeRefused = refused };

    /// <summary>FR-65: GitHub refused AIDE's merge (as opposed to the <c>manual</c> strategy not trying).</summary>
    public bool MergeRefused { get; init; }

    /// <summary>FR-65: released on a merge someone else made (the PR was already merged on GitHub, or the Local branch by hand).</summary>
    public bool MergedExternally { get; init; }
}

/// <summary>The project PR to open (FR-36).</summary>
public sealed record PullRequestRequest(string HeadBranch, string Title, string Body);

/// <summary>
/// The outcome of opening the project PR: its number and web URL (GitHub; both <c>null</c> for Local), whether an open PR
/// was reused, or a failure <see cref="Reason"/> (never containing a token) and whether a retry could help.
/// </summary>
public sealed record PullRequestOutcome(bool Ok, int? Number, string? Url, bool Reused, string? Reason, bool Transient)
{
    /// <summary>An error code for the job failure (e.g. <c>GIT_NOT_CONNECTED</c>, FR-136), when the provider knows one.</summary>
    public string? ErrorCode { get; init; }

    public static PullRequestOutcome Opened(int? number, string? url, bool reused) => new(true, number, url, reused, null, false);

    public static PullRequestOutcome Failed(string reason, bool transient) => new(false, null, null, false, reason, transient);
}

/// <summary>
/// The outcome of a provider operation; <see cref="Reason"/> explains a failure (never contains a token) and
/// <see cref="Transient"/> tells whether a retry could help (a network error) or not (bad credentials, a rejected push).
/// </summary>
public sealed record GitOutcome(bool Ok, string? Reason, bool Transient = true)
{
    /// <summary>An error code for the job failure (e.g. <c>GIT_NOT_CONNECTED</c>, FR-136), when the provider knows one.</summary>
    public string? ErrorCode { get; init; }

    public static GitOutcome Failed(string reason, bool transient, string? errorCode) => new(false, reason, transient) { ErrorCode = errorCode };

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
