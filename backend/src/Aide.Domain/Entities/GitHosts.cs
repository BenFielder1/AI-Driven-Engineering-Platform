namespace Aide.Domain.Entities;

/// <summary>A GitLab instance (FR-130) or the Bitbucket Cloud OAuth consumer (FR-133); table <c>git_host_instances</c>.
/// Unique by (kind, base URL), with a NULL base URL (gitlab.com, Bitbucket Cloud) counted as one value. Environment
/// configuration (<c>GitLab__Instances__*</c>, <c>Bitbucket__OAuth__*</c>) overrides a row with the same base URL. The
/// client secret is Data Protection ciphertext and never returned.</summary>
public sealed class GitHostInstance
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public GitHostKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>FR-130: the self-managed GitLab base URL (normalised, no trailing slash); null for gitlab.com and for
    /// Bitbucket Cloud.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The OAuth application id (GitLab) or consumer key (Bitbucket).</summary>
    public string OauthClientId { get; set; } = string.Empty;

    /// <summary>Ciphertext of the OAuth client secret (FR-130, FR-133).</summary>
    public string OauthClientSecretEncrypted { get; set; } = string.Empty;

    /// <summary>FR-130: allows an <c>http://</c> base URL (lab hosts only).</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>FR-130: a disabled instance stops its connections working.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>FR-130: the GitLab version from <c>GET /api/v4/version</c>, when known.</summary>
    public string? Version { get; set; }

    /// <summary>The Admin who added the instance; null once that user is deleted.</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Kind of Git host instance, and the provider of a <see cref="GitConnection"/> (FR-130, FR-133). Wire values:
/// <c>gitlab</c>, <c>bitbucket</c>.</summary>
public enum GitHostKind
{
    Gitlab = 0,
    Bitbucket = 1,
}

/// <summary>A provider identity linked to an AIDE user, used for sign-in matching (FR-131, FR-134, D-V4-18); table
/// <c>user_external_logins</c>. Unique by (provider, instance, external user id), with a NULL instance (GitHub, or a removed
/// instance) counted as one value. Existing <c>users.github_id</c> values are backfilled as
/// <see cref="ExternalLoginProvider.Github"/> rows; <c>users.github_id</c> stays for v2 code paths.</summary>
public sealed class UserExternalLogin
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public ExternalLoginProvider Provider { get; set; }

    /// <summary>The <see cref="GitHostInstance"/> for GitLab and Bitbucket; null for GitHub. Cleared (ON DELETE SET NULL)
    /// when the instance is removed.</summary>
    public Guid? InstanceId { get; set; }

    /// <summary>The provider's stable user id (GitLab numeric id, Bitbucket account UUID, GitHub numeric id), as text.</summary>
    public string ExternalUserId { get; set; } = string.Empty;

    public string Login { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Sign-in identity provider (FR-131, FR-134). Wire values: <c>github</c>, <c>gitlab</c>, <c>bitbucket</c>.</summary>
public enum ExternalLoginProvider
{
    Github = 0,
    Gitlab = 1,
    Bitbucket = 2,
}

/// <summary>A user's GitLab (FR-131) or Bitbucket (FR-134) connection on one instance; table <c>git_connections</c>.
/// Tokens are Data Protection ciphertext, never returned, logged or given to an LLM. GitLab invalidates the old token pair
/// on refresh, so refresh rotation is concurrency-checked with an <c>xmin</c> token.</summary>
public sealed class GitConnection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public GitHostKind Provider { get; set; }

    /// <summary>The <see cref="GitHostInstance"/>; it can't be deleted while connections use it (ON DELETE RESTRICT).</summary>
    public Guid InstanceId { get; set; }

    public GitConnectionAuthKind AuthKind { get; set; }

    /// <summary>The provider's stable user id, as text.</summary>
    public string ExternalUserId { get; set; } = string.Empty;

    public string Login { get; set; } = string.Empty;

    /// <summary>Ciphertext of the OAuth access token or the access/API token.</summary>
    public string AccessTokenEncrypted { get; set; } = string.Empty;

    /// <summary>OAuth access token expiry (2 h); null for tokens.</summary>
    public DateTimeOffset? AccessExpiresAt { get; set; }

    /// <summary>Ciphertext of the OAuth refresh token; null for tokens. Rotated on every refresh (FR-131, FR-134).</summary>
    public string? RefreshTokenEncrypted { get; set; }

    /// <summary>FR-134: ciphertext of the Atlassian account email that goes with a Bitbucket API token.</summary>
    public string? TokenEmailEncrypted { get; set; }

    /// <summary>Granted scopes (OAuth scopes, or a token's scopes as validated).</summary>
    public IReadOnlyList<string> Scopes { get; set; } = [];

    /// <summary>FR-131: the access token's own expiry (GitLab PAT <c>expires_at</c>); AIDE warns 14 days before it.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public GitConnectionStatus Status { get; set; } = GitConnectionStatus.Valid;

    public DateTimeOffset? LastValidatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint RowVersion { get; set; }
}

/// <summary>How a <see cref="GitConnection"/> authenticates (FR-131, FR-134). Wire values: <c>oauth</c>, <c>token</c>.</summary>
public enum GitConnectionAuthKind
{
    Oauth = 0,
    Token = 1,
}

/// <summary>State of a <see cref="GitConnection"/> (FR-131, FR-134). Wire values: <c>valid</c>, <c>invalid</c>,
/// <c>insufficient_scope</c>.</summary>
public enum GitConnectionStatus
{
    Valid = 0,
    Invalid = 1,
    InsufficientScope = 2,
}
