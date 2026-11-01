using Aide.Api.Features.Connections;
using Aide.Api.Features.GitHub;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Trackers;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Aide.Infrastructure.Trackers;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Trackers;

/// <summary>Which of a person's connections an import uses, without touching a secret.</summary>
public sealed record TrackerConnectionRef(TrackerProvider Provider, Guid? PlatformConnectionId, Guid? GitConnectionId, long? GithubUserId);

/// <summary>A credential ready to use, and what it came from (to record a rejection). Never logged: its record output holds no secret beyond the credential's own redacted form.</summary>
public sealed record ResolvedTracker(TrackerProvider Provider, Guid UserId, TrackerCredential Credential)
{
    public Guid? PlatformConnectionId { get; init; }

    public Guid? GitConnectionId { get; init; }

    public long? GithubUserId { get; init; }

    public GitAccessToken? GitToken { get; init; }

    public GitHubUserSession? GitHubSession { get; init; }
}

/// <summary>
/// FR-165–FR-168: a person's tracker connections. Jira and Linear are platform connections, GitLab a <c>git_connection</c> on a registered
/// instance, GitHub the user's connection (PAT-mode projects can import only with one; the project's PAT is never used). Connections are
/// personal: another user's is <c>CREDENTIAL_NOT_OWNER</c>.
/// </summary>
public sealed class TrackerConnectionResolver(
    AideDbContext db,
    ISecretProtectors protectors,
    GitHubConnections github,
    IGitConnectionTokens gitTokens,
    GitHubTrackerAccess gitHubAccess,
    IIssueTrackers trackers,
    TimeProvider time)
{
    private ISecretProtector Protector => protectors.ForPurpose(SecretPurposes.TrackerToken);

    /// <summary>
    /// The caller's connection: the one asked for (<c>NOT_FOUND</c>, <c>CREDENTIAL_NOT_OWNER</c>), else their newest usable one, else
    /// <c>null</c> connection ids (<c>TRACKER_NOT_CONNECTED</c> when <paramref name="required"/>). No network, no secret.
    /// </summary>
    public async Task<Result<TrackerConnectionRef>> FindOwnAsync(Guid userId, TrackerProvider provider, Guid? connectionId, bool required, CancellationToken cancellationToken)
    {
        switch (provider)
        {
            case TrackerProvider.Jira or TrackerProvider.Linear:
                var kind = provider == TrackerProvider.Jira ? PlatformProvider.Jira : PlatformProvider.Linear;
                if (connectionId is { } platformId)
                {
                    var row = await db.PlatformConnections.AsNoTracking().Where(c => c.Id == platformId && c.Provider == kind).Select(c => new { c.UserId, c.Status }).FirstOrDefaultAsync(cancellationToken);
                    if (row is null)
                    {
                        return Fail(ErrorCodes.NotFound, "The connection was not found.");
                    }

                    if (row.UserId != userId)
                    {
                        return NotOwner();
                    }

                    return required && row.Status == PlatformConnectionStatus.Invalid
                        ? NotConnected(provider)
                        : Result.Success(new TrackerConnectionRef(provider, platformId, null, null));
                }

                var newest = await db.PlatformConnections.AsNoTracking()
                    .Where(c => c.UserId == userId && c.Provider == kind && c.Status != PlatformConnectionStatus.Invalid)
                    .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                return newest is null && required ? NotConnected(provider) : Result.Success(new TrackerConnectionRef(provider, newest, null, null));

            case TrackerProvider.Gitlab:
                if (connectionId is { } gitId)
                {
                    var row = await db.GitConnections.AsNoTracking().Where(c => c.Id == gitId && c.Provider == GitHostKind.Gitlab).Select(c => new { c.UserId, c.Status }).FirstOrDefaultAsync(cancellationToken);
                    if (row is null)
                    {
                        return Fail(ErrorCodes.NotFound, "The connection was not found.");
                    }

                    if (row.UserId != userId)
                    {
                        return NotOwner();
                    }

                    return required && row.Status == GitConnectionStatus.Invalid
                        ? NotConnected(provider)
                        : Result.Success(new TrackerConnectionRef(provider, null, gitId, null));
                }

                var newestGit = await db.GitConnections.AsNoTracking()
                    .Where(c => c.UserId == userId && c.Provider == GitHostKind.Gitlab && c.Status != GitConnectionStatus.Invalid)
                    .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                return newestGit is null && required ? NotConnected(provider) : Result.Success(new TrackerConnectionRef(provider, null, newestGit, null));

            default:
                if (connectionId is { } other && other != userId)
                {
                    return NotOwner();
                }

                var account = await db.GitHubConnections.AsNoTracking()
                    .Where(c => c.UserId == userId && c.Status == GitHubConnectionStatus.Valid)
                    .Select(c => (long?)c.GithubUserId)
                    .FirstOrDefaultAsync(cancellationToken);
                return account is null && required ? NotConnected(provider) : Result.Success(new TrackerConnectionRef(provider, null, null, account));
        }
    }

    /// <summary>
    /// A usable credential of <paramref name="userId"/>: <c>TRACKER_NOT_CONNECTED</c> for none (or an unreadable secret, which also marks the
    /// connection invalid), <c>CONNECTION_SCOPE_INSUFFICIENT</c> for a Jira or Linear connection without read access.
    /// </summary>
    public async Task<Result<ResolvedTracker>> ResolveAsync(Guid userId, TrackerProvider provider, Guid? connectionId, CancellationToken cancellationToken)
    {
        var found = await FindOwnAsync(userId, provider, connectionId, required: true, cancellationToken);
        if (found.IsFailure)
        {
            return Result.Failure<ResolvedTracker>(found.Error!);
        }

        return provider switch
        {
            TrackerProvider.Jira or TrackerProvider.Linear => await PlatformAsync(userId, provider, found.Value.PlatformConnectionId!.Value, cancellationToken),
            TrackerProvider.Gitlab => await GitLabAsync(userId, found.Value.GitConnectionId!.Value, cancellationToken),
            _ => await GitHubAsync(userId, found.Value.GithubUserId, cancellationToken),
        };
    }

    /// <summary>
    /// FR-167: for GitHub, the credential that reads <paramref name="repository"/> (<c>owner/repo</c>): the installation's token; throws
    /// <see cref="TrackerApiException"/> (<c>AppPermissionMissing</c>, <c>NotConnected</c>…). Other trackers are unchanged.
    /// </summary>
    public async Task<ResolvedTracker> ForReadingAsync(ResolvedTracker resolved, string? repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (resolved.Provider != TrackerProvider.Github || resolved.GitHubSession is not { } session)
        {
            return resolved;
        }

        var parts = (repository ?? string.Empty).Split('/');
        if (parts.Length != 2)
        {
            throw new TrackerApiException(TrackerFailure.NotFound, null, "A GitHub repository looks like owner/repo.");
        }

        var token = await gitHubAccess.ReadTokenAsync(session.Token, session.App, parts[0], parts[1], cancellationToken);
        return resolved with { Credential = resolved.Credential with { Token = token } };
    }

    /// <summary>The tracker rejected the token: Jira and Linear connections become invalid (their owner is notified), GitLab's refreshes or becomes invalid, GitHub's becomes invalid. Saves.</summary>
    public async Task OnRejectedAsync(ResolvedTracker resolved, Guid? projectId, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (trackers.UsesFake && resolved.Provider is TrackerProvider.Github or TrackerProvider.Gitlab)
        {
            return;
        }

        if (resolved.PlatformConnectionId is { } platformId)
        {
            await PlatformConnectionService.MarkInvalidAsync(db, platformId, projectId, actor, time.GetUtcNow(), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (resolved.GitToken is { } gitToken)
        {
            await gitTokens.OnUnauthorizedAsync(gitToken, projectId, cancellationToken);
        }
        else if (resolved.GitHubSession is { } session)
        {
            await github.RejectedAsync(session.UserId, cancellationToken);
        }
    }

    private async Task<Result<ResolvedTracker>> PlatformAsync(Guid userId, TrackerProvider provider, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.FirstAsync(c => c.Id == connectionId, cancellationToken);
        if (connection.Status == PlatformConnectionStatus.InsufficientScope)
        {
            return Result.Failure<ResolvedTracker>(TrackerRules.ToError(provider, TrackerFailure.MissingPermissions));
        }

        if (!Protector.TryUnprotect(connection.SecretEncrypted, out var secret) || TrackerSecrets.Decode(connection.Provider, secret) is not { Token.Length: > 0 } decoded)
        {
            await PlatformConnectionService.MarkInvalidAsync(db, connectionId, null, SystemActor.Name, time.GetUtcNow(), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return NotConnectedTracker(provider);
        }

        return Result.Success(new ResolvedTracker(provider, userId, new TrackerCredential(decoded.Token)
        {
            Email = decoded.Email,
            Site = connection.Metadata.Site,
            CloudId = connection.Metadata.CloudId,
            AcceptanceCriteriaField = connection.Metadata.AcceptanceCriteriaField,
        })
        {
            PlatformConnectionId = connectionId,
        });
    }

    private async Task<Result<ResolvedTracker>> GitLabAsync(Guid userId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (trackers.UsesFake)
        {
            return Result.Success(new ResolvedTracker(TrackerProvider.Gitlab, userId, new TrackerCredential("fake")) { GitConnectionId = connectionId });
        }

        var token = await gitTokens.GetAccessTokenAsync(connectionId, cancellationToken);
        if (token.IsFailure)
        {
            return token.Error!.Code == ErrorCodes.GitNotConnected
                ? NotConnectedTracker(TrackerProvider.Gitlab)
                : Result.Failure<ResolvedTracker>(token.Error);
        }

        return Result.Success(new ResolvedTracker(TrackerProvider.Gitlab, userId, new TrackerCredential(token.Value.Token) { InstanceBaseUrl = token.Value.InstanceBaseUrl })
        {
            GitConnectionId = connectionId,
            GitToken = token.Value,
        });
    }

    private async Task<Result<ResolvedTracker>> GitHubAsync(Guid userId, long? githubUserId, CancellationToken cancellationToken)
    {
        if (trackers.UsesFake)
        {
            return Result.Success(new ResolvedTracker(TrackerProvider.Github, userId, new TrackerCredential("fake")) { GithubUserId = githubUserId });
        }

        var session = await github.SessionAsync(userId, cancellationToken);
        if (session.IsFailure)
        {
            return session.Error!.Code == ErrorCodes.GitHubNotConnected
                ? NotConnectedTracker(TrackerProvider.Github)
                : Result.Failure<ResolvedTracker>(session.Error);
        }

        return Result.Success(new ResolvedTracker(TrackerProvider.Github, userId, new TrackerCredential(session.Value.Token))
        {
            GithubUserId = session.Value.GithubUserId,
            GitHubSession = session.Value,
        });
    }

    private static Result<TrackerConnectionRef> Fail(string code, string message) => Result.Failure<TrackerConnectionRef>(new DomainError(code, message));

    private static Result<TrackerConnectionRef> NotOwner() =>
        Fail(ErrorCodes.CredentialNotOwner, "That connection belongs to someone else. Use one of your own.");

    private static Result<TrackerConnectionRef> NotConnected(TrackerProvider provider) =>
        Result.Failure<TrackerConnectionRef>(TrackerRules.ToError(provider, TrackerFailure.NotConnected));

    private static Result<ResolvedTracker> NotConnectedTracker(TrackerProvider provider) =>
        Result.Failure<ResolvedTracker>(TrackerRules.ToError(provider, TrackerFailure.NotConnected));
}
