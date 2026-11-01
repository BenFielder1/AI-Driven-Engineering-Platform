using Aide.Api.Features.Git;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Bitbucket;
using Microsoft.AspNetCore.DataProtection;

namespace Aide.Api.Features.Bitbucket;

/// <summary>A started Bitbucket OAuth flow (FR-134), held server-side until its callback. Bitbucket's consumer flow has no PKCE.</summary>
public sealed record BitbucketOAuthFlow(GitOAuthMode Mode, string ReturnUrl, Guid? StartedByUserId, DateTimeOffset ExpiresAt) : IGitOAuthFlow
{
    public override string ToString() => $"BitbucketOAuthFlow {{ Mode = {Mode} }}";
}

/// <summary>§8 OAuth <c>state</c> for Bitbucket (<see cref="GitOAuthStateStore{TFlow}"/>): signed, time-limited, single-use, browser-bound and bounded.</summary>
public sealed class BitbucketOAuthStates(IDataProtectionProvider dataProtection, TimeProvider time)
    : GitOAuthStateStore<BitbucketOAuthFlow>(dataProtection, time, Purpose)
{
    /// <summary>The browser-binding cookie (path <c>/api/auth/bitbucket</c>).</summary>
    public const string CookieName = "aide.bitbucket.oauth";

    private const string Purpose = "Aide.Bitbucket.OAuthState.v1";

    /// <summary>Starts a flow: the <c>state</c> parameter and the cookie value; <c>null</c> when too many flows are held.</summary>
    public (string State, string CookieValue, BitbucketOAuthFlow Flow)? Issue(GitOAuthMode mode, string returnUrl, Guid? userId) =>
        IssueCore(expiresAt => new BitbucketOAuthFlow(mode, returnUrl, userId, expiresAt));
}

/// <summary>FR-134, D-V4-18: a Bitbucket sign-in whose verified email matches an existing, unlinked AIDE account.</summary>
public sealed record BitbucketPendingLink(
    Guid Id,
    Guid InstanceId,
    Guid OfferedToUserId,
    string Email,
    BitbucketUser User,
    BitbucketOAuthTokens Tokens,
    DateTimeOffset ExpiresAt) : IGitPendingLink
{
    public override string ToString() => $"BitbucketPendingLink {{ Id = {Id}, Login = {User.Login} }}";
}

/// <summary>FR-134: Bitbucket's pending email links (<see cref="GitPendingLinkStore{TLink}"/>).</summary>
public sealed class BitbucketPendingLinks(IDataProtectionProvider dataProtection, TimeProvider time)
    : GitPendingLinkStore<BitbucketPendingLink>(dataProtection, time, Purpose)
{
    public const string CookieName = "aide.bitbucket.link";

    private const string Purpose = "Aide.Bitbucket.PendingLink.v1";

    /// <summary>Holds a link and returns its cookie value.</summary>
    public (string CookieValue, BitbucketPendingLink Link) Add(Guid instanceId, Guid offeredTo, string email, BitbucketUser user, BitbucketOAuthTokens tokens)
    {
        var link = new BitbucketPendingLink(Guid.CreateVersion7(), instanceId, offeredTo, email, user, tokens, Time.GetUtcNow() + GitOAuthLimits.Lifetime);
        return (AddCore(link), link);
    }
}
