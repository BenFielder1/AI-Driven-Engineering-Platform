using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.GitLab;

/// <summary>
/// A usable access token of a <see cref="GitConnection"/> (FR-131, FR-136), bound to its instance: send it only to
/// <see cref="InstanceBaseUrl"/> (<c>IGitLabClients.ForInstance(InstanceBaseUrl)</c>). For Git over HTTPS use the Basic
/// <c>http.extraHeader</c> with <see cref="BasicUser"/> and the token; never put it in a URL, on disk, in a log or a prompt.
/// <see cref="RowVersion"/> identifies the stored pair, for <see cref="IGitConnectionTokens.OnUnauthorizedAsync"/>.
/// </summary>
public sealed record GitAccessToken(
    Guid ConnectionId,
    Guid UserId,
    GitHostKind Provider,
    Guid InstanceId,
    Uri InstanceBaseUrl,
    GitConnectionAuthKind AuthKind,
    string Login,
    string Token,
    uint RowVersion,
    bool Refreshed)
{
    public GitCredentialKind CredentialKind => Provider switch
    {
        GitHostKind.Gitlab => GitCredentialKind.Gitlab,
        _ => AuthKind == GitConnectionAuthKind.Oauth ? GitCredentialKind.BitbucketOauth : GitCredentialKind.BitbucketApiToken,
    };

    /// <summary>FR-136: the Basic user name (<c>oauth2</c> for GitLab).</summary>
    public string BasicUser => GitTokenRules.GitBasicUser(CredentialKind);

    /// <summary>Redacted: the token never reaches a log.</summary>
    public override string ToString() => $"GitAccessToken {{ ConnectionId = {ConnectionId}, Provider = {Provider}, Login = {Login}, Refreshed = {Refreshed} }}";
}

/// <summary>
/// FR-131, FR-136: valid access tokens for GitLab (and later Bitbucket) connections, for the repo picker and for background
/// Git work (push, MR, merge, release, import, polling), which uses the project's binding Owner's connection.
/// <list type="bullet">
/// <item><see cref="GetAccessTokenAsync"/>: the stored token, refreshed first when it expires within 5 minutes
/// (<see cref="GitTokenRules.NeedsRefresh"/>). Refreshes are serialised per connection (<c>FOR UPDATE</c>) and the rotated pair
/// is saved at once in its own transaction (GitLab invalidates the old pair), concurrency-checked with <c>xmin</c>.</item>
/// <item><see cref="OnUnauthorizedAsync"/>: after the host answers 401: an OAuth connection that hasn't refreshed in this job
/// refreshes once; otherwise (or after that refresh) it is marked <c>invalid</c> and its owner notified
/// (<c>CONNECTION_INVALID</c> → <c>connection_invalid</c>).</item>
/// </list>
/// Failures: <c>GIT_NOT_CONNECTED</c> (missing, invalid or unreadable connection; disabled instance; refresh refused),
/// <c>UPSTREAM_ERROR</c> (the token endpoint couldn't be reached or failed transiently: retry; the connection is unchanged) and
/// <c>GIT_HOST_NOT_ALLOWED</c> (the SSRF guard refused the instance's address; the connection is unchanged).
/// Once a refresh request is sent, the caller's cancellation no longer applies: the rotated pair is always saved.
/// </summary>
public interface IGitConnectionTokens
{
    Task<Result<GitAccessToken>> GetAccessTokenAsync(Guid connectionId, CancellationToken cancellationToken);

    /// <param name="rejected">The token the host rejected (its <see cref="GitAccessToken.Refreshed"/> says whether this job
    /// already refreshed).</param>
    /// <param name="projectId">The project the call was for, recorded on the event (null outside a project).</param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    Task<Result<GitAccessToken>> OnUnauthorizedAsync(GitAccessToken rejected, Guid? projectId, CancellationToken cancellationToken);

    /// <summary>Marks the connection <c>invalid</c> and notifies its owner (no-op when it already is).</summary>
    Task MarkInvalidAsync(Guid connectionId, string reason, Guid? projectId, CancellationToken cancellationToken);
}

