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
