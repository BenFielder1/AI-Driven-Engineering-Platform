using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Bitbucket;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Bitbucket;

/// <summary>
/// A Bitbucket repository checked for binding to a project (FR-135, FR-136): the Bitbucket Cloud instance row, the binding
/// connection and its owner, the repository UUID and <c>workspace/slug</c>, default branch, web URL, head commit (<c>null</c>
/// for an empty repo) and whether it already has code (FR-86: more than starter files).
/// </summary>
public sealed record VerifiedBitbucketRepo(
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
/// FR-135, FR-136, FR-141: binding a Bitbucket repository to a project (create, import, re-bind in Settings → Git) and the
/// import's reads, with the caller's own connection. The checks follow <see cref="GitBindingRules.CanRebind"/>: an Owner
/// (<c>FORBIDDEN</c>), an existing Bitbucket connection (<c>NOT_FOUND</c>), their own (<c>CREDENTIAL_NOT_OWNER</c>), valid
/// (<c>GIT_NOT_CONNECTED</c>), and write access to the repository as Bitbucket reports it
/// (<c>GET /user/permissions/repositories?q=repository.uuid="…"</c>: <c>write</c> or <c>admin</c>, else <c>FORBIDDEN</c>).
/// Bitbucket failures map as in <see cref="BitbucketConnectionService.CallAsync{T}"/>; errors name their request field.
/// </summary>
public sealed class BitbucketProjectRepos(AideDbContext db, BitbucketConnectionService connections, IBitbucketConsumer consumer, IGitProviderResolver providers)
{
    private const string Host = "Bitbucket";

    /// <param name="callerId">The user binding the repo.</param>
    /// <param name="callerRole">The caller's project role (<see cref="ProjectRole.Owner"/> when creating a project).</param>
    /// <param name="connectionId">The caller's connection to bind.</param>
    /// <param name="repoId">The repository UUID (<c>{…}</c>).</param>
    /// <param name="repoFullName">Its <c>workspace/slug</c> (the workspace scopes the lookup).</param>
    /// <param name="fieldPrefix">Prefix of the fields in errors (<c>git.</c> on create).</param>
    /// <param name="cancellationToken">Cancels the checks.</param>
    public async Task<Result<VerifiedBitbucketRepo>> VerifyAsync(
        Guid callerId, ProjectRole? callerRole, Guid connectionId, string repoId, string repoFullName, string fieldPrefix, CancellationToken cancellationToken)
    {
        var instanceId = (await consumer.GetAsync(cancellationToken)).InstanceId;
        var connection = await db.GitConnections.AsNoTracking()
            .Where(c => c.Id == connectionId && c.Provider == GitHostKind.Bitbucket)
            .Select(c => new GitConnectionRef(c.Id, c.UserId, c.InstanceId, c.Status == GitConnectionStatus.Valid))
            .FirstOrDefaultAsync(cancellationToken);
        var connectionField = fieldPrefix + "connectionId";
        var repoField = fieldPrefix + "repoId";
        var allowed = GitBindingRules.CanRebind(callerId, callerRole, connection, instanceId, hasWriteAccess: true, Host);
        if (allowed.IsFailure)
        {
            var error = allowed.Error!;
            return Result.Failure<VerifiedBitbucketRepo>(error.Code is ErrorCodes.NotFound or ErrorCodes.CredentialNotOwner or ErrorCodes.GitNotConnected
                ? error with { Field = connectionField }
                : error.Code == ErrorCodes.Validation
                    ? error with { Details = [.. error.Details.Select(d => d.Replace("gitConnectionId:", connectionField + ":", StringComparison.Ordinal))] }
                    : error);
        }

        var (workspace, _) = BitbucketProjectRules.SplitFullName(repoFullName);
        var read = await connections.CallAsync(callerId, connectionId, async call =>
        {
            var repo = await call.Client.GetRepositoryAsync(call.Auth, workspace, repoId, cancellationToken);
            if (repo is null || !string.Equals(repo.Uuid, repoId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var permission = await call.Client.GetRepositoryPermissionAsync(call.Auth, repo.Uuid, cancellationToken);
            var branch = repo.MainBranch ?? Domain.Projects.ProjectSettingsRules.DefaultBranch;
            var head = repo.MainBranch is null ? null : await call.Client.GetBranchHeadAsync(call.Auth, repo.FullName, branch, cancellationToken);
            var root = head is null ? null : await call.Client.ListRootEntriesAsync(call.Auth, repo.FullName, head, cancellationToken);
            return new { Repo = repo, Permission = permission, Branch = branch, Head = head, HasCode = head is not null && !Domain.GitHub.GitHubRules.IsEmptyRepository(root) };
        }, cancellationToken);
        if (read.IsFailure)
        {
            var error = read.Error!;
            return Result.Failure<VerifiedBitbucketRepo>(error.Code switch
            {
                ErrorCodes.GitNotConnected => error with { Field = connectionField },
                ErrorCodes.NotFound or ErrorCodes.Forbidden => error with { Field = repoField },
                _ => error,
            });
        }

        if (read.Value is not { } found)
        {
            return Result.Failure<VerifiedBitbucketRepo>(new DomainError(ErrorCodes.NotFound, "The Bitbucket repository wasn't found, or your Bitbucket account can't see it.") { Field = repoField });
        }

        var bindable = GitBindingRules.CanRebind(callerId, callerRole, connection, instanceId, GitBindingRules.BitbucketHasWriteAccess(found.Permission), Host);
        if (bindable.IsFailure)
        {
            return Result.Failure<VerifiedBitbucketRepo>(bindable.Error!.Code == ErrorCodes.Forbidden ? bindable.Error with { Field = repoField } : bindable.Error);
        }

        var fullName = found.Repo.FullName.Length > 0 ? found.Repo.FullName : repoFullName;
        var webUrl = Uri.TryCreate(found.Repo.WebUrl, UriKind.Absolute, out var web)
            && string.Equals(web.Host, new Uri(BitbucketRules.WebBaseUrl).Host, StringComparison.OrdinalIgnoreCase) && web.Scheme == Uri.UriSchemeHttps
            ? web.AbsoluteUri.TrimEnd('/')
            : BitbucketProjectRules.RepoWebUrl(fullName);
        return Result.Success(new VerifiedBitbucketRepo(
            instanceId,
            connectionId,
            callerId,
            found.Repo.Uuid,
            fullName,
            found.Branch,
            webUrl,
            found.Head,
            found.HasCode));
    }

    /// <summary>FR-141: lookups of <paramref name="tags"/> in the repo with the caller's connection: the ones that exist, with their commits.</summary>
    public async Task<Result<IReadOnlyList<(string Tag, string Commit)>>> FindTagsAsync(
        Guid callerId, Guid connectionId, string fullName, IEnumerable<string> tags, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var wanted = tags.Distinct(StringComparer.Ordinal).ToList();
        return await connections.CallAsync<IReadOnlyList<(string Tag, string Commit)>>(callerId, connectionId, async call =>
        {
            var found = new List<(string Tag, string Commit)>();
            foreach (var tag in wanted)
            {
                if (await call.Client.GetTagAsync(call.Auth, fullName, tag, cancellationToken) is { Hash: { } hash })
                {
                    found.Add((tag, hash));
                }
            }

            return found;
        }, cancellationToken);
    }

    /// <summary>FR-141 (<c>GET /api/me/git/bitbucket/repos/:repoId/refs</c>): the repo's semver tags and default branch, with the suggestion.</summary>
    public async Task<Result<GitHostReleaseRefs>> RefsAsync(Guid callerId, Guid connectionId, string repoId, CancellationToken cancellationToken)
    {
        // The caller's own connection only (another user's is NOT_FOUND, as for the repo picker).
        var owned = await db.GitConnections.AsNoTracking()
            .AnyAsync(c => c.Id == connectionId && c.UserId == callerId && c.Provider == GitHostKind.Bitbucket, cancellationToken);
        if (!owned)
        {
            return Result.Failure<GitHostReleaseRefs>(new DomainError(ErrorCodes.NotFound, "The Bitbucket connection was not found."));
        }

        return await providers.Resolve(GitProvider.Bitbucket).FindReleaseRefsAsync(connectionId, repoId, cancellationToken);
    }
}
