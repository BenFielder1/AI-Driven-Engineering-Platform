using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aide.Api.Features.GitLab;

/// <summary>
/// FR-131: the caller's GitLab connections (<c>git_connections</c>, provider <c>gitlab</c>): stored from OAuth (sign-in or
/// connect) or from a pasted access token, with tokens encrypted (<see cref="SecretPurposes.GitHostUserToken"/>), never
/// returned or logged. Connections are personal: another user's id is <c>NOT_FOUND</c>.
/// </summary>
public sealed partial class GitLabConnectionService(
    AideDbContext db,
    IGitLabInstances instances,
    IGitLabClients clients,
    IGitConnectionTokens tokens,
    ISecretProtectors protectors,
    TimeProvider time,
    ILogger<GitLabConnectionService> logger)
{
    private ISecretProtector Protector => protectors.ForPurpose(SecretPurposes.GitHostUserToken);

    public async Task<IReadOnlyList<GitLabConnectionDto>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var all = (await instances.ListAsync(cancellationToken)).ToDictionary(i => i.Id);
        var rows = await db.GitConnections.AsNoTracking()
            .Where(c => c.UserId == userId && c.Provider == GitHostKind.Gitlab)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        return [.. rows.Where(r => all.ContainsKey(r.InstanceId)).Select(r => ToDto(r, all[r.InstanceId], now))];
    }

    /// <summary>
    /// FR-131 token fallback: validates with <c>GET /user</c> (rejected → <c>CREDENTIAL_INVALID</c>) and
    /// <c>GET /personal_access_tokens/self</c> (missing <c>api</c>/<c>write_repository</c> → <c>CONNECTION_SCOPE_INSUFFICIENT</c>
    /// listing them; expired or revoked → <c>CREDENTIAL_INVALID</c>), then creates or replaces the caller's connection for that
    /// GitLab account on the instance. Nothing is saved on failure.
    /// </summary>
    public async Task<Result<GitLabConnectionDto>> CreateFromTokenAsync(Guid userId, Guid? instanceId, string token, CancellationToken cancellationToken)
    {
        var valid = GitLabRules.ValidateToken(token);
        if (valid.IsFailure)
        {
            return Result.Failure<GitLabConnectionDto>(valid.Error!);
        }

        var instance = await UsableInstanceAsync(instanceId, cancellationToken);
        if (instance is null)
        {
            return Result.Failure<GitLabConnectionDto>(new DomainError(ErrorCodes.NotFound, "The GitLab instance was not found or is disabled."));
        }

        var checkedToken = await ValidateTokenAsync(instance, token, cancellationToken);
        if (checkedToken.IsFailure)
        {
            return Result.Failure<GitLabConnectionDto>(checkedToken.Error!);
        }

        var (user, info) = checkedToken.Value;
        var now = time.GetUtcNow();
        var row = await db.GitConnections.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == GitHostKind.Gitlab && c.InstanceId == instance.Id && c.ExternalUserId == user.ExternalId,
            cancellationToken);
        if (row is null)
        {
            row = new GitConnection
            {
                UserId = userId,
                Provider = GitHostKind.Gitlab,
                InstanceId = instance.Id,
                ExternalUserId = user.ExternalId,
                CreatedAt = now,
            };
            db.GitConnections.Add(row);
        }

        row.AuthKind = GitConnectionAuthKind.Token;
        row.Login = user.Username;
        row.AccessTokenEncrypted = Protector.Protect(token);
        row.AccessExpiresAt = null;
        row.RefreshTokenEncrypted = null;
        row.Scopes = info.Scopes;
        row.ExpiresAt = info.ExpiresAt;
        row.Status = GitConnectionStatus.Valid;
        row.LastValidatedAt = now;
        row.UpdatedAt = now;
        var saved = await SaveAsync(cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<GitLabConnectionDto>(saved.Error!);
        }

        LogTokenSaved(logger, userId, row.Id);
        return Result.Success(ToDto(row, instance, now));
    }

    /// <summary>
    /// FR-131: stores the OAuth tokens of a sign-in or connect as the user's connection for that GitLab account (created or
    /// replaced; status <c>valid</c>).
    /// </summary>
    public async Task<Result<GitConnection>> SaveOAuthAsync(Guid userId, GitLabInstance instance, GitLabUser user, GitLabOAuthTokens oauth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(oauth);
        var now = time.GetUtcNow();
        var row = await db.GitConnections.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == GitHostKind.Gitlab && c.InstanceId == instance.Id && c.ExternalUserId == user.ExternalId,
            cancellationToken);
        if (row is null)
        {
            row = new GitConnection
            {
                UserId = userId,
                Provider = GitHostKind.Gitlab,
                InstanceId = instance.Id,
                ExternalUserId = user.ExternalId,
                CreatedAt = now,
            };
            db.GitConnections.Add(row);
        }

        row.AuthKind = GitConnectionAuthKind.Oauth;
        row.Login = user.Username;
        row.AccessTokenEncrypted = Protector.Protect(oauth.AccessToken);
        row.AccessExpiresAt = GitTokenRules.AccessExpiresAt(now, oauth.ExpiresIn);
        row.RefreshTokenEncrypted = oauth.RefreshToken is { } refresh ? Protector.Protect(refresh) : null;
        row.Scopes = oauth.Scopes.Count > 0 ? oauth.Scopes : GitTokenRules.GitLabOauthScopes;
        row.ExpiresAt = null;
        row.Status = GitConnectionStatus.Valid;
        row.LastValidatedAt = now;
        row.UpdatedAt = now;
        var saved = await SaveAsync(cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<GitConnection>(saved.Error!);
        }

        LogOAuthSaved(logger, userId, row.Id);
        return Result.Success(row);
    }

    /// <summary>Removes one of the caller's connections; projects bound to it lose the binding (FR-136).</summary>
    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken, string? actor = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.GitConnections.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId && c.Provider == GitHostKind.Gitlab, cancellationToken);
        if (row is null)
        {
            return false;
        }

        // FR-136: projects bound to it lose the binding (and their Owners are notified, git_binding_cleared).
        await GitBindings.ClearForConnectionAsync(db, id, GitBindingClearedReason.ConnectionDeleted, actor ?? SystemActor.Name, time.GetUtcNow(), cancellationToken);
        db.GitConnections.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogDeleted(logger, userId, id);
        return true;
    }

    /// <summary>
    /// Checks one of the caller's connections with GitLab again (refreshing an OAuth token first when due) and records the
    /// result: <c>valid</c>, <c>invalid</c> (rejected) or <c>insufficient_scope</c> (a token that lost a scope).
    /// <c>UPSTREAM_ERROR</c> when GitLab can't be reached and <c>GIT_HOST_NOT_ALLOWED</c> when the SSRF guard refuses the
    /// instance's address (status unchanged in both).
    /// </summary>
    public async Task<Result<GitLabConnectionDto>> TestAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var row = await db.GitConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId && c.Provider == GitHostKind.Gitlab, cancellationToken);
        if (row is null)
        {
            return Result.Failure<GitLabConnectionDto>(new DomainError(ErrorCodes.NotFound, "The GitLab connection was not found."));
        }

        var instance = await instances.GetAsync(row.InstanceId, cancellationToken);
        if (instance is null)
        {
            return Result.Failure<GitLabConnectionDto>(new DomainError(ErrorCodes.NotFound, "The GitLab connection was not found."));
        }

        var now = time.GetUtcNow();
        if (row.Status != GitConnectionStatus.Valid || !instance.Enabled)
        {
            // An invalid connection stays invalid until it is reconnected; a disabled instance isn't contacted.
            return Result.Success(ToDto(row, instance, now));
        }

        GitConnectionStatus status;
        IReadOnlyList<string>? scopes = null;
        DateTimeOffset? expiresAt = row.ExpiresAt;
        var outcome = await CallAsync(userId, id, async (client, token) =>
        {
            await client.GetUserAsync(token, cancellationToken);
            return row.AuthKind == GitConnectionAuthKind.Token ? await client.GetTokenSelfAsync(token, cancellationToken) : null;
        }, cancellationToken);
        if (outcome.IsFailure)
        {
            if (outcome.Error!.Code != ErrorCodes.GitNotConnected)
            {
                // UPSTREAM_ERROR, GIT_HOST_NOT_ALLOWED (SSRF guard), rate limits…: the status is left as it is.
                return Result.Failure<GitLabConnectionDto>(outcome.Error);
            }

            // Only GitLab rejecting the token (a 401, or invalid_grant on refresh) invalidates a connection, and the token
            // service has already recorded that (with its event and notification): show the connection as it is stored now.
            var current = await db.GitConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            return current is null
                ? Result.Failure<GitLabConnectionDto>(new DomainError(ErrorCodes.NotFound, "The GitLab connection was not found."))
                : Result.Success(ToDto(current, instance, now));
        }

        if (outcome.Value is { } info)
        {
            scopes = info.Scopes;
            expiresAt = info.ExpiresAt;
            status = !info.Active || info.Revoked || GitTokenRules.IsExpired(info.ExpiresAt, now)
                ? GitConnectionStatus.Invalid
                : GitTokenRules.MissingGitLabScopes(info.Scopes).Count > 0 ? GitConnectionStatus.InsufficientScope : GitConnectionStatus.Valid;
        }
        else
        {
            status = GitConnectionStatus.Valid;
        }

        var tracked = await db.GitConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (tracked is null)
        {
            return Result.Failure<GitLabConnectionDto>(new DomainError(ErrorCodes.NotFound, "The GitLab connection was not found."));
        }

        if (GitBindingRules.StatusChangeClearsBinding(tracked.Status, status))
        {
            // FR-136: an expired or revoked token, or one that lost a scope (insufficient_scope counts as an invalidation),
            // can't run Git work: projects bound to it lose the binding and their Owners are notified.
            await GitBindings.ClearForConnectionAsync(db, id, GitBindingClearedReason.ConnectionInvalid, SystemActor.Name, now, cancellationToken);
        }

        tracked.Status = status;
        tracked.Scopes = scopes ?? tracked.Scopes;
        tracked.ExpiresAt = expiresAt;
        tracked.LastValidatedAt = now;
        tracked.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Refreshed or reconnected meanwhile: show it as it is now.
            db.ChangeTracker.Clear();
            tracked = await db.GitConnections.AsNoTracking().FirstAsync(c => c.Id == id, cancellationToken);
        }

        return Result.Success(ToDto(tracked, instance, now));
    }

    /// <summary>
    /// Runs <paramref name="call"/> with a valid token of the caller's connection <paramref name="connectionId"/>, bound to its
    /// instance. A 401 refreshes an OAuth token once and retries; a second 401 (or a token's 401) marks the connection
    /// invalid (<c>GIT_NOT_CONNECTED</c>). Other failures: <c>NOT_FOUND</c> (not the caller's GitLab connection),
    /// <c>FORBIDDEN</c> (403), <c>UPSTREAM_RATE_LIMITED</c> (429, with <see cref="DomainError.RetryAfter"/>),
    /// <c>GIT_HOST_NOT_ALLOWED</c> (SSRF guard), <c>UPSTREAM_ERROR</c> (anything else).
    /// </summary>
    public async Task<Result<T>> CallAsync<T>(Guid userId, Guid connectionId, Func<GitLabClient, string, Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var owned = await db.GitConnections.AsNoTracking()
            .AnyAsync(c => c.Id == connectionId && c.UserId == userId && c.Provider == GitHostKind.Gitlab, cancellationToken);
        if (!owned)
        {
            return Result.Failure<T>(new DomainError(ErrorCodes.NotFound, "The GitLab connection was not found."));
        }

        var access = await tokens.GetAccessTokenAsync(connectionId, cancellationToken);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (access.IsFailure)
            {
                return Result.Failure<T>(access.Error!);
            }

            var token = access.Value;
            try
            {
                return Result.Success(await call(clients.ForInstance(token.InstanceBaseUrl), token.Token));
            }
            catch (GitLabApiException ex) when (ex.IsUnauthorized)
            {
                access = await tokens.OnUnauthorizedAsync(token, null, cancellationToken);
            }
            catch (GitLabApiException ex)
            {
                return Result.Failure<T>(UpstreamError(ex));
            }
            catch (GitHostBlockedException ex)
            {
                return Result.Failure<T>(new DomainError(ErrorCodes.GitHostNotAllowed, "That address isn't allowed for a GitLab instance.") { Details = [ex.Message] });
            }
        }

        return Result.Failure<T>(new DomainError(ErrorCodes.GitNotConnected, "GitLab rejected the connection. Connect GitLab again under Account → Connections → GitLab."));
    }

    /// <summary>The AIDE error for a GitLab failure other than 401.</summary>
    public static DomainError UpstreamError(GitLabApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Status switch
        {
            429 => new DomainError(ErrorCodes.UpstreamRateLimited, "GitLab is rate limiting requests. Try again in a few minutes.") { RetryAfter = exception.RetryAfter },
            403 => new DomainError(ErrorCodes.Forbidden, "GitLab refused: your GitLab account doesn't have permission to do that."),
            404 => new DomainError(ErrorCodes.NotFound, "GitLab couldn't find that, or your account can't see it."),
            _ => new DomainError(ErrorCodes.UpstreamError, exception.Status is null ? "GitLab couldn't be reached. Try again." : "GitLab returned an error. Try again later."),
        };
    }

    /// <summary>An enabled instance by id (<c>null</c> id = gitlab.com).</summary>
    public async Task<GitLabInstance?> UsableInstanceAsync(Guid? instanceId, CancellationToken cancellationToken)
    {
        var all = await instances.ListAsync(cancellationToken);
        var instance = instanceId is { } id ? all.FirstOrDefault(i => i.Id == id) : all.FirstOrDefault(i => i.IsGitLabCom);
        return instance is { Enabled: true } ? instance : null;
    }

    private async Task<Result<(GitLabUser User, GitLabTokenInfo Info)>> ValidateTokenAsync(GitLabInstance instance, string token, CancellationToken cancellationToken)
    {
        var client = clients.ForInstance(instance.BaseUrl);
        try
        {
            var user = await client.GetUserAsync(token, cancellationToken);
            var info = await client.GetTokenSelfAsync(token, cancellationToken);
            if (!info.Active || info.Revoked || GitTokenRules.IsExpired(info.ExpiresAt, time.GetUtcNow()))
            {
                return Result.Failure<(GitLabUser, GitLabTokenInfo)>(new DomainError(ErrorCodes.CredentialInvalid, "GitLab says this token is expired or revoked."));
            }

            var scopes = GitLabRules.CheckTokenScopes(info.Scopes);
            return scopes.IsFailure
                ? Result.Failure<(GitLabUser, GitLabTokenInfo)>(scopes.Error!)
                : Result.Success((user, info));
        }
        catch (GitLabApiException ex) when (ex.Status is 401)
        {
            return Result.Failure<(GitLabUser, GitLabTokenInfo)>(new DomainError(ErrorCodes.CredentialInvalid, "GitLab rejected this token."));
        }
        catch (GitLabApiException ex) when (ex.Status is 403)
        {
            // A token without read access to /user or its own details lacks `api` (read_user/read_api tokens can't do Git work).
            return Result.Failure<(GitLabUser, GitLabTokenInfo)>(GitLabRules.CheckTokenScopes([]).Error!);
        }
        catch (GitLabApiException ex)
        {
            return Result.Failure<(GitLabUser, GitLabTokenInfo)>(UpstreamError(ex));
        }
        catch (GitHostBlockedException ex)
        {
            return Result.Failure<(GitLabUser, GitLabTokenInfo)>(new DomainError(ErrorCodes.GitHostNotAllowed, "That address isn't allowed for a GitLab instance.") { Details = [ex.Message] });
        }
    }

    private async Task<Result> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            // The connection was refreshed, tested or removed by another request since it was read (xmin).
            db.ChangeTracker.Clear();
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "This GitLab connection was changed by another request at the same time. Try again."));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "This GitLab account was connected by another request at the same time. Try again."));
        }
    }

    private static GitLabConnectionDto ToDto(GitConnection row, GitLabInstance instance, DateTimeOffset now) => new(
        row.Id,
        row.InstanceId,
        instance.Name,
        instance.BaseUrl.AbsoluteUri.TrimEnd('/'),
        instance.Enabled,
        row.Login,
        row.ExternalUserId,
        row.AuthKind,
        row.Scopes,
        row.ExpiresAt,
        GitTokenRules.ShouldWarnExpiry(row.ExpiresAt, now),
        row.Status,
        row.LastValidatedAt,
        row.CreatedAt,
        row.UpdatedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitLab token connection {ConnectionId} saved for user {UserId}")]
    private static partial void LogTokenSaved(ILogger logger, Guid userId, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitLab OAuth connection {ConnectionId} saved for user {UserId}")]
    private static partial void LogOAuthSaved(ILogger logger, Guid userId, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitLab connection {ConnectionId} of user {UserId} deleted")]
    private static partial void LogDeleted(ILogger logger, Guid userId, Guid connectionId);
}
