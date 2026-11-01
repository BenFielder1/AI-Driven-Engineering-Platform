using Aide.Domain.Common;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.GitLab;

namespace Aide.Api.Features.GitLab;

/// <summary>
/// FR-132: the GitLab repo picker, the owners a project can be created in, and project creation, all with the caller's own
/// connection (<see cref="GitLabConnectionService.CallAsync{T}"/>: refresh on 401, invalid after a second 401).
/// </summary>
public sealed partial class GitLabRepoService(GitLabConnectionService connections, ILogger<GitLabRepoService> logger)
{
    public async Task<Result<GitLabRepoPageDto>> ListReposAsync(Guid userId, Guid connectionId, string? search, string? cursor, CancellationToken cancellationToken)
    {
        if (cursor is { Length: > 0 } && !long.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return Result.Failure<GitLabRepoPageDto>(new DomainError(ErrorCodes.Validation, "The request is invalid.") { Details = ["cursor: Use the nextCursor of the previous page."] });
        }

        var page = await connections.CallAsync(userId, connectionId, (client, token) => client.ListProjectsAsync(token, search, cursor, cancellationToken), cancellationToken);
        return page.IsFailure
            ? Result.Failure<GitLabRepoPageDto>(page.Error!)
            : Result.Success(new GitLabRepoPageDto([.. page.Value.Items.Select(ToDto)], page.Value.NextCursor));
    }

    /// <summary>
    /// FR-132: the personal namespace, then the groups where the caller is at least Developer
    /// (<c>GET /groups?min_access_level=30</c>), each with whether its <c>project_creation_level</c> lets them create a project.
    /// The caller's level per group comes from the Maintainer (40) and Owner (50) group lists.
    /// </summary>
    public async Task<Result<IReadOnlyList<GitLabOwnerDto>>> ListOwnersAsync(Guid userId, Guid connectionId, CancellationToken cancellationToken)
    {
        var owners = await connections.CallAsync(userId, connectionId, (client, token) => GitLabReaders.OwnersAsync(client, token, cancellationToken), cancellationToken);
        return owners.IsFailure
            ? Result.Failure<IReadOnlyList<GitLabOwnerDto>>(owners.Error!)
            : Result.Success<IReadOnlyList<GitLabOwnerDto>>([.. owners.Value.Select(o => new GitLabOwnerDto(
                o.Id, EnumStrings.Parse<GitLabOwnerKind>(o.Kind), o.FullPath, o.Name, o.CanCreate, o.Reason))]);
    }

    /// <summary>
    /// FR-132: creates the project in <c>owner</c> (the personal namespace or a group's full path) with a README on
    /// <c>main</c>. Errors: <c>REPO_NAME_TAKEN</c> 409, <c>FORBIDDEN</c> 403 (not allowed in the group), <c>NOT_FOUND</c> (no
    /// such namespace), <c>UPSTREAM_RATE_LIMITED</c> 429 (the GitLab.com project-creation limit), <c>VALIDATION</c>.
    /// </summary>
    public async Task<Result<GitLabRepoDto>> CreateRepoAsync(Guid userId, CreateGitLabRepoRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var valid = GitLabRules.ValidateNewProject(request.Name, request.Path, request.Description);
        if (valid.IsFailure)
        {
            return Result.Failure<GitLabRepoDto>(valid.Error!);
        }

        var ownerPath = request.Owner.Trim().Trim('/');
        if (ownerPath.Length == 0)
        {
            return Result.Failure<GitLabRepoDto>(new DomainError(ErrorCodes.Validation, "The repository is invalid.") { Details = ["owner: Choose where to create the project."] });
        }

        var created = await connections.CallAsync<Result<GitLabProject>>(userId, request.ConnectionId, async (client, token) =>
        {
            var user = await client.GetUserAsync(token, cancellationToken);
            long? namespaceId = null;
            if (!string.Equals(ownerPath, user.Username, StringComparison.OrdinalIgnoreCase))
            {
                var ns = await client.GetNamespaceAsync(token, ownerPath, cancellationToken);
                if (ns is null)
                {
                    return Result.Failure<GitLabProject>(new DomainError(ErrorCodes.NotFound, "That GitLab group wasn't found, or your account can't see it."));
                }

                namespaceId = ns.Id;
            }

            try
            {
                return Result.Success(await client.CreateProjectAsync(
                    token,
                    new GitLabCreateProject(request.Name.Trim(), request.Path?.Trim(), namespaceId, request.Description?.Trim(), request.Private),
                    cancellationToken));
            }
            catch (GitLabApiException ex) when (ex.Status is 400 or 403 or 409 or 422 or 429)
            {
                var code = GitLabRules.CreateProjectErrorCode(ex.Status!.Value, ex.UpstreamMessage);
                LogCreateRefused(logger, ex.Status, code);
                return Result.Failure<GitLabProject>(code switch
                {
                    ErrorCodes.RepoNameTaken => new DomainError(code, "A repository with that name already exists."),
                    ErrorCodes.Forbidden => new DomainError(code, "GitLab doesn't let your account create projects there."),
                    ErrorCodes.UpstreamRateLimited => new DomainError(code, "GitLab is rate limiting project creation. Try again in a few minutes.") { RetryAfter = ex.RetryAfter },
                    ErrorCodes.Validation => new DomainError(code, "GitLab refused the project.") { Details = [$"name: {ex.UpstreamMessage ?? "GitLab refused the name or path."}"] },
                    _ => new DomainError(ErrorCodes.UpstreamError, "GitLab returned an error. Try again later."),
                });
            }
        }, cancellationToken);
        if (created.IsFailure)
        {
            return Result.Failure<GitLabRepoDto>(created.Error!);
        }

        if (created.Value.IsFailure)
        {
            return Result.Failure<GitLabRepoDto>(created.Value.Error!);
        }

        var project = created.Value.Value;
        LogCreated(logger, project.Id, request.ConnectionId);
        return Result.Success(ToDto(project));
    }

    private static GitLabRepoDto ToDto(GitLabProject p) =>
        new(p.ExternalId, p.PathWithNamespace, p.Name, p.Path, p.DefaultBranch, p.WebUrl, p.NamespaceFullPath, p.Visibility, p.LastActivityAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitLab project {ProjectId} created with connection {ConnectionId}")]
    private static partial void LogCreated(ILogger logger, long projectId, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitLab refused to create a project (HTTP {Status}, {ErrorCode})")]
    private static partial void LogCreateRefused(ILogger logger, int? status, string errorCode);
}
