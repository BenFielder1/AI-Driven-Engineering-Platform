using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.GitLab;

/// <summary>
/// A GitLab repository checked for binding to a project (FR-132, FR-136): the instance, the binding connection and its
/// owner, GitLab's project id and canonical path, default branch, web URL, head commit (<c>null</c> for an empty repo) and
/// whether it already has code (FR-86: more than starter files).
/// </summary>
public sealed record VerifiedGitLabRepo(
    Guid InstanceId,
    Guid ConnectionId,
    Guid OwnerUserId,
    string RepoId,
    string FullName,
    string DefaultBranch,
    string WebUrl,
    string? HeadSha,
    bool HasCode);

/// <summary>
/// FR-132, FR-136, FR-141: binding a GitLab repository to a project (create, import, re-bind in Settings → Git) and the
/// import's reads, with the caller's own connection. The checks follow <see cref="GitBindingRules.CanRebind"/>: an Owner
/// (<c>FORBIDDEN</c>), an existing GitLab connection (<c>NOT_FOUND</c>), their own (<c>CREDENTIAL_NOT_OWNER</c>), on the
/// repo's instance (<c>VALIDATION</c>), valid on an enabled instance (<c>GIT_NOT_CONNECTED</c>), and Developer access or
/// higher on the repo as GitLab reports it (<c>FORBIDDEN</c>). GitLab failures map as in
/// <see cref="GitLabConnectionService.CallAsync{T}"/>.
/// </summary>
public sealed class GitLabProjectRepos(AideDbContext db, GitLabConnectionService connections, IGitLabInstances instances, IGitProviderResolver providers)
{
    private const string Host = "GitLab";

    /// <param name="callerId">The user binding the repo.</param>
    /// <param name="callerRole">The caller's project role (<see cref="ProjectRole.Owner"/> when creating a project).</param>
    /// <param name="instanceId">The repo's instance.</param>
    /// <param name="connectionId">The caller's connection to bind.</param>
    /// <param name="repoId">GitLab's project id.</param>
    /// <param name="fieldPrefix">Prefix of the connection field in validation details (<c>git.</c> on create).</param>
    /// <param name="cancellationToken">Cancels the checks.</param>
    public async Task<Result<VerifiedGitLabRepo>> VerifyAsync(
        Guid callerId, ProjectRole? callerRole, Guid instanceId, Guid connectionId, string repoId, string fieldPrefix, CancellationToken cancellationToken)
    {
        var connection = await db.GitConnections.AsNoTracking()
            .Where(c => c.Id == connectionId && c.Provider == GitHostKind.Gitlab)
            .Select(c => new GitConnectionRef(c.Id, c.UserId, c.InstanceId, c.Status == GitConnectionStatus.Valid))
            .FirstOrDefaultAsync(cancellationToken);
        var connectionField = fieldPrefix + "connectionId";
        var repoField = fieldPrefix + "repoId";
        var allowed = Check(GitBindingRules.CanRebind(callerId, callerRole, connection, instanceId, hasWriteAccess: true, Host), fieldPrefix);
        if (allowed.IsFailure)
        {
            // The connection is missing, another user's or invalid (an Owner check failure isn't about a field).
            var error = allowed.Error!;
            return Result.Failure<VerifiedGitLabRepo>(error.Code is ErrorCodes.NotFound or ErrorCodes.CredentialNotOwner or ErrorCodes.GitNotConnected
                ? error with { Field = connectionField }
                : error);
        }

        var instance = await instances.GetAsync(instanceId, cancellationToken);
        if (instance is null)
        {
            return Result.Failure<VerifiedGitLabRepo>(new DomainError(ErrorCodes.NotFound, "The GitLab instance was not found.") { Field = fieldPrefix + "instanceId" });
        }

        if (!instance.Enabled)
        {
            return Result.Failure<VerifiedGitLabRepo>(new DomainError(ErrorCodes.GitNotConnected, $"The GitLab instance {instance.Name} is disabled.") { Field = connectionField });
        }

        var read = await connections.CallAsync(callerId, connectionId, async (client, token) =>
        {
            var project = await client.GetProjectAsync(token, repoId, cancellationToken);
            if (project is null)
            {
                return null;
            }

            var branch = project.DefaultBranch ?? Domain.Projects.ProjectSettingsRules.DefaultBranch;
            var head = project.EmptyRepo ? null : await client.GetBranchHeadAsync(token, project.ExternalId, branch, cancellationToken);
            var root = head is null ? null : await client.ListRootEntriesAsync(token, project.ExternalId, branch, cancellationToken);
            return new { Project = project, Branch = branch, Head = head, HasCode = head is not null && !Domain.GitHub.GitHubRules.IsEmptyRepository(root) };
        }, cancellationToken);
        if (read.IsFailure)
        {
            // GitLab rejected the connection (GIT_NOT_CONNECTED), or can't show the repo (NOT_FOUND, FORBIDDEN).
            var error = read.Error!;
            return Result.Failure<VerifiedGitLabRepo>(error.Code switch
            {
                ErrorCodes.GitNotConnected => error with { Field = connectionField },
                ErrorCodes.NotFound or ErrorCodes.Forbidden => error with { Field = repoField },
                _ => error,
            });
        }

        if (read.Value is not { } found)
        {
            return Result.Failure<VerifiedGitLabRepo>(new DomainError(ErrorCodes.NotFound, "The GitLab repository wasn't found, or your GitLab account can't see it.") { Field = repoField });
        }

        var developer = GitLabProjectRules.HasDeveloperAccess(found.Project.ProjectAccess, found.Project.GroupAccess);
        var bindable = Check(GitBindingRules.CanRebind(callerId, callerRole, connection, instanceId, developer, Host), fieldPrefix);
        if (bindable.IsFailure)
        {
            // Below Developer access on the repo.
            return Result.Failure<VerifiedGitLabRepo>(bindable.Error!.Code == ErrorCodes.Forbidden ? bindable.Error with { Field = repoField } : bindable.Error);
        }

        var webUrl = Uri.TryCreate(found.Project.WebUrl, UriKind.Absolute, out var web) && Domain.GitHosts.GitHostAddressRules.SameOrigin(instance.BaseUrl, web)
            ? web.AbsoluteUri.TrimEnd('/')
            : GitLabProjectRules.ProjectWebUrl(instance.BaseUrl, found.Project.PathWithNamespace);
        return Result.Success(new VerifiedGitLabRepo(
            instanceId,
            connectionId,
            callerId,
            found.Project.ExternalId,
            found.Project.PathWithNamespace,
            found.Branch,
            webUrl,
            found.Head,
            found.HasCode));
    }

