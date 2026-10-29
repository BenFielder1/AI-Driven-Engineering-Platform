using Aide.Api.Auth;
using Aide.Api.Features.Git;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Bitbucket;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aide.Api.Features.Bitbucket;

/// <summary>
/// FR-134: "Continue with Bitbucket" and "Connect Bitbucket" through the OAuth consumer: authorization code
/// (<c>https://bitbucket.org/site/oauth2/authorize</c>, scopes fixed on the consumer), a signed single-use browser-bound
/// <c>state</c> (<see cref="BitbucketOAuthStates"/>), the exchange at <c>/site/oauth2/access_token</c> with client Basic auth,
/// the identity from <c>GET /2.0/user</c> and the verified email from <c>GET /2.0/user/emails</c> (confirmed addresses only).
/// Accounts match by <c>(bitbucket, account uuid)</c> in <c>user_external_logins</c> (<see cref="GitSignInRules"/>), never
/// silently by email (D-V4-18): an existing account with the same email is linked only when its owner signs in and confirms
/// (<see cref="BitbucketPendingLinks"/>, <c>/login?link=bitbucket</c>). Failures redirect with
/// <c>error=GIT_AUTH_FAILED&amp;reason=…&amp;provider=bitbucket</c>.
/// </summary>
public sealed partial class BitbucketSignIn(
    AideDbContext db,
    IBitbucketConsumer consumer,
    IBitbucketClients clients,
    BitbucketOAuthStates states,
    BitbucketPendingLinks pendingLinks,
    BitbucketConnectionService connections,
    UserManager<AppUser> users,
    AccountService accounts,
    IOptions<AideAuthOptions> authOptions,
    TimeProvider time,
    ILogger<BitbucketSignIn> logger)
{
    public const string Provider = "bitbucket";

    /// <summary>The <c>reason</c> when too many OAuth flows are in progress to start another.</summary>
    public const string Busy = "busy";

    private const string CookiePath = "/api/auth/bitbucket";

    /// <summary>
    /// Starts the flow: the authorize URL with the state cookie set, or a <c>GIT_AUTH_FAILED&amp;reason=busy</c> redirect when
    /// too many flows are held. <c>null</c> when no consumer is configured (<c>NOT_FOUND</c>).
    /// </summary>
    public async Task<string?> StartAsync(HttpContext http, GitOAuthMode mode, string? returnUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var info = await consumer.GetAsync(cancellationToken);
        if (!info.OAuthConfigured || info.ClientId is not { } clientId)
        {
            return null;
        }

        var userId = mode == GitOAuthMode.Connect ? http.User.GetUserId() : null;
        var safeReturnUrl = ReturnUrls.Sanitize(returnUrl);
        if (states.Issue(mode, safeReturnUrl, userId) is not { } issued)
        {
            LogFailed(logger, Busy);
            return GitOAuthRedirects.FailureUrl(Provider, mode, safeReturnUrl, Busy);
        }

        var (state, cookie, _) = issued;
        http.Response.Cookies.Append(BitbucketOAuthStates.CookieName, cookie, Cookie(http, GitOAuthLimits.Lifetime));
        LogStarted(logger, mode);
        return BitbucketClient.AuthorizeUrl(clientId, state).AbsoluteUri;
    }

    /// <summary>Completes the flow; returns where to redirect the browser.</summary>
    public async Task<string> CallbackAsync(HttpContext http, string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var flow = states.TryConsume(state, http.Request.Cookies[BitbucketOAuthStates.CookieName]);
        http.Response.Cookies.Delete(BitbucketOAuthStates.CookieName, Cookie(http, TimeSpan.Zero));
        if (flow is null)
        {
            LogFailed(logger, "state");
            return GitOAuthRedirects.FailureUrl(Provider, GitOAuthMode.Signin, GitOAuthRedirects.LoginPath, "state");
        }

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            LogFailed(logger, string.IsNullOrEmpty(error) ? "code" : "access_denied");
            return FailureUrl(flow, "access_denied");
        }

        var info = await consumer.GetAsync(cancellationToken);
        if (!info.OAuthConfigured || info.ClientId is not { } clientId || await consumer.GetClientSecretAsync(cancellationToken) is not { } clientSecret)
        {
            LogFailed(logger, "consumer");
            return FailureUrl(flow, "instance_unavailable");
        }

        var client = clients.Create();
        BitbucketOAuthTokens tokens;
        BitbucketUser bitbucketUser;
        IReadOnlyList<BitbucketEmail> emails;
        try
        {
            tokens = await client.ExchangeCodeAsync(clientId, clientSecret, code, cancellationToken);
            var auth = BitbucketAuth.OAuth(tokens.AccessToken);
            bitbucketUser = await client.GetUserAsync(auth, cancellationToken);
            emails = await EmailsAsync(client, auth, cancellationToken);
        }
        catch (BitbucketApiException ex)
        {
            LogExchangeFailed(logger, ex.Status, ex.OauthError);
            return FailureUrl(flow, "token_exchange");
        }

        // FR-134: only an address Bitbucket confirmed may create or match an account.
        var verifiedEmail = BitbucketRules.VerifiedEmail(emails);
        var linkedUserId = await LinkedUserAsync(info.InstanceId, bitbucketUser.Uuid, cancellationToken);
        var currentUserId = http.User.GetUserId();
        var emailOwner = verifiedEmail is null || linkedUserId is not null ? null : await users.FindByEmailAsync(verifiedEmail);
        var decision = GitSignInRules.Decide(flow.Mode, flow.StartedByUserId, currentUserId, linkedUserId, verifiedEmail, emailOwner?.Id);

        switch (decision.Action)
        {
            case GitSignInAction.SignInLinked:
            case GitSignInAction.LinkToCurrentUser:
                {
                    var user = await users.FindByIdAsync(decision.UserId!.Value.ToString());
                    if (user is null)
                    {
                        return FailureUrl(flow, GitSignInRules.LinkSession);
                    }

                    var linked = await LinkAndConnectAsync(user.Id, info.InstanceId, bitbucketUser, tokens, cancellationToken);
                    if (linked is not null)
                    {
                        return FailureUrl(flow, linked);
                    }

                    if (decision.Action == GitSignInAction.SignInLinked && currentUserId != user.Id)
                    {
                        await accounts.SignInAsync(http, user);
                        LogSignedIn(logger, user.Id);
                    }

                    return flow.ReturnUrl;
                }

            case GitSignInAction.CreateAccount:
                {
                    var user = new AppUser
                    {
                        Email = verifiedEmail,
                        UserName = verifiedEmail,
                        EmailConfirmed = true,
                        Name = Truncate(bitbucketUser.DisplayName ?? bitbucketUser.Login, 200),
                        AvatarUrl = bitbucketUser.AvatarUrl is { Length: <= 500 } avatar ? avatar : null,
                    };

                    // Create, link and connect in one transaction: losing a race (already_linked) or failing to store the
                    // connection leaves no orphan passwordless account behind.
                    await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
                    {
                        var created = await accounts.CreateAsync(user, password: null, cancellationToken);
                        if (!created.Succeeded)
                        {
                            await RollbackAsync(transaction);
                            LogFailed(logger, "account_create_failed");
                            return FailureUrl(flow, "account_create_failed");
                        }

                        var linked = await LinkAndConnectAsync(user.Id, info.InstanceId, bitbucketUser, tokens, cancellationToken);
                        if (linked is not null)
                        {
                            await RollbackAsync(transaction);
                            LogFailed(logger, linked);
                            return FailureUrl(flow, linked);
                        }

                        await transaction.CommitAsync(cancellationToken);
                    }

                    LogCreated(logger, user.Id, user.SystemRole);
                    await accounts.SignInAsync(http, user);
                    return flow.ReturnUrl;
                }

            case GitSignInAction.ConfirmEmailLink:
                {
                    var (cookie, link) = pendingLinks.Add(info.InstanceId, decision.UserId!.Value, verifiedEmail!, bitbucketUser, tokens);
                    http.Response.Cookies.Append(BitbucketPendingLinks.CookieName, cookie, Cookie(http, GitOAuthLimits.Lifetime));
                    LogLinkPending(logger, link.Id);
                    return GitOAuthRedirects.LinkUrl(Provider, flow.ReturnUrl);
                }

            default:
                LogFailed(logger, decision.Reason ?? "refused");
                return FailureUrl(flow, decision.Reason ?? "refused");
        }
    }

    /// <summary>The pending email link of this browser, if any (FR-134 confirm step).</summary>
    public BitbucketPendingLinkDto? PendingLink(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return pendingLinks.Find(http.Request.Cookies[BitbucketPendingLinks.CookieName]) is { } link
            ? new BitbucketPendingLinkDto(link.User.Login, link.Email, link.ExpiresAt)
            : null;
    }

    /// <summary>
    /// FR-134, D-V4-18: the signed-in owner of the matching account confirms the link: the Bitbucket identity is linked and
    /// connected. <c>NOT_FOUND</c> without a pending link; <c>FORBIDDEN</c> when signed in as another account;
    /// <c>CONFLICT</c> when the identity was linked to someone else meanwhile.
    /// </summary>
    public async Task<Result> ConfirmLinkAsync(HttpContext http, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var cookie = http.Request.Cookies[BitbucketPendingLinks.CookieName];
        var link = pendingLinks.Find(cookie);
        if (link is null)
        {
            return Result.Failure(new DomainError(ErrorCodes.NotFound, "There's no Bitbucket sign-in waiting to be linked. Continue with Bitbucket again."));
        }

        var linkedUserId = await LinkedUserAsync(link.InstanceId, link.User.Uuid, cancellationToken);
        if (!GitSignInRules.CanConfirmEmailLink(link.OfferedToUserId, userId, link.ExpiresAt, time.GetUtcNow(), linkedUserId))
        {
            return linkedUserId is { } other && other != link.OfferedToUserId
                ? Result.Failure(new DomainError(ErrorCodes.Conflict, "This Bitbucket account is already linked to another AIDE account."))
                : Result.Failure(new DomainError(ErrorCodes.Forbidden, "Sign in to the AIDE account with this email to link Bitbucket to it."));
        }

        pendingLinks.Take(cookie);
        http.Response.Cookies.Delete(BitbucketPendingLinks.CookieName, Cookie(http, TimeSpan.Zero));
        var failure = await LinkAndConnectAsync(userId, link.InstanceId, link.User, link.Tokens, cancellationToken);
        if (failure is not null)
        {
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "This Bitbucket account is already linked to another AIDE account."));
        }

        LogLinkConfirmed(logger, userId);
        return Result.Success();
    }

    /// <summary>Drops this browser's pending link (the user declined).</summary>
    public void CancelLink(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        pendingLinks.Take(http.Request.Cookies[BitbucketPendingLinks.CookieName]);
        http.Response.Cookies.Delete(BitbucketPendingLinks.CookieName, Cookie(http, TimeSpan.Zero));
    }

    /// <summary>
    /// <c>GET /2.0/user/emails</c>; a refusal (the consumer lacks the <c>email</c> permission) leaves no verified email, so a
    /// new account can't be created (<c>email_required</c>) but a linked one still signs in.
    /// </summary>
    private static async Task<IReadOnlyList<BitbucketEmail>> EmailsAsync(BitbucketClient client, BitbucketAuth auth, CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetEmailsAsync(auth, cancellationToken);
        }
        catch (BitbucketApiException ex) when (ex.Status is 403 or 404)
        {
            return [];
        }
    }

    /// <summary>Links the identity to <paramref name="userId"/> (if not yet) and stores the connection; a failure reason, else null.</summary>
    private async Task<string?> LinkAndConnectAsync(Guid userId, Guid instanceId, BitbucketUser bitbucketUser, BitbucketOAuthTokens tokens, CancellationToken cancellationToken)
    {
        var login = await db.UserExternalLogins.FirstOrDefaultAsync(
            l => l.Provider == ExternalLoginProvider.Bitbucket && l.InstanceId == instanceId && l.ExternalUserId == bitbucketUser.Uuid,
            cancellationToken);
        if (login is not null && login.UserId != userId)
        {
            return GitSignInRules.AlreadyLinked;
        }

        if (login is null)
        {
            db.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = userId,
                Provider = ExternalLoginProvider.Bitbucket,
                InstanceId = instanceId,
                ExternalUserId = bitbucketUser.Uuid,
                Login = bitbucketUser.Login,
                CreatedAt = time.GetUtcNow(),
            });
        }
        else
        {
            login.Login = bitbucketUser.Login;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race: the same Bitbucket identity was linked concurrently.
            db.ChangeTracker.Clear();
            return GitSignInRules.AlreadyLinked;
        }

        var saved = await connections.SaveOAuthAsync(userId, instanceId, bitbucketUser, tokens, cancellationToken);
        return saved.IsSuccess ? null : "connection_failed";
    }

    private async Task RollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    private Task<Guid?> LinkedUserAsync(Guid instanceId, string externalUserId, CancellationToken cancellationToken) =>
        db.UserExternalLogins.AsNoTracking()
            .Where(l => l.Provider == ExternalLoginProvider.Bitbucket && l.InstanceId == instanceId && l.ExternalUserId == externalUserId)
            .Select(l => (Guid?)l.UserId)
            .FirstOrDefaultAsync(cancellationToken);

    private static string FailureUrl(BitbucketOAuthFlow flow, string reason) =>
        GitOAuthRedirects.FailureUrl(Provider, flow.Mode, flow.ReturnUrl, reason);

    // The Bitbucket → callback hop is a cross-site top-level GET, which Lax cookies survive.
    private CookieOptions Cookie(HttpContext http, TimeSpan lifetime) => GitOAuthRedirects.Cookie(http, authOptions.Value, CookiePath, lifetime);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Information, Message = "Bitbucket OAuth started ({Mode})")]
    private static partial void LogStarted(ILogger logger, GitOAuthMode mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bitbucket OAuth callback refused ({Reason})")]
    private static partial void LogFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bitbucket token exchange failed (HTTP {Status}, {OauthError})")]
    private static partial void LogExchangeFailed(ILogger logger, int? status, string? oauthError);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} signed in with Bitbucket")]
    private static partial void LogSignedIn(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} created from Bitbucket sign-in with role {SystemRole}")]
    private static partial void LogCreated(ILogger logger, Guid userId, Domain.Access.SystemRole systemRole);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bitbucket sign-in {LinkId} waits for the matching account to confirm the link")]
    private static partial void LogLinkPending(ILogger logger, Guid linkId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} confirmed linking Bitbucket")]
    private static partial void LogLinkConfirmed(ILogger logger, Guid userId);
}
