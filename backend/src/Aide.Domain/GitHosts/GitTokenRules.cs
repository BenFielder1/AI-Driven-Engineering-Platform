namespace Aide.Domain.GitHosts;

/// <summary>How a GitLab or Bitbucket credential authenticates Git over HTTPS (FR-136).</summary>
public enum GitCredentialKind
{
    /// <summary>A GitLab OAuth access token or access token.</summary>
    Gitlab = 0,

    /// <summary>A Bitbucket OAuth access token.</summary>
    BitbucketOauth = 1,

    /// <summary>An Atlassian API token with scopes.</summary>
    BitbucketApiToken = 2,
}

/// <summary>What to do after the host rejects a token (FR-131, FR-134).</summary>
public enum GitTokenFailureAction
{
    /// <summary>Refresh the OAuth token once (storing the rotated refresh token atomically) and retry.</summary>
    Refresh = 0,

    /// <summary>Mark the connection <c>invalid</c> and notify its owner (v2 <c>connection_invalid</c>).</summary>
    MarkInvalid = 1,

    /// <summary>A transient failure (host error, network): keep the connection's status and retry the job.</summary>
    Retry = 2,
}

/// <summary>
/// FR-131, FR-134, FR-136: GitLab and Bitbucket token rules: the PAT expiry warning, when OAuth access tokens are refreshed,
/// when a connection becomes <c>invalid</c>, the Git <c>http.extraHeader</c> user name, and the scopes a token needs. Pure.
/// </summary>
public static class GitTokenRules
{
    /// <summary>FR-131: AIDE warns this long before a GitLab access token's <c>expires_at</c>.</summary>
    public static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(14);

    /// <summary>FR-131, FR-134: GitLab and Bitbucket OAuth access tokens last 2 hours.</summary>
    public static readonly TimeSpan OauthAccessLifetime = TimeSpan.FromHours(2);

    /// <summary>FR-131, FR-136: an access token is refreshed (per job) when it expires within this margin.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>FR-131: the scopes a GitLab access token needs.</summary>
    public static IReadOnlyList<string> GitLabTokenScopes { get; } = ["api", "write_repository"];

    /// <summary>FR-131: the GitLab OAuth scopes AIDE requests.</summary>
    public static IReadOnlyList<string> GitLabOauthScopes { get; } = ["api", "write_repository", "openid", "profile", "email"];

    /// <summary>FR-134: the scopes an Atlassian API token needs (all <c>:bitbucket</c>).</summary>
    public static IReadOnlyList<string> BitbucketTokenScopes { get; } =
    [
        "read:user:bitbucket", "read:workspace:bitbucket", "read:project:bitbucket", "read:repository:bitbucket",
        "write:repository:bitbucket", "admin:repository:bitbucket", "read:pullrequest:bitbucket", "write:pullrequest:bitbucket",
        "read:pipeline:bitbucket",
    ];

    /// <summary>
    /// FR-131: the expiry warning shows from 14 days before <paramref name="expiresAt"/> until it passes (no expiry: never).
    /// </summary>
    public static bool ShouldWarnExpiry(DateTimeOffset? expiresAt, DateTimeOffset now) =>
        expiresAt is { } expires && expires > now && expires - now <= ExpiryWarningWindow;

    /// <summary>The token's own expiry has passed.</summary>
    public static bool IsExpired(DateTimeOffset? expiresAt, DateTimeOffset now) => expiresAt is { } expires && expires <= now;

    /// <summary>
    /// FR-131, FR-136: an OAuth access token is refreshed before a job when it expires within five minutes (or has
    /// expired). Tokens without an access expiry (PATs, API tokens) are never refreshed.
    /// </summary>
    public static bool NeedsRefresh(DateTimeOffset? accessExpiresAt, DateTimeOffset now) =>
        accessExpiresAt is { } expires && expires - now <= RefreshMargin;

    /// <summary>The access token's expiry from the token response's <c>expires_in</c> (seconds; 2 hours when absent).</summary>
    public static DateTimeOffset AccessExpiresAt(DateTimeOffset issuedAt, long? expiresInSeconds) =>
        issuedAt + (expiresInSeconds is > 0 and var seconds ? TimeSpan.FromSeconds(Math.Min(seconds, 86_400)) : OauthAccessLifetime);

    /// <summary>
    /// FR-131, FR-134: after a 401 from the host: an OAuth connection that hasn't refreshed in this job refreshes once;
    /// a 401 after that refresh, or for a token that can't be refreshed, marks the connection <c>invalid</c>.
    /// </summary>
    public static GitTokenFailureAction OnUnauthorized(bool canRefresh, bool refreshedThisJob) =>
        canRefresh && !refreshedThisJob ? GitTokenFailureAction.Refresh : GitTokenFailureAction.MarkInvalid;

    /// <summary>
    /// FR-131, FR-134: a failed refresh: the token endpoint rejecting the grant (400 <c>invalid_grant</c>, 401) marks the
    /// connection <c>invalid</c>; 429, 5xx and network failures (<paramref name="status"/> <c>null</c>) are retried.
    /// </summary>
    public static GitTokenFailureAction OnRefreshFailed(int? status, string? oauthError) => status switch
    {
        null => GitTokenFailureAction.Retry,
        429 or >= 500 => GitTokenFailureAction.Retry,
        400 or 401 or 403 => GitTokenFailureAction.MarkInvalid,
        _ when string.Equals(oauthError, "invalid_grant", StringComparison.Ordinal) => GitTokenFailureAction.MarkInvalid,
        _ => GitTokenFailureAction.Retry,
    };

    /// <summary>
    /// FR-136: the user name of the Basic <c>http.extraHeader</c> for Git over HTTPS (the password is the token):
    /// GitLab <c>oauth2</c>, Bitbucket OAuth <c>x-token-auth</c>, Bitbucket API tokens <c>x-bitbucket-api-token-auth</c>.
    /// </summary>
    public static string GitBasicUser(GitCredentialKind kind) => kind switch
    {
        GitCredentialKind.Gitlab => "oauth2",
        GitCredentialKind.BitbucketOauth => "x-token-auth",
        GitCredentialKind.BitbucketApiToken => "x-bitbucket-api-token-auth",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>FR-131: the GitLab token scopes missing from <paramref name="granted"/>, in the required order.</summary>
    public static IReadOnlyList<string> MissingGitLabScopes(IEnumerable<string> granted) => Missing(GitLabTokenScopes, granted);

    /// <summary>FR-134: the Bitbucket API token scopes missing from <paramref name="granted"/>, in the required order.</summary>
    public static IReadOnlyList<string> MissingBitbucketScopes(IEnumerable<string> granted) => Missing(BitbucketTokenScopes, granted);

    private static List<string> Missing(IEnumerable<string> required, IEnumerable<string> granted)
    {
        ArgumentNullException.ThrowIfNull(granted);
        var have = granted.Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
        return [.. required.Where(s => !have.Contains(s))];
    }
}
