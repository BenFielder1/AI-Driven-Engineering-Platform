using System.Security.Cryptography;
using Aide.Api.Features.Git;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.GitLab;
using Microsoft.AspNetCore.DataProtection;

namespace Aide.Api.Features.GitLab;

/// <summary>A started GitLab OAuth flow (FR-131), held server-side until its callback. The PKCE verifier never leaves AIDE.</summary>
public sealed record GitLabOAuthFlow(Guid InstanceId, GitOAuthMode Mode, string ReturnUrl, Guid? StartedByUserId, string CodeVerifier, DateTimeOffset ExpiresAt) : IGitOAuthFlow
{
    public override string ToString() => $"GitLabOAuthFlow {{ InstanceId = {InstanceId}, Mode = {Mode} }}";
}

/// <summary>
/// §8 OAuth <c>state</c> for GitLab (<see cref="GitOAuthStateStore{TFlow}"/>): signed, time-limited, single-use,
/// browser-bound and bounded, with a new PKCE verifier per flow.
/// </summary>
public sealed class GitLabOAuthStates(IDataProtectionProvider dataProtection, TimeProvider time)
    : GitOAuthStateStore<GitLabOAuthFlow>(dataProtection, time, Purpose)
{
    public static readonly TimeSpan Lifetime = GitOAuthLimits.Lifetime;

    /// <summary>The most flows held at once.</summary>
    public const int MaxPending = GitOAuthLimits.MaxPending;

    /// <summary>How often expired flows are swept (amortised over starts).</summary>
    public static readonly TimeSpan SweepInterval = GitOAuthLimits.SweepInterval;

    /// <summary>The browser-binding cookie (path <c>/api/auth/gitlab</c>).</summary>
    public const string CookieName = "aide.gitlab.oauth";

    private const string Purpose = "Aide.GitLab.OAuthState.v1";

    /// <summary>
    /// Starts a flow: returns the <c>state</c> parameter, the cookie value and a new PKCE verifier (in the flow); <c>null</c>
    /// when <see cref="MaxPending"/> flows are already held.
    /// </summary>
    public (string State, string CookieValue, GitLabOAuthFlow Flow)? Issue(Guid instanceId, GitOAuthMode mode, string returnUrl, Guid? userId) =>
        IssueCore(expiresAt => new GitLabOAuthFlow(instanceId, mode, returnUrl, userId, GitLabRules.Base64Url(RandomNumberGenerator.GetBytes(32)), expiresAt));
}

/// <summary>
/// FR-131: a sign-in whose verified GitLab email matches an existing, unlinked AIDE account (<see cref="GitLabPendingLinks"/>).
/// </summary>
public sealed record GitLabPendingLink(
    Guid Id,
    GitLabInstance Instance,
    Guid OfferedToUserId,
    string Email,
    GitLabUser User,
    GitLabOAuthTokens Tokens,
    DateTimeOffset ExpiresAt) : IGitPendingLink
{
    public override string ToString() => $"GitLabPendingLink {{ Id = {Id}, Login = {User.Username} }}";
}

/// <summary>FR-131: GitLab's pending email links (<see cref="GitPendingLinkStore{TLink}"/>).</summary>
public sealed class GitLabPendingLinks(IDataProtectionProvider dataProtection, TimeProvider time)
    : GitPendingLinkStore<GitLabPendingLink>(dataProtection, time, Purpose)
{
    public static readonly TimeSpan Lifetime = GitOAuthLimits.Lifetime;

    public const string CookieName = "aide.gitlab.link";

    private const string Purpose = "Aide.GitLab.PendingLink.v1";

    /// <summary>Holds a link and returns its cookie value.</summary>
    public (string CookieValue, GitLabPendingLink Link) Add(GitLabInstance instance, Guid offeredTo, string email, GitLabUser user, GitLabOAuthTokens tokens)
    {
        var link = new GitLabPendingLink(Guid.CreateVersion7(), instance, offeredTo, email, user, tokens, Time.GetUtcNow() + Lifetime);
        return (AddCore(link), link);
    }
}
