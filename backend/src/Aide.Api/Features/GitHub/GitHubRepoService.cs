using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Domain.Projects;
using Aide.Infrastructure.GitHub;

namespace Aide.Api.Features.GitHub;

/// <summary>
/// FR-82, FR-86: a repository picked for a <c>github_app</c> project, as verified with GitHub. <c>HeadSha</c> is the
/// default branch's head when the repository already has code (<c>HasCode</c>), else <c>null</c>.
/// </summary>
public sealed record VerifiedAppRepo(long InstallationId, long RepoId, string FullName, string DefaultBranch, bool HasCode, string? HeadSha);

/// <summary>
/// FR-81, FR-82, FR-83, FR-86: the repo picker, "Create repository" and the checks that the AIDE GitHub App can access a
/// project's repository. User tokens serve the picker, creation and identity; the App JWT only looks installations up
/// (§4.14). Failures are domain errors with the spec's codes; rate limits propagate as
/// <see cref="GitHubRateLimitedException"/> (429 <c>GITHUB_RATE_LIMITED</c>).
/// </summary>
public sealed partial class GitHubRepoService(
    GitHubConnections connections,
    IGitHubAppStore apps,
    GitHubApi api,
    GitHubAppInfoCache appInfo,
    TimeProvider time,
    ILogger<GitHubRepoService> logger)
{
    public const int PageSize = 30;

    /// <summary>How many repositories one installation lists at most (the picker searches within them).</summary>
    public const int MaxRepositoriesPerInstallation = 1000;

    private const int GitHubPageSize = 100;

    /// <summary>FR-81: the caller's connection, the App installations they can access and the App's install URL.</summary>
    public async Task<Result<GitHubConnectionDto>> ConnectionAsync(Guid userId, CancellationToken cancellationToken)
    {
        var app = await apps.GetAsync(cancellationToken);
        var stored = await connections.FindAsync(userId, cancellationToken);
        if (stored is null)
        {
            return Result.Success(new GitHubConnectionDto(false, null, null, null, null, [], app?.InstallUrl));
        }

        IReadOnlyList<GitHubInstallationDto> installations = [];
        if (stored.Status == GitHubConnectionStatus.Valid && app is not null)
        {
            var session = await connections.SessionAsync(userId, cancellationToken);
            if (session.IsSuccess)
            {
                var listed = await CallValueAsync(session.Value, () => api.ListUserInstallationsAsync(session.Value.Token, cancellationToken), cancellationToken);
                if (listed.IsFailure && listed.Error!.Code != ErrorCodes.GitHubNotConnected)
                {
                    return Result.Failure<GitHubConnectionDto>(listed.Error);
                }

                installations = listed.IsSuccess ? [.. listed.Value.Select(ToDto)] : [];
            }

            stored = await connections.FindAsync(userId, cancellationToken) ?? stored;
        }

        return Result.Success(new GitHubConnectionDto(true, stored.Login, stored.GithubUserId, stored.Status, stored.AccessExpiresAt, installations, app?.InstallUrl));
    }

    /// <summary>
    /// FR-81: the repositories the App is installed on that the caller can access, across their installations, filtered
    /// by <paramref name="search"/> (case-insensitive, in <c>owner/name</c>), sorted by full name, 30 per page.
    /// </summary>
    public async Task<Result<GitHubRepoPageDto>> ListReposAsync(Guid userId, string? search, int page, CancellationToken cancellationToken)
    {
        var session = await connections.SessionAsync(userId, cancellationToken);
        if (session.IsFailure)
        {
            return Result.Failure<GitHubRepoPageDto>(session.Error!);
        }

        var user = session.Value;
        var listed = await CallValueAsync(user, async () =>
        {
            var all = new List<GitHubRepoDto>();
            foreach (var installation in await api.ListUserInstallationsAsync(user.Token, cancellationToken))
            {
                for (var githubPage = 1; githubPage * GitHubPageSize <= MaxRepositoriesPerInstallation; githubPage++)
                {
                    var (repositories, total) = await api.ListInstallationRepositoriesAsync(user.Token, installation.Id, githubPage, GitHubPageSize, cancellationToken);
                    all.AddRange(repositories.Select(r => ToDto(r, installation.Id, r.Size == 0 ? true : null)));
                    if (repositories.Count < GitHubPageSize || githubPage * GitHubPageSize >= total)
                    {
                        break;
                    }
                }
            }

            return all;
        }, cancellationToken);
        if (listed.IsFailure)
        {
            return Result.Failure<GitHubRepoPageDto>(listed.Error!);
        }

        var term = search?.Trim();
        var matching = listed.Value
            .Where(r => string.IsNullOrEmpty(term) || r.FullName.Contains(term, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(r => r.Id)
            .OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var items = matching.Skip((page - 1) * PageSize).Take(PageSize).ToList();
        return Result.Success(new GitHubRepoPageDto(items, page, matching.Count > page * PageSize));
    }

    /// <summary>FR-81: the caller and their organisations, with whether the App is installed there and creating is possible.</summary>
    public async Task<Result<IReadOnlyList<GitHubOwnerDto>>> ListOwnersAsync(Guid userId, CancellationToken cancellationToken)
    {
        var session = await connections.SessionAsync(userId, cancellationToken);
        if (session.IsFailure)
        {
            return Result.Failure<IReadOnlyList<GitHubOwnerDto>>(session.Error!);
        }

        var user = session.Value;
        var listed = await CallValueAsync(user, async () =>
        {
            var installations = await api.ListUserInstallationsAsync(user.Token, cancellationToken);
            IReadOnlyList<string> organizations;
            try
            {
                organizations = await api.ListUserOrganizationsAsync(user.Token, cancellationToken);
            }
            catch (GitHubApiException ex) when (ex.Kind is GitHubFailureKind.Forbidden or GitHubFailureKind.NotFound)
            {
                // Best effort: organisations without the App are only offered when GitHub lists them.
                organizations = [];
            }

            return (installations, organizations);
        }, cancellationToken);
        if (listed.IsFailure)
        {
            return Result.Failure<IReadOnlyList<GitHubOwnerDto>>(listed.Error!);
        }

        var canCreate = await CanCreateRepositoriesAsync(user.App, cancellationToken);
        var owners = GitHubRules.Owners(
            user.Login,
            [.. listed.Value.installations.Select(i => new GitHubInstallationRef(i.Id, i.AccountLogin, i.AccountType))],
            listed.Value.organizations,
            canCreate);
        return Result.Success<IReadOnlyList<GitHubOwnerDto>>([.. owners.Select(o => new GitHubOwnerDto(o.Login, o.Type, o.InstallationId, o.AppInstalled, o.CanCreate))]);
    }

    /// <summary>FR-73, FR-86: a repository's default branch, whether it has code, its releases and its semver tags.</summary>
    public async Task<Result<GitHubRepoRefsDto>> RefsAsync(Guid userId, string owner, string repo, CancellationToken cancellationToken)
    {
        var session = await connections.SessionAsync(userId, cancellationToken);
        if (session.IsFailure)
        {
            return Result.Failure<GitHubRepoRefsDto>(session.Error!);
        }

        var user = session.Value;
        return await CallAsync(user, async () =>
        {
            var repository = await api.FindRepositoryAsync(user.Token, owner, repo, cancellationToken);
            if (repository is null)
            {
                return await NotVisibleAsync<GitHubRepoRefsDto>(user.App, owner, repo, cancellationToken);
            }

            var root = await api.ReadBranchRootAsync(user.Token, repository.Owner, repository.Name, repository.DefaultBranch, cancellationToken);
            var (releases, latest) = root is null ? ([], null) : await api.ListReleasesAsync(user.Token, repository.Owner, repository.Name, cancellationToken);
            var tags = root is null ? [] : await api.ListTagsAsync(user.Token, repository.Owner, repository.Name, cancellationToken);
            return Result.Success(new GitHubRepoRefsDto(
                repository.DefaultBranch,
                GitHubRules.IsEmptyRepository(root?.RootEntries),
                [.. releases.Where(r => !r.Draft).Select(r => new GitHubReleaseDto(r.TagName, r.Name, r.PublishedAt, r.TagName == latest))],
                [.. tags.Select(t => new GitHubTagDto(t.Name, t.Sha, GitHubRules.TagSemver(t.Name)))]));
        }, cancellationToken);
    }

    /// <summary>
    /// FR-81, §4.14: creates the repository with the user token (<c>POST /user/repos</c> or <c>/orgs/{org}/repos</c>,
    /// <c>auto_init</c>), after checking the App has Administration write (else <c>FORBIDDEN</c>) and is installed on the
    /// owner (else <c>GITHUB_APP_NOT_INSTALLED</c>, nothing created); then checks the App can access the new repository
    /// (an installation on "selected repositories" may not include it: <c>GITHUB_APP_NOT_INSTALLED</c>).
    /// </summary>
    public async Task<Result<GitHubRepoDto>> CreateRepoAsync(Guid userId, CreateGitHubRepoRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await connections.SessionAsync(userId, cancellationToken);
        if (session.IsFailure)
        {
            return Result.Failure<GitHubRepoDto>(session.Error!);
        }

        var user = session.Value;
        if (!await CanCreateRepositoriesAsync(user.App, cancellationToken))
        {
            return Result.Failure<GitHubRepoDto>(new DomainError(
                ErrorCodes.Forbidden, "The AIDE GitHub App doesn't have the Administration permission, so it can't create repositories. Create it on GitHub and pick it instead."));
        }

        var forUser = string.Equals(request.Owner, user.Login, StringComparison.OrdinalIgnoreCase);
        return await CallAsync(user, async () =>
        {
            var installations = await api.ListUserInstallationsAsync(user.Token, cancellationToken);
            var installation = installations.FirstOrDefault(i =>
                i.AccountType == (forUser ? GitHubOwnerType.User : GitHubOwnerType.Organization)
                && string.Equals(i.AccountLogin, request.Owner, StringComparison.OrdinalIgnoreCase));
            if (installation is null)
            {
                return Result.Failure<GitHubRepoDto>(new DomainError(
                    ErrorCodes.GitHubAppNotInstalled, $"Install the AIDE GitHub App on {request.Owner} first, then create the repository."));
            }

            GitHubRepository created;
            try
            {
                created = await api.CreateRepositoryAsync(
                    user.Token, forUser ? null : request.Owner, request.Name, string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(), request.Private, cancellationToken);
            }
            catch (GitHubApiException ex) when (ex.Kind == GitHubFailureKind.Unprocessable && ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<GitHubRepoDto>(new DomainError(ErrorCodes.RepoNameTaken, $"{request.Owner}/{request.Name} already exists. Pick another name, or pick the existing repository."));
            }
            catch (GitHubApiException ex) when (ex.Kind == GitHubFailureKind.Unprocessable)
            {
                return Result.Failure<GitHubRepoDto>(new DomainError(ErrorCodes.Validation, "GitHub refused the repository.") { Details = [$"name: {ex.Message}"] });
            }
            catch (GitHubApiException ex) when (ex.Kind is GitHubFailureKind.Forbidden or GitHubFailureKind.NotFound)
            {
                return Result.Failure<GitHubRepoDto>(new DomainError(ErrorCodes.Forbidden, $"GitHub didn't let you create a repository for {request.Owner}. {ex.Message}"));
            }

            LogCreated(logger, created.Id, installation.Id);

            // §4.14: the App must reach the new repository before the wizard continues.
            var reachable = await api.FindRepoInstallationAsync(Jwt(user.App), created.Owner, created.Name, cancellationToken);
            if (reachable != installation.Id)
            {
                return Result.Failure<GitHubRepoDto>(new DomainError(
                    ErrorCodes.GitHubAppNotInstalled,
                    $"{created.FullName} was created, but the AIDE GitHub App's installation on {request.Owner} covers selected repositories only. Add it to the installation, then pick it."));
            }

            // The creator owns (administers) the new repository.
            return Result.Success(ToDto(created with { CanPush = true }, installation.Id, empty: true));
        }, cancellationToken);
    }

    /// <summary>
    /// FR-82, FR-86: checks a <c>github_app</c> project's repository: the caller can access it with the App (their user
    /// token sees it, and the App's installation for it is <paramref name="installationId"/>) and may push to it (push,
    /// maintain or admin; else <c>FORBIDDEN</c>: AIDE never writes where the caller couldn't), and whether it has code.
    /// </summary>
    public async Task<Result<VerifiedAppRepo>> VerifyProjectRepoAsync(Guid userId, long installationId, string repoFullName, CancellationToken cancellationToken)
    {
        if (!ProjectSettingsRules.TryParseRepoFullName(repoFullName, out var name))
        {
            return Result.Failure<VerifiedAppRepo>(new DomainError(ErrorCodes.Validation, "The Git settings are invalid.") { Details = ["git.repoFullName: Use owner/name, e.g. acme/todo-app."] });
        }

        var session = await connections.SessionAsync(userId, cancellationToken);
        if (session.IsFailure)
        {
            return Result.Failure<VerifiedAppRepo>(session.Error!);
        }

        var user = session.Value;
        return await CallAsync(user, async () =>
        {
            var installation = await api.FindRepoInstallationAsync(Jwt(user.App), name.Owner, name.Name, cancellationToken);
            var repository = installation == installationId ? await api.FindRepositoryAsync(user.Token, name.Owner, name.Name, cancellationToken) : null;
            if (repository is null)
            {
                return Result.Failure<VerifiedAppRepo>(new DomainError(
                    ErrorCodes.GitHubAppNotInstalled, $"The AIDE GitHub App can't access {name.Owner}/{name.Name} for you. Install it on the repository, then pick it again."));
            }

            if (!repository.CanPush)
            {
                return Result.Failure<VerifiedAppRepo>(PushRequired(repository.FullName));
            }

            var root = await api.ReadBranchRootAsync(user.Token, repository.Owner, repository.Name, repository.DefaultBranch, cancellationToken);
            var hasCode = !GitHubRules.IsEmptyRepository(root?.RootEntries);
            return Result.Success(new VerifiedAppRepo(installationId, repository.Id, repository.FullName, repository.DefaultBranch, hasCode, hasCode ? root?.HeadSha : null));
        }, cancellationToken);
    }

    /// <summary>
    /// FR-83 "Switch to GitHub App": the App's installation and the repository id for a PAT project's repository, after
    /// checking that the App is installed on it (else <c>GITHUB_APP_NOT_INSTALLED</c>) and that the <b>caller</b> — with their
    /// own GitHub connection (else <c>GITHUB_NOT_CONNECTED</c>) — can see it and push to it (else <c>FORBIDDEN</c>). Holding a
    /// PAT project for a repository is no proof of access, so the App's installation tokens are only ever used for
    /// repositories the switching Owner could write to themselves. No installation token is minted here.
    /// </summary>
    public async Task<Result<(long InstallationId, long RepoId, string FullName)>> VerifySwitchAsync(Guid userId, string owner, string repo, CancellationToken cancellationToken)
    {
        var session = await connections.SessionAsync(userId, cancellationToken);
        if (session.IsFailure)
        {
            return Result.Failure<(long, long, string)>(session.Error!);
        }

        var user = session.Value;
        return await CallAsync(user, async () =>
        {
            if (await api.FindRepoInstallationAsync(Jwt(user.App), owner, repo, cancellationToken) is not { } installationId)
            {
                return Result.Failure<(long, long, string)>(new DomainError(
                    ErrorCodes.GitHubAppNotInstalled, $"The AIDE GitHub App isn't installed on {owner}/{repo}. Install it on the repository, then switch."));
            }

            var repository = await api.FindRepositoryAsync(user.Token, owner, repo, cancellationToken);
            if (repository is null || !repository.CanPush)
            {
                return Result.Failure<(long, long, string)>(PushRequired($"{owner}/{repo}"));
            }

            return Result.Success((installationId, repository.Id, repository.FullName));
        }, cancellationToken);
    }

    private static DomainError PushRequired(string fullName) =>
        new(ErrorCodes.Forbidden, $"Your GitHub account can't push to {fullName}. A project needs a repository you can write to (push, maintain or admin).");

    /// <summary>FR-83: whether the App is installed on a repository (<c>null</c>: GitHub couldn't say).</summary>
    public async Task<bool?> IsAppInstalledAsync(GitHubAppCredentials app, string owner, string repo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        try
        {
            return await api.FindRepoInstallationAsync(Jwt(app), owner, repo, cancellationToken) is not null;
        }
        catch (GitHubApiException)
        {
            return null;
        }
        catch (GitHubRateLimitedException)
        {
            return null;
        }
    }

    /// <summary>§4.14: "Create repository" needs Administration write.</summary>
    public async Task<bool> CanCreateRepositoriesAsync(GitHubAppCredentials app, CancellationToken cancellationToken) =>
        await appInfo.GetAsync(app, api, cancellationToken) is { } info && GitHubRules.CanCreateRepositories(info.Permissions);

    /// <summary>Runs a GitHub call with the user token and maps its failures (401 marks the connection invalid).</summary>
    private async Task<Result<T>> CallValueAsync<T>(GitHubUserSession user, Func<Task<T>> call, CancellationToken cancellationToken) =>
        await CallAsync(user, async () => Result.Success(await call()), cancellationToken);

    private async Task<Result<T>> CallAsync<T>(GitHubUserSession user, Func<Task<Result<T>>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (GitHubApiException ex) when (ex.Kind == GitHubFailureKind.Unauthorized)
        {
            return Result.Failure<T>(await connections.RejectedAsync(user.UserId, cancellationToken));
        }
        catch (GitHubApiException ex)
        {
            LogFailed(logger, ex.Status, ex.Kind);
            return Result.Failure<T>(ex.Kind switch
            {
                GitHubFailureKind.Forbidden => new DomainError(ErrorCodes.Forbidden, $"GitHub refused the request. {ex.Message}"),
                GitHubFailureKind.NotFound => new DomainError(ErrorCodes.NotFound, "The repository was not found, or you can't access it."),
                _ => new DomainError(ErrorCodes.UpstreamError, $"GitHub failed or couldn't be reached. {ex.Message}"),
            });
        }
    }

    /// <summary>A repository the user token can't see: the App isn't installed on it, or it doesn't exist for the caller.</summary>
    private async Task<Result<T>> NotVisibleAsync<T>(GitHubAppCredentials app, string owner, string repo, CancellationToken cancellationToken) =>
        await api.FindRepoInstallationAsync(Jwt(app), owner, repo, cancellationToken) is null
            ? Result.Failure<T>(new DomainError(ErrorCodes.GitHubAppNotInstalled, $"The AIDE GitHub App isn't installed on {owner}/{repo}. Install it, then pick the repository again."))
            : Result.Failure<T>(new DomainError(ErrorCodes.NotFound, "The repository was not found, or you can't access it."));

    private string Jwt(GitHubAppCredentials app) => GitHubAppJwt.Create(app.AppId, app.PrivateKeyPem, time.GetUtcNow());

    private static GitHubInstallationDto ToDto(GitHubInstallation i) =>
        new(i.Id, new GitHubAccountDto(i.AccountLogin, i.AccountType), i.RepositorySelection, GitHubRules.ManageInstallationUrl(i.Id, i.AccountLogin, i.AccountType));

    private static GitHubRepoDto ToDto(GitHubRepository r, long installationId, bool? empty) =>
        new(r.Id, r.FullName, r.Owner, r.Name, r.Private, r.DefaultBranch, installationId, empty, r.HtmlUrl, r.CanPush);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created GitHub repository {RepoId} (App installation {InstallationId})")]
    private static partial void LogCreated(ILogger logger, long repoId, long installationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub request failed with HTTP {Status} ({Kind})")]
    private static partial void LogFailed(ILogger logger, int? status, GitHubFailureKind kind);
}
