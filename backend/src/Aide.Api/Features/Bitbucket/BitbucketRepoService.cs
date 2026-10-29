using Aide.Domain.Common;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Bitbucket;
using Aide.Infrastructure.Git;

namespace Aide.Api.Features.Bitbucket;

/// <summary>
/// FR-135: the Bitbucket repo picker, the workspaces and projects a repository can be created in, and repository creation
/// with an initial README commit on <c>main</c>, all with the caller's own connection
/// (<see cref="BitbucketConnectionService.CallAsync{T}"/>: per-connection budget, refresh on 401, invalid after a second 401).
/// </summary>
public sealed partial class BitbucketRepoService(
    BitbucketConnectionService connections,
    IBitbucketRepoInitializer initializer,
    BitbucketWorkspaceCache workspaceCache,
    ILogger<BitbucketRepoService> logger)
{
    /// <summary>The most workspaces the owners list shows (§4.19 budget: one projects call each).</summary>
    public const int MaxOwnerWorkspaces = 50;

    /// <summary>
    /// FR-135: one page of repositories the caller can write to: the cursor's workspace (the first one without a cursor)
    /// (<c>GET /repositories/{ws}?role=contributor&amp;q=name~"…"&amp;sort=-updated_on</c>), moving on to the next workspace
    /// when one has no more pages. The workspace list (<c>GET /user/workspaces</c>) is read once per connection and reused for
    /// five minutes (<see cref="BitbucketWorkspaceCache"/>), not on every page.
    /// </summary>
    public async Task<Result<BitbucketRepoPageDto>> ListReposAsync(Guid userId, Guid connectionId, string? search, string? cursor, CancellationToken cancellationToken)
    {
        var position = BitbucketRules.ParseCursor(cursor);
        if (position.IsFailure)
        {
            return Result.Failure<BitbucketRepoPageDto>(position.Error!);
        }

        var query = BitbucketRules.RepoQuery(search);
        var (cursorWorkspace, page) = (position.Value.Workspace, position.Value.Page);
        return await connections.CallAsync(userId, connectionId, async call =>
        {
            var workspaces = await workspaceCache.GetAsync(connectionId, async () =>
                [.. (await call.Client.ListWorkspacesAsync(call.Auth, cancellationToken)).Select(w => w.Slug)]);
            var workspace = cursorWorkspace ?? (workspaces.Count > 0 ? workspaces[0] : null);
            if (workspace is null)
            {
                return new BitbucketRepoPageDto([], null);
            }

            var result = await call.Client.ListRepositoriesAsync(call.Auth, workspace, query, page, cancellationToken);
            return new BitbucketRepoPageDto(
                [.. result.Items.Select(ToDto)],
                BitbucketRules.NextCursor(workspaces, workspace, page, result.HasNext));
        }, cancellationToken);
    }

    /// <summary>
    /// FR-135: where a repository can be created: the caller's first 50 workspaces (<c>GET /user/workspaces</c>), each with
    /// the first page (100) of its projects (<c>GET /workspaces/{ws}/projects</c>). <c>truncated</c> says more workspaces
    /// exist, and a workspace's <c>projectsTruncated</c> that it has more projects. A workspace whose projects Bitbucket won't
    /// list is shown without projects.
    /// </summary>
    public Task<Result<BitbucketOwnersDto>> ListOwnersAsync(Guid userId, Guid connectionId, CancellationToken cancellationToken) =>
        connections.CallAsync(userId, connectionId, async call =>
        {
            var workspaces = await call.Client.ListWorkspacesPageAsync(call.Auth, MaxOwnerWorkspaces, 1, cancellationToken);
            var owners = new List<BitbucketWorkspaceDto>(workspaces.Items.Count);
            foreach (var ws in workspaces.Items.Take(MaxOwnerWorkspaces))
            {
                BitbucketPage<BitbucketProject> projects;
                try
                {
                    projects = await call.Client.ListProjectsPageAsync(call.Auth, ws.Slug, cancellationToken);
                }
                catch (BitbucketApiException ex) when (ex.Status is 403 or 404)
                {
                    projects = new BitbucketPage<BitbucketProject>([], false);
                }

                owners.Add(new BitbucketWorkspaceDto(
                    ws.Slug, ws.Name, ws.IsAdmin, [.. projects.Items.Select(p => new BitbucketProjectDto(p.Key, p.Name, p.IsPrivate))], projects.HasNext));
            }

            return new BitbucketOwnersDto(owners, workspaces.HasNext || workspaces.Items.Count > MaxOwnerWorkspaces);
        }, cancellationToken);

    /// <summary>
    /// FR-135: creates <c>{owner}/{slug of name}</c> in project <c>project</c> (<c>POST /repositories/{ws}/{slug}</c>,
    /// <c>scm: git</c>), then pushes an initial commit with a README to <c>main</c> (Bitbucket has no auto-init). Errors:
    /// <c>REPO_NAME_TAKEN</c> 409, <c>FORBIDDEN</c> 403, <c>NOT_FOUND</c> (no such workspace), <c>UPSTREAM_RATE_LIMITED</c>
    /// 429, <c>VALIDATION</c>; 502 <c>UPSTREAM_ERROR</c> when the README commit can't be made or pushed (git fails or is
    /// missing, or the temp folder can't be written; the empty repository then exists).
    /// </summary>
    public async Task<Result<BitbucketRepoDto>> CreateRepoAsync(Guid userId, CreateBitbucketRepoRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var valid = BitbucketRules.ValidateNewRepo(request.Owner, request.Project, request.Name, request.Description);
        if (valid.IsFailure)
        {
            return Result.Failure<BitbucketRepoDto>(valid.Error!);
        }

        var name = request.Name.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        var create = new BitbucketCreateRepository(request.Owner.Trim(), BitbucketRules.Slug(name), name, request.Project.Trim(), description, request.Private);
        var created = await connections.CallAsync<Result<BitbucketRepository>>(userId, request.ConnectionId, async call =>
        {
            BitbucketRepository repository;
            try
            {
                repository = await call.Client.CreateRepositoryAsync(call.Auth, create, cancellationToken);
            }
            catch (BitbucketApiException ex) when (ex.Status is 400 or 403 or 404 or 409 or 429)
            {
                var code = BitbucketRules.CreateRepoErrorCode(ex.Status!.Value, ex.UpstreamMessage);
                LogCreateRefused(logger, ex.Status, code);
                return Result.Failure<BitbucketRepository>(code switch
                {
                    ErrorCodes.RepoNameTaken => new DomainError(code, "A repository with that name already exists."),
                    ErrorCodes.Forbidden => new DomainError(code, "Bitbucket doesn't let your account create repositories there."),
                    ErrorCodes.NotFound => new DomainError(code, "That Bitbucket workspace or project wasn't found, or your account can't see it."),
                    ErrorCodes.UpstreamRateLimited => new DomainError(code, "Bitbucket is rate limiting requests. Try again in a few minutes.") { RetryAfter = ex.RetryAfter },
                    ErrorCodes.Validation => new DomainError(code, "Bitbucket refused the repository.") { Details = [$"name: {ex.UpstreamMessage ?? "Bitbucket refused the name or project."}"] },
                    _ => new DomainError(ErrorCodes.UpstreamError, "Bitbucket returned an error. Try again later."),
                });
            }

            var fullName = repository.FullName.Length > 0 ? repository.FullName : $"{create.Workspace}/{create.Slug}";
            try
            {
                await initializer.PushReadmeAsync(fullName, name, description, call.Credential, call.Auth.Token, cancellationToken);
            }
            catch (Exception ex) when (ex is BitbucketInitialPushException or GitCommandException or IOException or UnauthorizedAccessException)
            {
                // git failed, timed out or is missing, or the temp folder couldn't be written: the empty repository stays.
                LogPushFailed(logger, repository.Uuid, ex.GetType().Name);
                return Result.Failure<BitbucketRepository>(new DomainError(ErrorCodes.UpstreamError, $"Bitbucket created {fullName}, but AIDE couldn't push its first commit. Pick it in the repository list to use it, or delete it in Bitbucket and try again.")
                {
                    Details = [ex is BitbucketInitialPushException or GitCommandException ? ex.Message : "The README commit couldn't be prepared on the AIDE server."],
                });
            }

            return Result.Success(repository with { MainBranch = BitbucketRepoInitializer.DefaultBranch });
        }, cancellationToken);
        if (created.IsFailure)
        {
            return Result.Failure<BitbucketRepoDto>(created.Error!);
        }

        if (created.Value.IsFailure)
        {
            return Result.Failure<BitbucketRepoDto>(created.Value.Error!);
        }

        var repo = created.Value.Value;
        LogCreated(logger, repo.Uuid, request.ConnectionId);
        return Result.Success(ToDto(repo));
    }

    private static BitbucketRepoDto ToDto(BitbucketRepository r) =>
        new(r.Uuid, r.FullName, r.Name, r.Slug, r.WorkspaceSlug, r.ProjectKey, r.MainBranch, r.IsPrivate, r.WebUrl, r.UpdatedOn);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bitbucket repository {RepositoryId} created with connection {ConnectionId}")]
    private static partial void LogCreated(ILogger logger, string repositoryId, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bitbucket refused to create a repository (HTTP {Status}, {ErrorCode})")]
    private static partial void LogCreateRefused(ILogger logger, int? status, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bitbucket repository {RepositoryId} was created but its README push failed ({Failure})")]
    private static partial void LogPushFailed(ILogger logger, string repositoryId, string failure);
}
