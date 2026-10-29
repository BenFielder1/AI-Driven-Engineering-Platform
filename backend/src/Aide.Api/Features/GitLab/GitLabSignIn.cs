using Aide.Api.Auth;
using Aide.Api.Features.Git;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aide.Api.Features.GitLab;

/// <summary>
/// FR-131: "Continue with GitLab" and "Connect GitLab" through the instance's OAuth application: authorization code with
/// PKCE S256 and the scopes <c>api write_repository openid profile email</c>, a signed single-use browser-bound
/// <c>state</c> (<see cref="GitLabOAuthStates"/>), the token exchange on the instance that issued the code, the identity from
/// <c>GET /api/v4/user</c> and the verified email from OIDC <c>/oauth/userinfo</c>. Accounts match by
/// <c>(gitlab, instance, user id)</c> in <c>user_external_logins</c> (<see cref="GitSignInRules"/>): an existing account with
/// the same email is linked only when its owner signs in and confirms (<see cref="GitLabPendingLinks"/>). Failures redirect
/// with <c>error=GIT_AUTH_FAILED&amp;reason=…&amp;provider=gitlab</c>.
/// </summary>
public sealed partial class GitLabSignIn(
    AideDbContext db,
    IGitLabInstances instances,
    IGitLabClients clients,
    GitLabOAuthStates states,
    GitLabPendingLinks pendingLinks,
    GitLabConnectionService connections,
    UserManager<AppUser> users,
    AccountService accounts,
    IOptions<AideAuthOptions> authOptions,
    TimeProvider time,
    ILogger<GitLabSignIn> logger)
{
    public const string LoginPath = GitOAuthRedirects.LoginPath;

    /// <summary>The <c>reason</c> when too many OAuth flows are in progress to start another.</summary>
    public const string Busy = "busy";

    private const string CookiePath = "/api/auth/gitlab";

    /// <summary>
    /// Starts the flow: where to redirect the browser, normally the authorize URL on the instance with the state cookie set.
    /// When too many flows are in progress (<see cref="GitLabOAuthStates.MaxPending"/>) it redirects with
    /// <c>error=GIT_AUTH_FAILED&amp;reason=busy</c> instead. <c>null</c> when the instance doesn't exist, is disabled, or has no
    /// OAuth application (<c>NOT_FOUND</c>).
    /// </summary>
    public async Task<string?> StartAsync(HttpContext http, Guid? instanceId, GitOAuthMode mode, string? returnUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var instance = await connections.UsableInstanceAsync(instanceId, cancellationToken);
        if (instance is not { OAuthConfigured: true } || instance.ClientId is not { } clientId)
        {
            return null;
        }

        var userId = mode == GitOAuthMode.Connect ? http.User.GetUserId() : null;
        var safeReturnUrl = ReturnUrls.Sanitize(returnUrl);
        if (states.Issue(instance.Id, mode, safeReturnUrl, userId) is not { } issued)
        {
            LogFailed(logger, Busy);
            return FailureUrl(mode, safeReturnUrl, Busy);
        }

        var (state, cookie, flow) = issued;

        http.Response.Cookies.Append(GitLabOAuthStates.CookieName, cookie, CookieOptions(http, GitLabOAuthStates.Lifetime));
        LogStarted(logger, instance.Id, mode);
        return clients.ForInstance(instance.BaseUrl).AuthorizeUrl(clientId, instances.RedirectUri, state, GitLabRules.PkceChallenge(flow.CodeVerifier)).AbsoluteUri;
    }

    /// <summary>Completes the flow; returns where to redirect the browser.</summary>
    public async Task<string> CallbackAsync(HttpContext http, string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var flow = states.TryConsume(state, http.Request.Cookies[GitLabOAuthStates.CookieName]);
        http.Response.Cookies.Delete(GitLabOAuthStates.CookieName, CookieOptions(http, TimeSpan.Zero));
        if (flow is null)
        {
            LogFailed(logger, "state");
            return FailureUrl(null, "state");
        }

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            LogFailed(logger, string.IsNullOrEmpty(error) ? "code" : "access_denied");
            return FailureUrl(flow, "access_denied");
        }

        var instance = await connections.UsableInstanceAsync(flow.InstanceId, cancellationToken);
        if (instance is not { OAuthConfigured: true } || instance.ClientId is not { } clientId
            || await instances.GetClientSecretAsync(instance, cancellationToken) is not { } clientSecret)
        {
            LogFailed(logger, "instance");
            return FailureUrl(flow, "instance_unavailable");
        }

        var client = clients.ForInstance(instance.BaseUrl);
        GitLabOAuthTokens tokens;
        GitLabUser gitlabUser;
        GitLabUserInfo? userInfo;
        try
        {
            tokens = await client.ExchangeCodeAsync(clientId, clientSecret, code, instances.RedirectUri, flow.CodeVerifier, cancellationToken);
            gitlabUser = await client.GetUserAsync(tokens.AccessToken, cancellationToken);
            userInfo = await client.GetUserInfoAsync(tokens.AccessToken, cancellationToken);
        }
        catch (GitLabApiException ex)
        {
            LogExchangeFailed(logger, ex.Status, ex.OauthError);
            return FailureUrl(flow, "token_exchange");
        }
        catch (GitHostBlockedException)
        {
            LogFailed(logger, "host_not_allowed");
            return FailureUrl(flow, "host_not_allowed");
        }

        // FR-131: only an email GitLab verified (OIDC email_verified, for the same subject) may create or match an account.
        var verifiedEmail = userInfo is { EmailVerified: true, Email: { Length: > 0 } email }
            && (userInfo.Subject is null || userInfo.Subject == gitlabUser.ExternalId)
                ? email.Trim()
                : null;
        var linkedUserId = await LinkedUserAsync(instance.Id, gitlabUser.ExternalId, cancellationToken);
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

                    var linked = await LinkAndConnectAsync(user.Id, instance, gitlabUser, tokens, cancellationToken);
                    if (linked is not null)
                    {
                        return FailureUrl(flow, linked);
                    }

                    if (decision.Action == GitSignInAction.SignInLinked && currentUserId != user.Id)
                    {
                        await accounts.SignInAsync(http, user);
                        LogSignedIn(logger, user.Id, instance.Id);
                    }

                    return SuccessUrl(flow);
                }

            case GitSignInAction.CreateAccount:
                {
                    var user = new AppUser
                    {
                        Email = verifiedEmail,
                        UserName = verifiedEmail,
                        EmailConfirmed = true,
                        Name = Truncate(gitlabUser.Name ?? gitlabUser.Username, 200),
                        AvatarUrl = gitlabUser.AvatarUrl is { Length: <= 500 } avatar ? avatar : null,
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

                        var linked = await LinkAndConnectAsync(user.Id, instance, gitlabUser, tokens, cancellationToken);
                        if (linked is not null)
                        {
                            await RollbackAsync(transaction);
                            LogFailed(logger, linked);
                            return FailureUrl(flow, linked);
                        }

                        await transaction.CommitAsync(cancellationToken);
                    }

                    LogCreated(logger, user.Id, user.SystemRole, instance.Id);
                    await accounts.SignInAsync(http, user);
                    return SuccessUrl(flow);
                }

            case GitSignInAction.ConfirmEmailLink:
                {
                    var (cookie, link) = pendingLinks.Add(instance, decision.UserId!.Value, verifiedEmail!, gitlabUser, tokens);
                    http.Response.Cookies.Append(GitLabPendingLinks.CookieName, cookie, CookieOptions(http, GitLabPendingLinks.Lifetime));
                    LogLinkPending(logger, link.Id, instance.Id);
                    return GitOAuthRedirects.LinkUrl("gitlab", flow.ReturnUrl);
                }

            default:
                LogFailed(logger, decision.Reason ?? "refused");
                return FailureUrl(flow, decision.Reason ?? "refused");
        }
    }

    /// <summary>The pending email link of this browser, if any (FR-131 confirm step).</summary>
    public GitLabPendingLinkDto? PendingLink(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return pendingLinks.Find(http.Request.Cookies[GitLabPendingLinks.CookieName]) is { } link
            ? new GitLabPendingLinkDto(link.Instance.Id, link.Instance.Name, link.User.Username, link.Email, link.ExpiresAt)
            : null;
    }

    /// <summary>
    /// FR-131: the signed-in owner of the matching account confirms the link: the GitLab identity is linked and connected.
    /// <c>NOT_FOUND</c> without a pending link; <c>FORBIDDEN</c> when signed in as another account; <c>CONFLICT</c> when the
    /// identity was linked to someone else meanwhile.
    /// </summary>
    public async Task<Result> ConfirmLinkAsync(HttpContext http, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var cookie = http.Request.Cookies[GitLabPendingLinks.CookieName];
        var link = pendingLinks.Find(cookie);
        if (link is null)
        {
            return Result.Failure(new DomainError(ErrorCodes.NotFound, "There's no GitLab sign-in waiting to be linked. Continue with GitLab again."));
        }

        var linkedUserId = await LinkedUserAsync(link.Instance.Id, link.User.ExternalId, cancellationToken);
        if (!GitSignInRules.CanConfirmEmailLink(link.OfferedToUserId, userId, link.ExpiresAt, time.GetUtcNow(), linkedUserId))
        {
            return linkedUserId is { } other && other != link.OfferedToUserId
                ? Result.Failure(new DomainError(ErrorCodes.Conflict, "This GitLab account is already linked to another AIDE account."))
                : Result.Failure(new DomainError(ErrorCodes.Forbidden, "Sign in to the AIDE account with this email to link GitLab to it."));
        }

        pendingLinks.Take(cookie);
        http.Response.Cookies.Delete(GitLabPendingLinks.CookieName, CookieOptions(http, TimeSpan.Zero));
        var failure = await LinkAndConnectAsync(userId, link.Instance, link.User, link.Tokens, cancellationToken);
        if (failure is not null)
        {
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "This GitLab account is already linked to another AIDE account."));
        }

        LogLinkConfirmed(logger, userId, link.Instance.Id);
        return Result.Success();
    }

    /// <summary>Drops this browser's pending link (the user declined).</summary>
    public void CancelLink(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        pendingLinks.Take(http.Request.Cookies[GitLabPendingLinks.CookieName]);
        http.Response.Cookies.Delete(GitLabPendingLinks.CookieName, CookieOptions(http, TimeSpan.Zero));
    }

    /// <summary>Links the identity to <paramref name="userId"/> (if not yet) and stores the connection; a failure reason, else null.</summary>
    private async Task<string?> LinkAndConnectAsync(Guid userId, GitLabInstance instance, GitLabUser gitlabUser, GitLabOAuthTokens tokens, CancellationToken cancellationToken)
    {
        var login = await db.UserExternalLogins.FirstOrDefaultAsync(
            l => l.Provider == ExternalLoginProvider.Gitlab && l.InstanceId == instance.Id && l.ExternalUserId == gitlabUser.ExternalId,
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
                Provider = ExternalLoginProvider.Gitlab,
                InstanceId = instance.Id,
                ExternalUserId = gitlabUser.ExternalId,
                Login = gitlabUser.Username,
                CreatedAt = time.GetUtcNow(),
            });
        }
        else
        {
            login.Login = gitlabUser.Username;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race: the same GitLab identity was linked concurrently.
            db.ChangeTracker.Clear();
            return GitSignInRules.AlreadyLinked;
        }

        var saved = await connections.SaveOAuthAsync(userId, instance, gitlabUser, tokens, cancellationToken);
        return saved.IsSuccess ? null : "connection_failed";
    }

    /// <summary>Rolls back and forgets what the rolled-back work tracked.</summary>
    private async Task RollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    private Task<Guid?> LinkedUserAsync(Guid instanceId, string externalUserId, CancellationToken cancellationToken) =>
        db.UserExternalLogins.AsNoTracking()
            .Where(l => l.Provider == ExternalLoginProvider.Gitlab && l.InstanceId == instanceId && l.ExternalUserId == externalUserId)
            .Select(l => (Guid?)l.UserId)
            .FirstOrDefaultAsync(cancellationToken);

    private static string SuccessUrl(GitLabOAuthFlow flow) => flow.ReturnUrl;

    /// <summary>Connect returns to its page; sign-in (or an unknown flow) to the login page.</summary>
    private static string FailureUrl(GitLabOAuthFlow? flow, string reason) =>
        flow is null ? FailureUrl(GitOAuthMode.Signin, LoginPath, reason) : FailureUrl(flow.Mode, flow.ReturnUrl, reason);

    private static string FailureUrl(GitOAuthMode mode, string returnUrl, string reason) =>
        GitOAuthRedirects.FailureUrl("gitlab", mode, returnUrl, reason);

    // The GitLab → callback hop is a cross-site top-level GET, which Lax cookies survive.
    private CookieOptions CookieOptions(HttpContext http, TimeSpan lifetime) => GitOAuthRedirects.Cookie(http, authOptions.Value, CookiePath, lifetime);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Information, Message = "GitLab OAuth started for instance {InstanceId} ({Mode})")]
    private static partial void LogStarted(ILogger logger, Guid instanceId, GitOAuthMode mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitLab OAuth callback refused ({Reason})")]
    private static partial void LogFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitLab token exchange failed (HTTP {Status}, {OauthError})")]
    private static partial void LogExchangeFailed(ILogger logger, int? status, string? oauthError);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} signed in with GitLab instance {InstanceId}")]
    private static partial void LogSignedIn(ILogger logger, Guid userId, Guid instanceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} created from GitLab sign-in (instance {InstanceId}) with role {SystemRole}")]
    private static partial void LogCreated(ILogger logger, Guid userId, Domain.Access.SystemRole systemRole, Guid instanceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitLab sign-in {LinkId} (instance {InstanceId}) waits for the matching account to confirm the link")]
    private static partial void LogLinkPending(ILogger logger, Guid linkId, Guid instanceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} confirmed linking GitLab instance {InstanceId}")]
    private static partial void LogLinkConfirmed(ILogger logger, Guid userId, Guid instanceId);
}
