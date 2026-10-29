namespace Aide.Domain.GitHosts;

/// <summary>How a GitLab (or Bitbucket) OAuth flow was started (FR-131, FR-134). Wire values: <c>signin</c>, <c>connect</c>.</summary>
public enum GitOAuthMode
{
    /// <summary>"Continue with GitLab" on the sign-in page.</summary>
    Signin = 0,

    /// <summary>Account → Connections: connect (or re-connect) for the signed-in user.</summary>
    Connect = 1,
}

/// <summary>What the OAuth callback does with the host identity (FR-131, D-V4-18).</summary>
public enum GitSignInAction
{
    /// <summary>The identity is linked to an AIDE account: sign in as it (and store the connection).</summary>
    SignInLinked = 0,

    /// <summary>Link the identity to the signed-in user (connect, or sign-in while signed in) and store the connection.</summary>
    LinkToCurrentUser = 1,

    /// <summary>New identity, unknown verified email: create an account (first account Admin, FR-3), link it and sign in.</summary>
    CreateAccount = 2,

    /// <summary>
    /// The verified email belongs to an existing account that isn't linked: never link silently. The callback keeps a
    /// pending link; the user signs in to that account and confirms (<c>POST /api/auth/gitlab/link</c>).
    /// </summary>
    ConfirmEmailLink = 3,

    /// <summary>Refuse, with <see cref="GitSignInDecision.Reason"/> (redirected as <c>GIT_AUTH_FAILED</c>).</summary>
    Fail = 4,
}

/// <summary>The callback's decision; <see cref="UserId"/> is the account to sign in as or link to, when known.</summary>
public sealed record GitSignInDecision(GitSignInAction Action, Guid? UserId, string? Reason)
{
    public static GitSignInDecision Failed(string reason) => new(GitSignInAction.Fail, null, reason);
}

/// <summary>
/// FR-131, D-V4-18: the account rules of the GitLab (and Bitbucket) OAuth callback. Accounts match by
/// <c>(provider, instance, external user id)</c> only; an email match is linked only after the user signs in to that
/// account and confirms. Failure reasons: <c>link_session</c> (connect started by another or no session),
/// <c>already_linked</c> (the identity belongs to another account), <c>email_required</c> (no verified email to create
/// an account with). Pure.
/// </summary>
public static class GitSignInRules
{
    public const string LinkSession = "link_session";
    public const string AlreadyLinked = "already_linked";
    public const string EmailRequired = "email_required";

    /// <param name="mode">How the flow started.</param>
    /// <param name="startedByUserId">The signed-in user who started a connect flow (bound into the OAuth state).</param>
    /// <param name="currentUserId">The user signed in at the callback, if any.</param>
    /// <param name="linkedUserId">The account the identity is already linked to, if any.</param>
    /// <param name="verifiedEmail">The host's verified email for the identity, if any.</param>
    /// <param name="emailOwnerUserId">The AIDE account with <paramref name="verifiedEmail"/>, if any.</param>
    public static GitSignInDecision Decide(
        GitOAuthMode mode,
        Guid? startedByUserId,
        Guid? currentUserId,
        Guid? linkedUserId,
        string? verifiedEmail,
        Guid? emailOwnerUserId)
    {
        if (mode == GitOAuthMode.Connect)
        {
            if (startedByUserId is null || currentUserId != startedByUserId)
            {
                return GitSignInDecision.Failed(LinkSession);
            }

            return linkedUserId is { } owner && owner != currentUserId
                ? GitSignInDecision.Failed(AlreadyLinked)
                : new GitSignInDecision(GitSignInAction.LinkToCurrentUser, currentUserId, null);
        }

        if (linkedUserId is { } linked)
        {
            return new GitSignInDecision(GitSignInAction.SignInLinked, linked, null);
        }

        if (currentUserId is { } current)
        {
            return new GitSignInDecision(GitSignInAction.LinkToCurrentUser, current, null);
        }

        if (string.IsNullOrWhiteSpace(verifiedEmail))
        {
            return GitSignInDecision.Failed(EmailRequired);
        }

        return emailOwnerUserId is { } emailOwner
            ? new GitSignInDecision(GitSignInAction.ConfirmEmailLink, emailOwner, null)
            : new GitSignInDecision(GitSignInAction.CreateAccount, null, null);
    }

    /// <summary>
    /// FR-131: a pending email link is confirmed only by the account it was offered to, while it hasn't expired, and while
    /// the identity is still unlinked.
    /// </summary>
    public static bool CanConfirmEmailLink(Guid offeredToUserId, Guid? currentUserId, DateTimeOffset expiresAt, DateTimeOffset now, Guid? linkedUserId) =>
        currentUserId == offeredToUserId && expiresAt > now && (linkedUserId is null || linkedUserId == offeredToUserId);
}
