using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Domain.Jobs;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aide.Api.Features.GitHub;

/// <summary>A usable user-to-server token for the picker, repo creation and identity (§4.14), with the App it belongs to.</summary>
public sealed record GitHubUserSession(Guid UserId, string Token, long GithubUserId, string Login, GitHubAppCredentials App)
{
    /// <summary>Redacted: the token never reaches a log.</summary>
    public override string ToString() => $"GitHubUserSession {{ UserId = {UserId}, Login = {Login} }}";
}

/// <summary>
/// FR-81: users' GitHub connections (<c>github_connections</c>): stored on sign-in or connect with both tokens encrypted
/// (<see cref="SecretPurposes.GitHubUserToken"/>), refreshed before the access token's 8-hour expiry with the refresh token,
/// and marked <c>invalid</c> (with a <c>CONNECTION_INVALID</c> event; the notification type comes with FR-92) when a refresh
/// fails or GitHub rejects the token. Tokens are never returned or logged.
/// </summary>
public sealed partial class GitHubConnections(
    AideDbContext db,
    ISecretProtectors protectors,
    IGitHubAppStore apps,
    GitHubApi api,
    TimeProvider time,
    ILogger<GitHubConnections> logger)
{
    private ISecretProtector Protector => protectors.ForPurpose(SecretPurposes.GitHubUserToken);

    /// <summary>
    /// Creates or replaces the user's connection (sign-in through the App, or connect). Returns <c>false</c> when the GitHub
    /// account is already connected to another AIDE user (unique <c>github_user_id</c>).
    /// </summary>
    public async Task<bool> SaveAsync(Guid userId, long githubUserId, string login, GitHubUserTokens tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (await db.GitHubConnections.AnyAsync(c => c.GithubUserId == githubUserId && c.UserId != userId, cancellationToken))
        {
            return false;
        }

        var now = time.GetUtcNow();
        var connection = await db.GitHubConnections.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        if (connection is null)
        {
            connection = new GitHubConnection { UserId = userId };
            db.GitHubConnections.Add(connection);
        }

        connection.GithubUserId = githubUserId;
        connection.Login = login;
        connection.AccessTokenEncrypted = Protector.Protect(tokens.AccessToken);
        connection.AccessExpiresAt = tokens.AccessExpiresAt;
        connection.RefreshTokenEncrypted = tokens.RefreshToken is { } refresh ? Protector.Protect(refresh) : null;
        connection.RefreshExpiresAt = tokens.RefreshExpiresAt;
        connection.Status = GitHubConnectionStatus.Valid;
        connection.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }

        LogSaved(logger, userId);
        return true;
    }

    /// <summary>The caller's connection row, or <c>null</c>.</summary>
    public Task<GitHubConnection?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        db.GitHubConnections.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);

    /// <summary>FR-81: removes the user's connection (both tokens). Sign-in with GitHub keeps working and reconnects.</summary>
    public async Task DisconnectAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await db.GitHubConnections.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken) > 0)
        {
            LogDisconnected(logger, userId);
        }
    }

    /// <summary>
    /// A valid user token for <paramref name="userId"/>, refreshed first when it expires within 5 minutes.
    /// <c>GITHUB_NOT_CONNECTED</c> without an App, a connection, or after a failed refresh (then the connection is
    /// <c>invalid</c>); <c>UPSTREAM_ERROR</c> when GitHub can't be reached to refresh.
    /// </summary>
    public async Task<Result<GitHubUserSession>> SessionAsync(Guid userId, CancellationToken cancellationToken)
    {
        var app = await apps.GetAsync(cancellationToken);
        if (app is null)
        {
            return NotConnected("No AIDE GitHub App is registered. An Admin registers it under Admin → Integrations → GitHub.");
        }

        var connection = await db.GitHubConnections.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        if (connection is null || connection.Status != GitHubConnectionStatus.Valid)
        {
            return NotConnected(connection is null
                ? "Connect GitHub under Account → Connections → GitHub first."
                : "Your GitHub connection is no longer valid. Connect GitHub again under Account → Connections → GitHub.");
        }

        var now = time.GetUtcNow();
        if (GitHubRules.NeedsRefresh(connection.AccessExpiresAt, now))
        {
            var refreshed = await RefreshAsync(userId, app, cancellationToken);
            if (refreshed.IsFailure)
            {
                return Result.Failure<GitHubUserSession>(refreshed.Error!);
            }

            connection = refreshed.Value;
        }

        if (!Protector.TryUnprotect(connection.AccessTokenEncrypted, out var token))
        {
            await InvalidateAsync(await TrackedAsync(userId, cancellationToken) ?? connection, "unreadable", cancellationToken);
            return NotConnected("Your GitHub connection can't be read any more. Connect GitHub again.");
        }

        return Result.Success(new GitHubUserSession(userId, token, connection.GithubUserId, connection.Login, app));
    }

    /// <summary>GitHub rejected the user's token (401): the connection is marked <c>invalid</c>.</summary>
    public async Task<DomainError> RejectedAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await TrackedAsync(userId, cancellationToken) is { } connection)
        {
            await InvalidateAsync(connection, "rejected", cancellationToken);
        }

        return NotConnected("GitHub rejected your connection. Connect GitHub again under Account → Connections → GitHub.").Error!;
    }

    /// <summary>
    /// FR-81: refreshes the user's token. GitHub refresh tokens are single-use, so refreshes are serialised per user: the
    /// connection row is locked (<c>FOR UPDATE</c>) for the refresh, and the expiry is checked again under the lock — a
    /// request that waited uses the tokens the first one saved instead of spending the old refresh token again (which
    /// GitHub would refuse, wrongly invalidating the connection).
    /// </summary>
    private async Task<Result<GitHubConnection>> RefreshAsync(Guid userId, GitHubAppCredentials app, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var connection = await LockAsync(userId, cancellationToken);
        if (connection is not { Status: GitHubConnectionStatus.Valid })
        {
            return Result.Failure<GitHubConnection>(NotConnected("Your GitHub connection is no longer valid. Connect GitHub again under Account → Connections → GitHub.").Error!);
        }

        var now = time.GetUtcNow();
        if (!GitHubRules.NeedsRefresh(connection.AccessExpiresAt, now))
        {
            // Refreshed by a concurrent request while this one waited for the lock.
            await CommitAsync(transaction, cancellationToken);
            return Result.Success(connection);
        }

        if (connection.RefreshTokenEncrypted is not { } encrypted
            || connection.RefreshExpiresAt is { } refreshExpires && refreshExpires <= now
            || !Protector.TryUnprotect(encrypted, out var refreshToken))
        {
            await InvalidateAsync(connection, "refresh_expired", cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return Result.Failure<GitHubConnection>(NotConnected("Your GitHub connection expired. Connect GitHub again under Account → Connections → GitHub.").Error!);
        }

        GitHubUserTokens? tokens;
        try
        {
            tokens = await api.RefreshUserTokenAsync(app.ClientId, app.ClientSecret, refreshToken, cancellationToken);
        }
        catch (GitHubApiException)
        {
            return Result.Failure<GitHubConnection>(new DomainError(ErrorCodes.UpstreamError, "GitHub couldn't be reached to refresh your connection. Try again."));
        }

        if (tokens is null)
        {
            await InvalidateAsync(connection, "refresh_failed", cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return Result.Failure<GitHubConnection>(NotConnected("GitHub refused to refresh your connection. Connect GitHub again under Account → Connections → GitHub.").Error!);
        }

        connection.AccessTokenEncrypted = Protector.Protect(tokens.AccessToken);
        connection.AccessExpiresAt = tokens.AccessExpiresAt;
        if (tokens.RefreshToken is { } refresh)
        {
            connection.RefreshTokenEncrypted = Protector.Protect(refresh);
            connection.RefreshExpiresAt = tokens.RefreshExpiresAt;
        }

        connection.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        LogRefreshed(logger, connection.UserId);
        return Result.Success(connection);
    }

    /// <summary>The user's connection row, locked for this transaction and tracked with its current values.</summary>
    private async Task<GitHubConnection?> LockAsync(Guid userId, CancellationToken cancellationToken)
    {
        Detach(userId);
        return await db.GitHubConnections
            .FromSql($"SELECT *, xmin FROM github_connections WHERE user_id = {userId} FOR UPDATE")
            .AsTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The user's connection, tracked with its current values (and <c>xmin</c>).</summary>
    private async Task<GitHubConnection?> TrackedAsync(Guid userId, CancellationToken cancellationToken)
    {
        Detach(userId);
        return await db.GitHubConnections.AsTracking().FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
    }

    /// <summary>Forgets a tracked copy, so the next read sees the row as it is now (identity resolution would keep the old values).</summary>
    private void Detach(Guid userId)
    {
        foreach (var entry in db.ChangeTracker.Entries<GitHubConnection>().Where(e => e.Entity.UserId == userId).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static async Task CommitAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    /// <summary>§4.14: marks the connection invalid and records <c>CONNECTION_INVALID</c> (no secret).</summary>
    private async Task InvalidateAsync(GitHubConnection connection, string reason, CancellationToken cancellationToken)
    {
        if (connection.Status == GitHubConnectionStatus.Invalid)
        {
            return;
        }

        var now = time.GetUtcNow();
        connection.Status = GitHubConnectionStatus.Invalid;
        connection.UpdatedAt = now;
        db.AddEvent(
            EventTypes.ConnectionInvalid,
            null,
            new { userId = connection.UserId, provider = "github", login = connection.Login, reason },
            SystemActor.Name,
            now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Changed since it was read (refreshed or reconnected): leave it as the other request saved it, without the event.
            Detach(connection.UserId);
            foreach (var entry in db.ChangeTracker.Entries<AuditEvent>().Where(e => e.State == EntityState.Added && e.Entity.Type == EventTypes.ConnectionInvalid).ToList())
            {
                entry.State = EntityState.Detached;
            }

            // ...and the owner's connection_invalid notification job the outbox queued with it (FR-92).
            foreach (var entry in db.ChangeTracker.Entries<Job>().Where(e => e.State == EntityState.Added && e.Entity.Type == JobTypes.Notification).ToList())
            {
                entry.State = EntityState.Detached;
            }

            return;
        }

        LogInvalid(logger, connection.UserId, reason);
    }

    private static Result<GitHubUserSession> NotConnected(string message) =>
        Result.Failure<GitHubUserSession>(new DomainError(ErrorCodes.GitHubNotConnected, message));

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub connection saved for user {UserId}")]
    private static partial void LogSaved(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub connection removed for user {UserId}")]
    private static partial void LogDisconnected(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub user token refreshed for user {UserId}")]
    private static partial void LogRefreshed(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub connection of user {UserId} marked invalid ({Reason})")]
    private static partial void LogInvalid(ILogger logger, Guid userId, string reason);
}