    /// <summary>FR-141: exact lookups of <paramref name="tags"/> in the repo with the caller's connection: the ones that exist, with their commits.</summary>
    public async Task<Result<IReadOnlyList<(string Tag, string Commit)>>> FindTagsAsync(
        Guid callerId, Guid connectionId, string repoId, IEnumerable<string> tags, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var wanted = tags.Distinct(StringComparer.Ordinal).ToList();
        return await connections.CallAsync<IReadOnlyList<(string Tag, string Commit)>>(callerId, connectionId, async (client, token) =>
        {
            var found = new List<(string Tag, string Commit)>();
            foreach (var tag in wanted)
            {
                if (await client.FindTagCommitAsync(token, repoId, tag, cancellationToken) is { } sha)
                {
                    found.Add((tag, sha));
                }
            }

            return found;
        }, cancellationToken);
    }

    /// <summary>FR-141 (<c>GET /api/me/git/gitlab/repos/:repoId/refs</c>): the repo's releases, tags and default branch, with the suggestion.</summary>
    public async Task<Result<GitHostReleaseRefs>> RefsAsync(Guid callerId, Guid connectionId, string repoId, CancellationToken cancellationToken)
    {
        // The caller's own connection only (another user's is NOT_FOUND, as for the repo picker).
        var owned = await db.GitConnections.AsNoTracking()
            .AnyAsync(c => c.Id == connectionId && c.UserId == callerId && c.Provider == GitHostKind.Gitlab, cancellationToken);
        if (!owned)
        {
            return Result.Failure<GitHostReleaseRefs>(new DomainError(ErrorCodes.NotFound, "The GitLab connection was not found."));
        }

        return await providers.Resolve(GitProvider.Gitlab).FindReleaseRefsAsync(connectionId, repoId, cancellationToken);
    }

    /// <summary>Validation details name the request's connection field (<c>gitConnectionId</c> → <c>connectionId</c>).</summary>
    private static Result Check(Result result, string fieldPrefix) =>
        result.IsFailure && result.Error!.Code == ErrorCodes.Validation
            ? Result.Failure(result.Error with
            {
                Details = [.. result.Error.Details.Select(d => d.Replace("gitConnectionId:", fieldPrefix + "connectionId:", StringComparison.Ordinal))],
            })
            : result;
}
