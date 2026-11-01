using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Bitbucket;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aide.Api.Features.Bitbucket;

/// <summary>One Bitbucket call with a connection: its client (counted against the connection's budget), auth and Git credential kind.</summary>
public sealed record BitbucketCall(BitbucketClient Client, BitbucketAuth Auth, GitCredentialKind Credential);

/// <summary>FR-134: an API token scope check: the verdict, and the scopes it confirmed (in the required order).</summary>
public sealed record BitbucketScopeCheck(BitbucketScopeVerdict Verdict, IReadOnlyList<string> Confirmed);

/// <summary>
/// FR-134: the caller's Bitbucket connections (<c>git_connections</c>, provider <c>bitbucket</c>, on the Bitbucket Cloud
/// row): stored from OAuth (sign-in or connect) or from an Atlassian API token with its account email, both encrypted
/// (<see cref="SecretPurposes.GitHostUserToken"/>, <see cref="SecretPurposes.GitHostTokenEmail"/>), never returned or logged.
/// Connections are personal: another user's id is <c>NOT_FOUND</c>.
/// </summary>
public sealed partial class BitbucketConnectionService(
    AideDbContext db,
    IBitbucketConsumer consumer,
    IBitbucketClients clients,
    IGitConnectionTokens tokens,
    BitbucketRateLimiter limiter,
    ISecretProtectors protectors,
    TimeProvider time,
    ILogger<BitbucketConnectionService> logger)
{
    private ISecretProtector Protector => protectors.ForPurpose(SecretPurposes.GitHostUserToken);

    private ISecretProtector EmailProtector => protectors.ForPurpose(SecretPurposes.GitHostTokenEmail);

    public async Task<IReadOnlyList<BitbucketConnectionDto>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.GitConnections.AsNoTracking()
            .Where(c => c.UserId == userId && c.Provider == GitHostKind.Bitbucket)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToDto)];
    }

    /// <summary>
    /// FR-134 token fallback: validates the API token with <c>GET /2.0/user</c> (Basic <c>email:token</c>; rejected →
    /// <c>CREDENTIAL_INVALID</c>), then checks every required scope (<see cref="CheckTokenScopesAsync"/>); the token is saved
    /// only when all nine are verified, otherwise <c>CONNECTION_SCOPE_INSUFFICIENT</c> lists the missing ones (<c>details</c>)
    /// and the ones AIDE couldn't confirm (<c>unverifiableScopes</c>). Then creates or replaces the caller's connection for
    /// that Bitbucket account. These calls take from the caller's own budget (§4.19, 900 an hour), so repeated saves are
    /// throttled (<c>UPSTREAM_RATE_LIMITED</c>). Nothing is saved on failure.
    /// </summary>
    public async Task<Result<BitbucketConnectionDto>> CreateFromTokenAsync(Guid userId, string token, string email, CancellationToken cancellationToken)
    {
        var valid = BitbucketRules.ValidateToken(token, email);
        if (valid.IsFailure)
        {
            return Result.Failure<BitbucketConnectionDto>(valid.Error!);
        }

        var auth = BitbucketAuth.ApiToken(email.Trim(), token);
        var client = clients.CreateForUser(userId);
        BitbucketUser user;
        IReadOnlyList<string> granted;
        try
        {
            user = await client.GetUserAsync(auth, cancellationToken);
            var scopes = await CheckTokenScopesAsync(client, auth, user, cancellationToken);
            var check = BitbucketRules.CheckTokenScopes(scopes.Verdict);
            if (check.IsFailure)
            {
                return Result.Failure<BitbucketConnectionDto>(check.Error!);
            }

            granted = scopes.Confirmed;
        }
        catch (BitbucketApiException ex) when (ex.Status is 401)
        {
            return Result.Failure<BitbucketConnectionDto>(new DomainError(ErrorCodes.CredentialInvalid, "Bitbucket rejected this token and email. Check both, and that it is an API token (app passwords aren't supported)."));
        }
        catch (BitbucketApiException ex) when (ex.Status is 403 && ex.ScopeError)
        {
            // GET /user needs read:user; a usable scope list from Bitbucket names everything else that is missing too.
            var verdict = BitbucketRules.ApiTokenScopes(ex.GrantedScopes) is { } listed
                ? BitbucketRules.EvaluateTokenScopes(listed, new Dictionary<string, BitbucketScopeProbe>())
                : new BitbucketScopeVerdict(["read:user:bitbucket"], []);
            return Result.Failure<BitbucketConnectionDto>(BitbucketRules.CheckTokenScopes(verdict).Error!);
        }
        catch (BitbucketApiException ex)
        {
            return Result.Failure<BitbucketConnectionDto>(UpstreamError(ex));
        }

        var instance = await consumer.GetAsync(cancellationToken);
        var now = time.GetUtcNow();
        var row = await FindOrAddAsync(userId, instance.InstanceId, user.Uuid, now, cancellationToken);
        row.AuthKind = GitConnectionAuthKind.Token;
        row.Login = user.Login;
        row.AccessTokenEncrypted = Protector.Protect(token);
        row.TokenEmailEncrypted = EmailProtector.Protect(email.Trim());
        row.AccessExpiresAt = null;
        row.RefreshTokenEncrypted = null;
        row.Scopes = granted;
        row.ExpiresAt = null;
        row.Status = GitConnectionStatus.Valid;
        row.LastValidatedAt = now;
        row.UpdatedAt = now;
        var saved = await SaveAsync(cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<BitbucketConnectionDto>(saved.Error!);
        }

        LogTokenSaved(logger, userId, row.Id);
        return Result.Success(ToDto(row));
    }

    /// <summary>
    /// FR-134: stores the OAuth tokens of a sign-in or connect as the user's connection for that Bitbucket account (created
    /// or replaced; status <c>valid</c>), with whatever refresh token Bitbucket sent.
    /// </summary>
    public async Task<Result<GitConnection>> SaveOAuthAsync(Guid userId, Guid instanceId, BitbucketUser user, BitbucketOAuthTokens oauth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(oauth);
        var now = time.GetUtcNow();
        var row = await FindOrAddAsync(userId, instanceId, user.Uuid, now, cancellationToken);
        row.AuthKind = GitConnectionAuthKind.Oauth;
        row.Login = user.Login;
        row.AccessTokenEncrypted = Protector.Protect(oauth.AccessToken);
        row.AccessExpiresAt = GitTokenRules.AccessExpiresAt(now, oauth.ExpiresIn);
        row.RefreshTokenEncrypted = oauth.RefreshToken is { } refresh ? Protector.Protect(refresh) : null;
        row.TokenEmailEncrypted = null;
        row.Scopes = oauth.Scopes.Count > 0 ? oauth.Scopes : BitbucketRules.ConsumerPermissions;
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
        var row = await db.GitConnections.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId && c.Provider == GitHostKind.Bitbucket, cancellationToken);
        if (row is null)
        {
            return false;
        }

        await GitBindings.ClearForConnectionAsync(db, id, GitBindingClearedReason.ConnectionDeleted, actor ?? SystemActor.Name, time.GetUtcNow(), cancellationToken);
        db.GitConnections.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        limiter.Forget(id);
        LogDeleted(logger, userId, id);
        return true;
    }

    /// <summary>
    /// Checks one of the caller's connections with Bitbucket again (refreshing an OAuth token first when due) and records the
    /// result: <c>valid</c>, <c>invalid</c> (rejected after one refresh) or <c>insufficient_scope</c> (an API token that lost
    /// a scope). <c>UPSTREAM_ERROR</c> or <c>UPSTREAM_RATE_LIMITED</c> leave the status unchanged.
    /// </summary>
    public async Task<Result<BitbucketConnectionDto>> TestAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var row = await db.GitConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId && c.Provider == GitHostKind.Bitbucket, cancellationToken);
        if (row is null)
        {
            return Result.Failure<BitbucketConnectionDto>(NotFound());
        }

        if (row.Status != GitConnectionStatus.Valid)
        {
            // An invalid connection stays invalid until it is reconnected.
            return Result.Success(ToDto(row));
        }

        var outcome = await CallAsync(userId, id, async call =>
        {
            var user = await call.Client.GetUserAsync(call.Auth, cancellationToken);
            return row.AuthKind == GitConnectionAuthKind.Token ? await CheckTokenScopesAsync(call.Client, call.Auth, user, cancellationToken) : null;
        }, cancellationToken);
        if (outcome.IsFailure)
        {
            if (outcome.Error!.Code != ErrorCodes.GitNotConnected)
            {
                return Result.Failure<BitbucketConnectionDto>(outcome.Error);
            }

            // Only Bitbucket rejecting the token invalidates a connection, and the token service has recorded that already.
            var current = await db.GitConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            return current is null ? Result.Failure<BitbucketConnectionDto>(NotFound()) : Result.Success(ToDto(current));
        }

        // A scope Bitbucket refuses makes the connection insufficient_scope; one that merely can't be checked any more (e.g. the
        // probe repository was deleted) leaves a saved connection as it is.
        var status = outcome.Value is { Verdict.Missing.Count: > 0 } ? GitConnectionStatus.InsufficientScope : GitConnectionStatus.Valid;
        var tracked = await db.GitConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (tracked is null)
        {
            return Result.Failure<BitbucketConnectionDto>(NotFound());
        }

        var now = time.GetUtcNow();
        if (GitBindingRules.StatusChangeClearsBinding(tracked.Status, status))
        {
            await GitBindings.ClearForConnectionAsync(db, id, GitBindingClearedReason.ConnectionInvalid, SystemActor.Name, now, cancellationToken);
        }

        tracked.Status = status;
        if (outcome.Value is { Verdict.AllVerified: true } checkedScopes)
        {
            tracked.Scopes = checkedScopes.Confirmed;
        }

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

        return Result.Success(ToDto(tracked));
    }

    /// <summary>
    /// Runs <paramref name="call"/> with a valid token of the caller's Bitbucket connection <paramref name="connectionId"/>.
    /// Every request takes from the connection's budget (§4.19). A 401 refreshes an OAuth token once and retries; a second
    /// 401 (or an API token's 401) marks the connection invalid and notifies its owner (<c>GIT_NOT_CONNECTED</c>). Other
    /// failures: <c>NOT_FOUND</c> (not the caller's Bitbucket connection, or Bitbucket's 404), <c>FORBIDDEN</c> (403),
    /// <c>UPSTREAM_RATE_LIMITED</c> (429 or the budget, with <see cref="DomainError.RetryAfter"/>), <c>UPSTREAM_ERROR</c>.
    /// </summary>
    public async Task<Result<T>> CallAsync<T>(Guid userId, Guid connectionId, Func<BitbucketCall, Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var owned = await db.GitConnections.AsNoTracking()
            .AnyAsync(c => c.Id == connectionId && c.UserId == userId && c.Provider == GitHostKind.Bitbucket, cancellationToken);
        if (!owned)
        {
            return Result.Failure<T>(NotFound());
        }

        var access = await tokens.GetAccessTokenAsync(connectionId, cancellationToken);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (access.IsFailure)
            {
                return Result.Failure<T>(access.Error!);
            }

            var token = access.Value;
            var auth = token.Email is { } email ? BitbucketAuth.ApiToken(email, token.Token) : BitbucketAuth.OAuth(token.Token);
            try
            {
                return Result.Success(await call(new BitbucketCall(clients.Create(connectionId), auth, token.CredentialKind)));
            }
            catch (BitbucketApiException ex) when (ex.IsUnauthorized)
            {
                access = await tokens.OnUnauthorizedAsync(token, null, cancellationToken);
            }
            catch (BitbucketApiException ex)
            {
                return Result.Failure<T>(UpstreamError(ex));
            }
        }

        return Result.Failure<T>(new DomainError(ErrorCodes.GitNotConnected, "Bitbucket rejected the connection. Connect Bitbucket again under Account → Connections → Bitbucket."));
    }

    /// <summary>The AIDE error for a Bitbucket failure other than 401.</summary>
    public static DomainError UpstreamError(BitbucketApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Status switch
        {
            429 => new DomainError(ErrorCodes.UpstreamRateLimited, "Bitbucket is rate limiting requests. Try again in a few minutes.") { RetryAfter = exception.RetryAfter },
            403 => new DomainError(ErrorCodes.Forbidden, exception.ScopeError
                ? "Bitbucket refused: the connection doesn't have the permission this needs."
                : "Bitbucket refused: your Bitbucket account doesn't have permission to do that."),
            404 => new DomainError(ErrorCodes.NotFound, "Bitbucket couldn't find that, or your account can't see it."),
            _ => new DomainError(ErrorCodes.UpstreamError, exception.Status is null ? "Bitbucket couldn't be reached. Try again." : "Bitbucket returned an error. Try again later."),
        };
    }

    /// <summary>
    /// FR-134: checks the API token's scopes with harmless calls (§4.19 has no token-introspection endpoint for Bitbucket).
    /// <list type="bullet">
    /// <item>When Bitbucket lists the token's scopes with API-token names (the <c>X-OAuth-Scopes</c> response header, or the
    /// <c>granted</c> list of a scope error), that list decides every scope at once. An OAuth-style list is ignored and the
    /// scopes are probed (<see cref="BitbucketRules.ApiTokenScopes"/>).</item>
    /// <item>User level: <c>read:user</c> <c>GET /user</c> (already made); <c>read:pullrequest</c>
    /// <c>GET /pullrequests/{account uuid}</c>; <c>read:workspace</c> <c>GET /user/workspaces</c>.</item>
    /// <item>Workspace level (each workspace in turn until a scope is settled): <c>read:project</c>
    /// <c>GET /workspaces/{ws}/projects</c>; <c>read:repository</c> <c>GET /repositories/{ws}</c>; <c>write:repository</c>
    /// <c>POST /repositories/{ws}/aide-scope-check!</c> with an invalid body (an invalid slug and an unknown <c>scm</c>, so it
    /// can't create anything: a 400 means the scope is there).</item>
    /// <item>Repository level, on a repository the user administers (else one they can write to): <c>read:pipeline</c>
    /// <c>GET …/pipelines</c>, <c>admin:repository</c> <c>GET …/branch-restrictions</c>, <c>write:pullrequest</c>
    /// <c>POST …/pullrequests {}</c> (rejected for the missing title).</item>
    /// </list>
    /// A scope refused for scopes is missing; one whose call can't run or proves nothing (a 404, a permission 403) is
    /// unverifiable (<see cref="BitbucketRules.EvaluateTokenScopes"/>).
    /// </summary>
    private static async Task<BitbucketScopeCheck> CheckTokenScopesAsync(BitbucketClient client, BitbucketAuth auth, BitbucketUser user, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string>? granted = BitbucketRules.ApiTokenScopes(client.LastGrantedScopes);
        var probes = new Dictionary<string, BitbucketScopeProbe>(StringComparer.Ordinal) { ["read:user:bitbucket"] = BitbucketScopeProbe.Granted };

        bool Settled(string scope) => granted is not null || (probes.TryGetValue(scope, out var p) && p != BitbucketScopeProbe.Unverified);

        void Record(string scope, BitbucketScopeProbe probe, IReadOnlyList<string>? listed)
        {
            // Keep a decisive answer from an earlier workspace or repository.
            if (!Settled(scope))
            {
                probes[scope] = probe;
            }

            if (BitbucketRules.ApiTokenScopes(listed) is { } usable)
            {
                granted = usable;
            }
        }

        async Task ProbeAsync(string scope, HttpMethod method, string relative, string? body = null)
        {
            if (Settled(scope))
            {
                return;
            }

            var answer = body is null
                ? await client.ProbeAsync(auth, method, relative, cancellationToken)
                : await client.ProbeAsync(auth, method, relative, body, cancellationToken);
            Record(scope, BitbucketRules.ClassifyProbe(answer.Status, answer.ScopeError), answer.GrantedScopes);
        }

        async Task<T?> ReadAsync<T>(string scope, Func<Task<T>> read)
            where T : class
        {
            if (granted is not null)
            {
                return null;
            }

            try
            {
                var value = await read();
                Record(scope, BitbucketScopeProbe.Granted, null);
                return value;
            }
            catch (BitbucketApiException ex) when (ex.Status is 403 or 404)
            {
                Record(scope, BitbucketRules.ClassifyProbe(ex.Status.Value, ex.ScopeError), ex.GrantedScopes);
                return null;
            }
        }

        await ProbeAsync("read:pullrequest:bitbucket", HttpMethod.Get, $"pullrequests/{Uri.EscapeDataString(user.Uuid)}?pagelen=1");
        var workspaces = await ReadAsync("read:workspace:bitbucket", () => client.ListWorkspacesPageAsync(auth, 10, 1, cancellationToken));
        BitbucketRepository? repo = null;
        foreach (var workspace in workspaces?.Items ?? [])
        {
            if (granted is not null)
            {
                break;
            }

            var ws = Uri.EscapeDataString(workspace.Slug);
            await ProbeAsync("read:project:bitbucket", HttpMethod.Get, $"workspaces/{ws}/projects?pagelen=1");
            await ProbeAsync("write:repository:bitbucket", HttpMethod.Post, $"repositories/{ws}/{Uri.EscapeDataString(BitbucketRules.ScopeProbeSlug)}", BitbucketRules.ScopeProbeCreateBody);
            if (repo is null)
            {
                var admin = await ReadAsync("read:repository:bitbucket", () => client.ListRepositoriesAsync(auth, workspace.Slug, null, 1, "admin", cancellationToken));
                repo = admin is { Items.Count: > 0 } ? admin.Items[0] : null;
                if (repo is null && granted is null)
                {
                    var writable = await ReadAsync("read:repository:bitbucket", () => client.ListRepositoriesAsync(auth, workspace.Slug, null, 1, "contributor", cancellationToken));
                    repo = writable is { Items.Count: > 0 } ? writable.Items[0] : null;
                }
            }

            if (Settled("read:project:bitbucket") && Settled("write:repository:bitbucket") && Settled("read:repository:bitbucket") && repo is not null)
            {
                break;
            }
        }

        if (repo is not null && granted is null)
        {
            var path = $"repositories/{Uri.EscapeDataString(repo.WorkspaceSlug ?? repo.FullName.Split('/')[0])}/{Uri.EscapeDataString(repo.Slug)}";
            await ProbeAsync("read:pipeline:bitbucket", HttpMethod.Get, $"{path}/pipelines?pagelen=1");
            await ProbeAsync("admin:repository:bitbucket", HttpMethod.Get, $"{path}/branch-restrictions?pagelen=1");
            await ProbeAsync("write:pullrequest:bitbucket", HttpMethod.Post, $"{path}/pullrequests");
        }

        var verdict = BitbucketRules.EvaluateTokenScopes(granted, probes);
        IReadOnlyList<string> confirmed = [.. GitTokenRules.BitbucketTokenScopes.Where(s => !verdict.Missing.Contains(s) && !verdict.Unverifiable.Contains(s))];
        return new BitbucketScopeCheck(verdict, confirmed);
    }

    private async Task<GitConnection> FindOrAddAsync(Guid userId, Guid instanceId, string externalUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await db.GitConnections.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == GitHostKind.Bitbucket && c.InstanceId == instanceId && c.ExternalUserId == externalUserId,
            cancellationToken);
        if (row is null)
        {
            row = new GitConnection
            {
                UserId = userId,
                Provider = GitHostKind.Bitbucket,
                InstanceId = instanceId,
                ExternalUserId = externalUserId,
                CreatedAt = now,
            };
            db.GitConnections.Add(row);
        }

        return row;
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
            db.ChangeTracker.Clear();
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "This Bitbucket connection was changed by another request at the same time. Try again."));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "This Bitbucket account was connected by another request at the same time. Try again."));
        }
    }

    private static DomainError NotFound() => new(ErrorCodes.NotFound, "The Bitbucket connection was not found.");

    private static BitbucketConnectionDto ToDto(GitConnection row) => new(
        row.Id,
        row.Login,
        row.ExternalUserId,
        row.AuthKind,
        row.Scopes,
        row.Status,
        row.LastValidatedAt,
        row.CreatedAt,
        row.UpdatedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bitbucket token connection {ConnectionId} saved for user {UserId}")]
    private static partial void LogTokenSaved(ILogger logger, Guid userId, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bitbucket OAuth connection {ConnectionId} saved for user {UserId}")]
    private static partial void LogOAuthSaved(ILogger logger, Guid userId, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bitbucket connection {ConnectionId} of user {UserId} deleted")]
    private static partial void LogDeleted(ILogger logger, Guid userId, Guid connectionId);
}
