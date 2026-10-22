namespace Aide.Domain.Entities;

/// <summary>A user's Vercel access token (FR-87, D-V2-10); table <c>vercel_connections</c>. Projects bind one through
/// <see cref="ProjectDeploymentSettings.ConnectionId"/> (FR-88). The token is Data Protection ciphertext
/// (<c>SecretPurposes.VercelToken</c>) and is never returned.</summary>
public sealed class VercelConnection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Data Protection ciphertext of the token (<c>SecretPurposes.VercelToken</c>).</summary>
    public string TokenEncrypted { get; set; } = string.Empty;

    /// <summary>The token's last 4 characters, shown as <c>••••a1b2</c> (<see cref="LlmCredential.Last4Of"/>).</summary>
    public string Last4 { get; set; } = string.Empty;

    /// <summary>Vercel's user id (<c>GET /v2/user</c>).</summary>
    public string VercelUserId { get; set; } = string.Empty;

    /// <summary>Vercel's username for the account (<c>GET /v2/user</c> <c>user.username</c>), shown as the account name;
    /// null when Vercel didn't say.</summary>
    public string? VercelUsername { get; set; }

    /// <summary>The team the token is scoped to; required when <see cref="Scope"/> is <see cref="VercelTokenScope.Team"/>
    /// (a check constraint enforces it).</summary>
    public string? TeamId { get; set; }

    public string? TeamSlug { get; set; }

    public VercelTokenScope Scope { get; set; } = VercelTokenScope.Account;

    /// <summary>When the token expires; null for a token without expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public VercelConnectionStatus Status { get; set; } = VercelConnectionStatus.Unchecked;

    public DateTimeOffset? LastValidatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Scope of a Vercel token (FR-87). Wire values: <c>account</c>, <c>team</c>.</summary>
public enum VercelTokenScope
{
    Account = 0,
    Team = 1,
}

/// <summary>Validation state of a Vercel connection (FR-87: a token Vercel rejects marks it invalid). Wire values:
/// <c>valid</c>, <c>invalid</c>, <c>unchecked</c>.</summary>
public enum VercelConnectionStatus
{
    Valid = 0,
    Invalid = 1,
    Unchecked = 2,
}
