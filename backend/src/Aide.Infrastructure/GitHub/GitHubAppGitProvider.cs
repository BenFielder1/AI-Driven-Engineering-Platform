using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Git;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.GitHub;

/// <summary>
/// FR-82: the <c>github_app</c> provider. Git and GitHub work is exactly the v1 GitHub provider's (clone, fetch, push, the
/// project PR, merge and tag; <see cref="GitHubGitProvider"/>), authenticated with an <b>installation token</b> minted per
/// call (cached ≤ 55 min, scoped to the project's repository with <c>repository_ids</c>) instead of a stored token — never a
/// user token. The token is passed to git only through the per-command <c>extraHeader</c> environment (v1 FR-35), never in
/// a remote URL, on disk, in logs or to an LLM. Commits, merges and tags in the working copy are attributed to the App bot.
/// The <c>token</c> argument callers pass is ignored (they have none for an App project).
/// </summary>
internal sealed partial class GitHubAppGitProvider(
    GitHubGitProvider github,
    IGitHubAppStore apps,
    IGitHubInstallationTokens tokens,
    GitHubInstallationTokenCache tokenCache,
    GitHubBotIdentities bots,
    GitHubApi api,
    IWorkingCopies workingCopies,
    ILogger<GitHubAppGitProvider> logger) : IGitProvider
{
    public GitProvider Kind => GitProvider.GithubApp;

    public async Task<GitTestResult> TestConnectionAsync(GitConnectionSettings settings, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var minted = await MintAsync(settings, cancellationToken);
        if (minted.Failure is { } failure)
        {
            return GitTestResult.Failed(failure.Reason!);
        }

        if (!ProjectSettingsRules.TryParseGitHubRepo(settings.RepoUrl, out var repo))
        {
            return GitTestResult.Failed("The project has no GitHub repository recorded. Pick it again in the project's Git settings.");
        }

        try
        {
            if (await api.FindRepositoryAsync(minted.Token!.Token, repo.Owner, repo.Name, cancellationToken) is null)
            {
                return GitTestResult.Failed($"The AIDE GitHub App can't access {repo.Owner}/{repo.Name}. Add the repository to the App's installation.");
            }

            // An installation token's own permissions say whether AIDE may push (Contents: write).
            var writable = minted.Token.Permissions.TryGetValue("contents", out var contents) && contents == "write";
            using var branch = await api.GetOrNullAsync(
                $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/branches/{Uri.EscapeDataString(settings.DefaultBranch)}",
                minted.Token.Token,
                cancellationToken,
                emptyIsMissing: true);
            var branchExists = branch is not null;
            string? reason = !writable
                ? "The AIDE GitHub App can't push to this repository: it needs Contents: read and write."
                : !branchExists
                    ? $"The default branch '{settings.DefaultBranch}' doesn't exist in {repo.Owner}/{repo.Name}."
                    : null;
            LogTested(logger, settings.ProjectId, writable, branchExists);
            return new GitTestResult(reason is null, Reachable: true, writable, branchExists, reason);
        }
        catch (GitHubApiException ex)
        {
            return GitTestResult.Failed(ex.Message);
        }
    }

    public async Task<GitOutcome> PrepareWorkingCopyAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var minted = await MintAsync(settings, cancellationToken);
        if (minted.Failure is { } failure)
        {
            return failure;
        }

        // FR-82: the App bot authors every commit, merge and tag in this working copy.
        workingCopies.UseCommitIdentity(settings.ProjectId, await bots.GetAsync(minted.App!.Slug, cancellationToken));
        return await github.PrepareWorkingCopyAsync(settings, branchName, minted.Token!.Token, cancellationToken);
    }

    public async Task<GitOutcome> PublishBranchAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var minted = await MintAsync(settings, cancellationToken);
        return minted.Failure ?? await github.PublishBranchAsync(settings, branchName, minted.Token!.Token, cancellationToken);
    }

    public async Task<PullRequestOutcome> OpenPullRequestAsync(GitConnectionSettings settings, PullRequestRequest request, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var minted = await MintAsync(settings, cancellationToken);
        return minted.Failure is { } failure
            ? PullRequestOutcome.Failed(failure.Reason!, failure.Transient)
            : await github.OpenPullRequestAsync(settings, request, minted.Token!.Token, cancellationToken);
    }

    public string ComparisonBaseRef(GitConnectionSettings settings) => github.ComparisonBaseRef(settings);

    public async Task<ReleaseOutcome> ReleaseAsync(GitConnectionSettings settings, ReleaseRequest request, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var minted = await MintAsync(settings, cancellationToken);
        if (minted.Failure is { } failure)
        {
            return ReleaseOutcome.Failed(ErrorCodes.GitError, failure.Reason!, failure.Transient);
        }

        workingCopies.UseCommitIdentity(settings.ProjectId, await bots.GetAsync(minted.App!.Slug, cancellationToken));
        return await github.ReleaseAsync(settings, request, minted.Token!.Token, cancellationToken);
    }

    public async Task<TagLookup> FindTagAsync(GitConnectionSettings settings, string tag, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var minted = await MintAsync(settings, cancellationToken);
        return minted.Failure is { } failure
            ? TagLookup.Failed(failure.Reason!, failure.Transient)
            : await github.FindTagAsync(settings, tag, minted.Token!.Token, cancellationToken);
    }

    /// <summary>
    /// The App and an installation token for the project's repository, or why there is none. A rate limit propagates
    /// (<see cref="GitHubRateLimitedException"/>: the job waits for the reset without counting a failure).
    /// </summary>
    private async Task<Minted> MintAsync(GitConnectionSettings settings, CancellationToken cancellationToken)
    {
        if (settings.InstallationId is not { } installationId || settings.RepoId is not { } repoId)
        {
            return Minted.Failed("The project's GitHub App installation isn't recorded. Pick the repository again, or switch the project to a personal access token.", transient: false);
        }

        var app = await apps.GetAsync(cancellationToken);
        if (app is null)
        {
            return Minted.Failed("No AIDE GitHub App is registered, so AIDE can't reach the repository. An Admin registers it under Admin → Integrations → GitHub.", transient: false);
        }

        try
        {
            return new Minted(app, await tokens.GetAsync(app, installationId, repoId, cancellationToken), null);
        }
        catch (GitHubApiException ex)
        {
            tokenCache.Forget(installationId);
            LogMintFailed(logger, settings.ProjectId, installationId, ex.Status);
            var transient = ex.Kind == GitHubFailureKind.Upstream;
            return Minted.Failed(
                ex.Kind is GitHubFailureKind.NotFound or GitHubFailureKind.Unprocessable or GitHubFailureKind.Forbidden
                    ? $"The AIDE GitHub App can't access the repository any more (it was uninstalled, or the repository was removed from its installation). {ex.Message}"
                    : ex.Message,
                transient);
        }
    }

    private sealed record Minted(GitHubAppCredentials? App, GitHubInstallationToken? Token, GitOutcome? Failure)
    {
        public static Minted Failed(string reason, bool transient) => new(null, null, GitOutcome.Failed(reason, transient));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub App connection test for project {ProjectId}: writable={Writable}, default branch exists={BranchExists}")]
    private static partial void LogTested(ILogger logger, Guid projectId, bool writable, bool branchExists);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Minting an installation token for project {ProjectId} (installation {InstallationId}) failed with HTTP {Status}")]
    private static partial void LogMintFailed(ILogger logger, Guid projectId, long installationId, int? status);
}
