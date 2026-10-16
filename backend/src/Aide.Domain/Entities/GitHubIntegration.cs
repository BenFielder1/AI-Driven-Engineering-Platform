namespace Aide.Domain.Entities;

/// <summary>The AIDE GitHub App registered through the manifest flow (FR-80); table <c>github_apps</c>, at most one row
/// (a unique index on a constant generated column). <c>GitHub__App__*</c> configuration overrides it. Secrets are
/// Data Protection ciphertext and never returned.</summary>
public sealed class GitHubApp
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>GitHub's numeric App id (the JWT issuer).</summary>
    public long AppId { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string OwnerLogin { get; set; } = string.Empty;

    public GitHubOwnerType OwnerType { get; set; }

    public string ClientId { get; set; } = string.Empty;

    /// <summary>Ciphertext of the OAuth client secret (<c>SecretPurposes.GitHubAppClientSecret</c>).</summary>
    public string ClientSecretEncrypted { get; set; } = string.Empty;

    /// <summary>Ciphertext of the PEM private key (<c>SecretPurposes.GitHubAppPrivateKey</c>).</summary>
    public string PrivateKeyEncrypted { get; set; } = string.Empty;

    /// <summary>The App's granted permissions as returned by GitHub, e.g. <c>{"contents": "write"}</c>.</summary>
    public IReadOnlyDictionary<string, string> Permissions { get; set; } = new Dictionary<string, string>();

    public string? HtmlUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Owner kind of a GitHub App (FR-80). Wire values: <c>user</c>, <c>organization</c>.</summary>
public enum GitHubOwnerType
{
    User = 0,
    Organization = 1,
}

/// <summary>A user's GitHub connection (FR-81); table <c>github_connections</c>, one per user. The user-to-server tokens
/// are Data Protection ciphertext (<c>SecretPurposes.GitHubUserToken</c>), used only for the picker, repo creation and
/// identity (§4.14). Refreshes can race, so the row carries an <c>xmin</c> concurrency token.</summary>
public sealed class GitHubConnection
{
    public Guid UserId { get; set; }

    /// <summary>GitHub's numeric user id; unique (one AIDE user per GitHub account). Sign-in matches on
    /// <c>users.github_id</c>, which holds the same id as text.</summary>
    public long GithubUserId { get; set; }

    public string Login { get; set; } = string.Empty;

    public string AccessTokenEncrypted { get; set; } = string.Empty;

    /// <summary>Null when the App has token expiration turned off (the token doesn't expire).</summary>
    public DateTimeOffset? AccessExpiresAt { get; set; }

    /// <summary>Null when the App has token expiration turned off (GitHub issues no refresh token).</summary>
    public string? RefreshTokenEncrypted { get; set; }

    public DateTimeOffset? RefreshExpiresAt { get; set; }

    public GitHubConnectionStatus Status { get; set; } = GitHubConnectionStatus.Valid;

    public DateTimeOffset UpdatedAt { get; set; }

    public uint RowVersion { get; set; }
}

/// <summary>State of a GitHub connection (FR-81, §4.14: a failed refresh marks it invalid). Wire values: <c>valid</c>,
/// <c>invalid</c>.</summary>
public enum GitHubConnectionStatus
{
    Valid = 0,
    Invalid = 1,
}
