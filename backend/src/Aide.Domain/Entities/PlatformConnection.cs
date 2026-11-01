namespace Aide.Domain.Entities;

/// <summary>
/// A user's pasted platform token (FR-142, §4.20, §4.21); table <c>platform_connections</c>, replacing v2's
/// <c>vercel_connections</c> (v2 rows were copied with the same id). Deployment targets bind one through
/// <see cref="DeploymentTarget.ConnectionId"/>. The token is Data Protection ciphertext (<c>SecretPurposes.PlatformToken</c>)
/// and is never returned or logged.
/// </summary>
public sealed class PlatformConnection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public PlatformProvider Provider { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Data Protection ciphertext of the token (<c>SecretPurposes.PlatformToken</c>).</summary>
    public string SecretEncrypted { get; set; } = string.Empty;

    /// <summary>The token's last 4 characters, shown as <c>••••a1b2</c>.</summary>
    public string Last4 { get; set; } = string.Empty;

    /// <summary>
    /// The account the token belongs to or was saved for: the Vercel user, Netlify user, Cloudflare account, Render user,
    /// Railway workspace or Supabase organisation; <c>null</c> when the platform didn't say.
    /// </summary>
    public string? AccountId { get; set; }

    public string? AccountName { get; set; }

    /// <summary>What else the token reaches and says about itself (teams, workspaces, orgs, token policies, missing permissions).</summary>
    public PlatformConnectionMetadata Metadata { get; set; } = new();

    /// <summary>The token's scopes when the platform reports them (empty otherwise).</summary>
    public IReadOnlyList<string> Scopes { get; set; } = [];

    /// <summary>When the token expires; null for a token without (known) expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public PlatformConnectionStatus Status { get; set; } = PlatformConnectionStatus.Unchecked;

    public DateTimeOffset? LastValidatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// <c>platform_connections.metadata</c> (FR-142): what a platform says about a token. <see cref="TeamId"/> and
/// <see cref="TeamSlug"/> are a team-scoped Vercel token's team (sent as <c>teamId</c> on every Vercel request);
/// <see cref="Accounts"/> the teams, accounts, workspaces or organisations it reaches; <see cref="Policies"/> a Cloudflare
/// token's policies (permission group names) where readable; <see cref="MissingPermissions"/> what the platform said is
/// missing (Supabase <c>missing_permissions</c>); <see cref="TokenKind"/> the kind of token when it matters (Railway
/// <c>workspace</c> or <c>account</c>).
/// </summary>
public sealed record PlatformConnectionMetadata
{
    public string? TeamId { get; init; }

    public string? TeamSlug { get; init; }

    public IReadOnlyList<PlatformAccountRef> Accounts { get; init; } = [];

    public IReadOnlyList<string> Policies { get; init; } = [];

    public IReadOnlyList<string> MissingPermissions { get; init; } = [];

    public string? TokenKind { get; init; }
}

/// <summary>A team, account, workspace or organisation a platform token reaches (FR-142).</summary>
public sealed record PlatformAccountRef(string Id, string Name, string? Slug = null, string? Kind = null);

/// <summary>Deployment platforms and databases with pasted-token connections (FR-142, §4.20, §4.21). Wire values:
/// <c>vercel</c>, <c>netlify</c>, <c>cloudflare</c>, <c>render</c>, <c>railway</c>, <c>supabase</c>.</summary>
public enum PlatformProvider
{
    Vercel = 0,
    Netlify = 1,
    Cloudflare = 2,
    Render = 3,
    Railway = 4,
    Supabase = 5,
}

/// <summary>Validation state of a platform connection (FR-142). Wire values: <c>valid</c>, <c>invalid</c> (rejected),
/// <c>insufficient_scope</c> (missing permissions), <c>unchecked</c>.</summary>
public enum PlatformConnectionStatus
{
    Valid = 0,
    Invalid = 1,
    InsufficientScope = 2,
    Unchecked = 3,
}

/// <summary>Scope of a Vercel token (FR-87). Wire values: <c>account</c>, <c>team</c>.</summary>
public enum VercelTokenScope
{
    Account = 0,
    Team = 1,
}