internal sealed partial class GitConnectionTokens(
    IServiceScopeFactory scopes,
    IGitLabClients clients,
    ISecretProtectors protectors,
    TimeProvider time,
    ILogger<GitConnectionTokens> logger) : IGitConnectionTokens
{
    private ISecretProtector Protector => protectors.ForPurpose(SecretPurposes.GitHostUserToken);

    public async Task<Result<GitAccessToken>> GetAccessTokenAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var instances = scope.ServiceProvider.GetRequiredService<IGitLabInstances>();
        var connection = await db.GitConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        var usable = await UsableAsync(instances, connection, cancellationToken);
        if (usable.IsFailure)
        {
            return Result.Failure<GitAccessToken>(usable.Error!);
        }

        if (GitTokenRules.NeedsRefresh(connection!.AccessExpiresAt, time.GetUtcNow()))
        {
            return await RefreshAsync(connectionId, expectedVersion: null, projectId: null, cancellationToken);
        }

        return await ToTokenAsync(db, connection, usable.Value, refreshed: false, cancellationToken);
    }

    public async Task<Result<GitAccessToken>> OnUnauthorizedAsync(GitAccessToken rejected, Guid? projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        var canRefresh = rejected.AuthKind == GitConnectionAuthKind.Oauth;
        if (GitTokenRules.OnUnauthorized(canRefresh, rejected.Refreshed) == GitTokenFailureAction.Refresh)
        {
            return await RefreshAsync(rejected.ConnectionId, rejected.RowVersion, projectId, cancellationToken);
        }

        await MarkInvalidAsync(rejected.ConnectionId, "rejected", projectId, cancellationToken);
        return NotConnected("GitLab rejected the connection. Connect GitLab again under Account → Connections → GitLab.");
    }

    public async Task MarkInvalidAsync(Guid connectionId, string reason, Guid? projectId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var connection = await db.GitConnections.FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null)
        {
            return;
        }

        await InvalidateAsync(db, connection, reason, projectId, cancellationToken);
    }

    /// <summary>
    /// Refreshes under a row lock. Without <paramref name="expectedVersion"/> (expiry) the expiry is checked again under the
    /// lock; with it (after a 401) the refresh happens only if the stored pair is still the rejected one — otherwise another
    /// request already rotated it, and the stored token is returned.
    /// </summary>
    private async Task<Result<GitAccessToken>> RefreshAsync(Guid connectionId, uint? expectedVersion, Guid? projectId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        var instances = scope.ServiceProvider.GetRequiredService<IGitLabInstances>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = await db.GitConnections
            .FromSql($"SELECT *, xmin FROM git_connections WHERE id = {connectionId} FOR UPDATE")
            .AsTracking()
            .FirstOrDefaultAsync(cancellationToken);
        var usable = await UsableAsync(instances, connection, cancellationToken);
        if (usable.IsFailure)
        {
            await transaction.CommitAsync(cancellationToken);
            return Result.Failure<GitAccessToken>(usable.Error!);
        }

        var now = time.GetUtcNow();
        var alreadyRotated = expectedVersion is { } expected ? connection!.RowVersion != expected : !GitTokenRules.NeedsRefresh(connection!.AccessExpiresAt, now);
        if (alreadyRotated)
        {
            await transaction.CommitAsync(cancellationToken);
            return await ToTokenAsync(db, connection, usable.Value, refreshed: true, cancellationToken);
        }

        var instance = usable.Value;
        if (connection.AuthKind != GitConnectionAuthKind.Oauth || connection.Provider != GitHostKind.Gitlab)
        {
            // Tokens can't be refreshed (Bitbucket OAuth refresh comes with FR-134).
            await transaction.CommitAsync(cancellationToken);
            return await ToTokenAsync(db, connection, instance, refreshed: true, cancellationToken);
        }

        var row = await db.GitHostInstances.AsNoTracking().FirstAsync(i => i.Id == connection.InstanceId, cancellationToken);
        var clientSecret = instances.GetClientSecret(row, instance);
        if (connection.RefreshTokenEncrypted is not { } encrypted || !Protector.TryUnprotect(encrypted, out var refreshToken)
            || instance.ClientId is not { } clientId || clientSecret is null)
        {
            await InvalidateAsync(db, connection, instance.OAuthConfigured ? "refresh_unavailable" : "oauth_not_configured", projectId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return NotConnected("Your GitLab connection can't be refreshed. Connect GitLab again under Account → Connections → GitLab.");
        }

        // Once the refresh request is sent GitLab may rotate the pair (invalidating the old one), so from here on the caller's
        // cancellation is ignored: the request (bounded by the client timeout), reading its answer, saving the rotated pair
        // and committing all run to completion. Otherwise a cancelled request would lose the only valid pair.
        cancellationToken.ThrowIfCancellationRequested();
        var uncancellable = CancellationToken.None;
        GitLabOAuthTokens tokens;
        try
        {
            tokens = await clients.ForInstance(instance.BaseUrl).RefreshAsync(clientId, clientSecret, refreshToken, instances.RedirectUri, uncancellable);
        }
        catch (GitLabApiException ex)
        {
            if (GitTokenRules.OnRefreshFailed(ex.Status, ex.OauthError) == GitTokenFailureAction.MarkInvalid)
            {
                await InvalidateAsync(db, connection, "refresh_failed", projectId, uncancellable);
                await transaction.CommitAsync(uncancellable);
                return NotConnected("GitLab refused to refresh your connection. Connect GitLab again under Account → Connections → GitLab.");
            }

            LogRefreshTransient(logger, connectionId, ex.Status);
            return Result.Failure<GitAccessToken>(new DomainError(ErrorCodes.UpstreamError, "GitLab couldn't be reached to refresh the connection. Try again."));
        }
        catch (GitHostBlockedException ex)
        {
            // The SSRF guard refused the instance's address: nothing was sent, and the connection itself is fine (FR-131).
            LogRefreshBlocked(logger, connectionId);
            return Result.Failure<GitAccessToken>(new DomainError(ErrorCodes.GitHostNotAllowed, "The GitLab instance's address isn't allowed on this server.") { Details = [ex.Message] });
        }

        connection.AccessTokenEncrypted = Protector.Protect(tokens.AccessToken);
        connection.AccessExpiresAt = GitTokenRules.AccessExpiresAt(now, tokens.ExpiresIn);
        if (tokens.RefreshToken is { } rotated)
        {
            connection.RefreshTokenEncrypted = Protector.Protect(rotated);
        }

        if (tokens.Scopes.Count > 0)
        {
            connection.Scopes = tokens.Scopes;
        }

        connection.LastValidatedAt = now;
        connection.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(uncancellable);
            await transaction.CommitAsync(uncancellable);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Can't happen under the row lock; if it does, the stored pair is whatever the other writer saved.
            LogRotationConflict(logger, connectionId);
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            var stored = await db.GitConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, uncancellable);
            return stored is null
                ? NotConnected("The GitLab connection no longer exists.")
                : await ToTokenAsync(db, stored, instance, refreshed: true, uncancellable);
        }

        LogRefreshed(logger, connectionId);
        return Result.Success(new GitAccessToken(
            connection.Id, connection.UserId, connection.Provider, connection.InstanceId, instance.BaseUrl, connection.AuthKind,
            connection.Login, tokens.AccessToken, connection.RowVersion, Refreshed: true));
    }

    private static async Task<Result<GitLabInstance>> UsableAsync(IGitLabInstances instances, GitConnection? connection, CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            return Result.Failure<GitLabInstance>(new DomainError(ErrorCodes.GitNotConnected, "The GitLab connection no longer exists. Connect GitLab under Account → Connections → GitLab."));
        }

        if (connection.Status != GitConnectionStatus.Valid)
        {
            return Result.Failure<GitLabInstance>(new DomainError(ErrorCodes.GitNotConnected, "The GitLab connection is no longer valid. Connect GitLab again under Account → Connections → GitLab."));
        }

        var instance = await instances.GetAsync(connection.InstanceId, cancellationToken);
        if (instance is null || !instance.Enabled)
        {
            return Result.Failure<GitLabInstance>(new DomainError(ErrorCodes.GitNotConnected, $"The GitLab instance {instance?.Name ?? string.Empty} is disabled.".Replace("  ", " ", StringComparison.Ordinal)));
        }

        return Result.Success(instance);
    }

    private async Task<Result<GitAccessToken>> ToTokenAsync(AideDbContext db, GitConnection connection, GitLabInstance instance, bool refreshed, CancellationToken cancellationToken)
    {
        if (!Protector.TryUnprotect(connection.AccessTokenEncrypted, out var token))
        {
            var tracked = await db.GitConnections.FirstOrDefaultAsync(c => c.Id == connection.Id, cancellationToken);
            if (tracked is not null)
            {
                await InvalidateAsync(db, tracked, "unreadable", null, cancellationToken);
            }

            return NotConnected("Your GitLab connection can't be read any more. Connect GitLab again.");
        }

        return Result.Success(new GitAccessToken(
            connection.Id, connection.UserId, connection.Provider, connection.InstanceId, instance.BaseUrl, connection.AuthKind,
            connection.Login, token, connection.RowVersion, refreshed));
    }

    /// <summary>Marks <paramref name="connection"/> (tracked by <paramref name="db"/>) invalid with a <c>CONNECTION_INVALID</c> event.</summary>
    private async Task InvalidateAsync(AideDbContext db, GitConnection connection, string reason, Guid? projectId, CancellationToken cancellationToken)
    {
        if (connection.Status == GitConnectionStatus.Invalid)
        {
            return;
        }

        var now = time.GetUtcNow();
        connection.Status = GitConnectionStatus.Invalid;
        connection.UpdatedAt = now;
        db.AddEvent(
            EventTypes.ConnectionInvalid,
            projectId,
            new { connectionId = connection.Id, userId = connection.UserId, provider = connection.Provider.ToWire(), login = connection.Login, reason },
            SystemActor.Name,
            now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Changed since it was read (refreshed or reconnected): leave it, without the event or its notification job.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is GitConnection or AuditEvent or Job).ToList())
            {
                entry.State = EntityState.Detached;
            }

            return;
        }

        LogInvalid(logger, connection.Id, reason);
    }

    private static Result<GitAccessToken> NotConnected(string message) =>
        Result.Failure<GitAccessToken>(new DomainError(ErrorCodes.GitNotConnected, message));

    [LoggerMessage(Level = LogLevel.Information, Message = "Git connection {ConnectionId} access token refreshed")]
    private static partial void LogRefreshed(ILogger logger, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing Git connection {ConnectionId} failed transiently (HTTP {Status})")]
    private static partial void LogRefreshTransient(ILogger logger, Guid connectionId, int? status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing Git connection {ConnectionId} was blocked: the instance's address isn't allowed")]
    private static partial void LogRefreshBlocked(ILogger logger, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Git connection {ConnectionId} changed during token rotation")]
    private static partial void LogRotationConflict(ILogger logger, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Git connection {ConnectionId} marked invalid ({Reason})")]
    private static partial void LogInvalid(ILogger logger, Guid connectionId, string reason);
}
