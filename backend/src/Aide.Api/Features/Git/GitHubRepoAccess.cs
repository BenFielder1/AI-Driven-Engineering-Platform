using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Git;

/// <summary>
/// A GitHub project's repository and the token for background release work (§4.14): an installation token for
/// <c>github_app</c> (minted per job, cached ≤ 55 min, scoped to the repository) or the decrypted PAT for
/// <c>github_pat</c>. <see cref="InstallationPermissions"/> are the installation token's permissions (<c>null</c> for a PAT).
/// The token never leaves the process: it is passed to <see cref="GitHubApi"/> only.
/// </summary>
public sealed record GitHubRepoTarget(
    string Owner,
    string Repo,
    string Token,
    string DefaultBranch,
    string RepoWebUrl,
    IReadOnlyDictionary<string, string>? InstallationPermissions)
{
    /// <summary>Redacted: the token never reaches a log.</summary>
    public override string ToString() => $"GitHubRepoTarget {{ {Owner}/{Repo} }}";

    /// <summary>FR-84: whether an installation token may merge (Contents and Pull requests: write); <c>null</c> for a PAT.</summary>
    public bool? InstallationCanMerge => InstallationPermissions is { } permissions
        ? permissions.TryGetValue("contents", out var contents) && contents == "write"
            && permissions.TryGetValue("pull_requests", out var pulls) && pulls == "write"
        : null;
}

/// <summary>
/// Resolves <see cref="GitHubRepoTarget"/> for a project (FR-64, FR-65, FR-84, FR-85). <c>null</c> (success) for a Local
/// project. Failures are token-free <see cref="DomainError"/>s (<c>UPSTREAM_ERROR</c> when GitHub failed). A rate limit
/// while minting propagates (<see cref="GitHubRateLimitedException"/>).
/// </summary>
public sealed class GitHubRepoAccess(AideDbContext db, ISecretProtector secrets, IGitHubAppStore apps, IGitHubInstallationTokens tokens)
{
    public async Task<Result<GitHubRepoTarget?>> ResolveAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var settings = await db.ProjectGitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        if (settings is null || !ProjectSettingsRules.IsGitHub(settings.Provider))
        {
            return Result.Success<GitHubRepoTarget?>(null);
        }

        var webUrl = ProjectSettingsRules.RepoWebUrl(settings.Provider, settings.RepoUrl, settings.RepoFullName);
        if (!ProjectSettingsRules.TryParseGitHubRepo(webUrl, out var repo))
        {
            return Failure("The project has no GitHub repository recorded.");
        }

        if (settings.Provider == GitProvider.GithubPat)
        {
            if (settings.TokenEncrypted is not { } encrypted || !secrets.TryUnprotect(encrypted, out var pat))
            {
                return Failure("The project's GitHub token is missing or can't be decrypted. An Owner needs to save it again.");
            }

            return Result.Success<GitHubRepoTarget?>(new GitHubRepoTarget(repo.Owner, repo.Name, pat, settings.DefaultBranch, webUrl!, null));
        }

        if (settings.InstallationId is not { } installationId || settings.RepoId is not { } repoId)
        {
            return Failure("The project's GitHub App installation isn't recorded. Pick the repository again.");
        }

        if (await apps.GetAsync(cancellationToken) is not { } app)
        {
            return Failure("No AIDE GitHub App is registered.");
        }

        try
        {
            var token = await tokens.GetAsync(app, installationId, repoId, cancellationToken);
            return Result.Success<GitHubRepoTarget?>(new GitHubRepoTarget(repo.Owner, repo.Name, token.Token, settings.DefaultBranch, webUrl!, token.Permissions));
        }
        catch (GitHubApiException ex)
        {
            return Failure($"The AIDE GitHub App can't access the repository. {ex.Message}", ErrorCodes.UpstreamError);
        }
    }

    private static Result<GitHubRepoTarget?> Failure(string message, string code = ErrorCodes.UpstreamError) =>
        Result.Failure<GitHubRepoTarget?>(new DomainError(code, message));
}
