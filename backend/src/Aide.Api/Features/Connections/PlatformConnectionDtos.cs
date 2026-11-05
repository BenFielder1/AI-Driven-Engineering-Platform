using System.ComponentModel.DataAnnotations;
using Aide.Domain.Entities;
using Aide.Domain.Platforms;

namespace Aide.Api.Features.Connections;

/// <summary>A project whose deployment target is bound to a platform connection (FR-144).</summary>
public sealed record PlatformBoundProjectDto(Guid Id, string Name);

/// <summary>
/// A team, account, workspace or organisation a platform token reaches (FR-142): its id (send it as <c>accountId</c>),
/// name, slug (Vercel teams, Netlify accounts, Supabase organisations) and kind (e.g. <c>team</c>, <c>user</c>,
/// <c>workspace</c>, <c>organization</c>; <c>null</c> when the platform doesn't say).
/// </summary>
public sealed record PlatformAccountDto(string Id, string Name, string? Slug, string? Kind);

/// <summary>
/// What the platform says about a token (FR-142). <c>teamId</c>/<c>teamSlug</c>: a team-scoped Vercel token's team.
/// <c>accounts</c>: the teams, accounts, workspaces or organisations it reaches. <c>policies</c>: a Cloudflare token's
/// permission groups (empty when the token can't read them). <c>missingPermissions</c>: what the platform said is missing
/// (Supabase <c>missing_permissions</c>; non-empty with status <c>insufficient_scope</c>). <c>tokenKind</c>: e.g. Railway
/// <c>workspace</c> or <c>account</c>, Cloudflare <c>user</c> (<c>null</c> when it doesn't matter). FR-165, FR-166: <c>site</c>, <c>cloudId</c> and
/// <c>acceptanceCriteriaField</c> for Jira; <c>organization</c> and <c>organizationUrlKey</c> for Linear.
/// </summary>
public sealed record PlatformConnectionMetadataDto(
    string? TeamId,
    string? TeamSlug,
    IReadOnlyList<PlatformAccountDto> Accounts,
    IReadOnlyList<string> Policies,
    IReadOnlyList<string> MissingPermissions,
    string? TokenKind,
    string? Site = null,
    string? CloudId = null,
    string? AcceptanceCriteriaField = null,
    string? Organization = null,
    string? OrganizationUrlKey = null);

/// <summary>
/// One of the caller's platform connections (FR-142). The token is never returned: only the label and its last 4
/// characters. <c>accountId</c>/<c>accountName</c> are the account it is for (the Vercel or Netlify user or team, Cloudflare
/// account, Render workspace, Railway workspace, Supabase organisation; <c>null</c> when not chosen or unknown),
/// <c>metadata</c> what else the token reaches, <c>scopes</c> its scopes where the platform reports them, <c>expiresAt</c>
/// its expiry (<c>null</c>: none or unknown), <c>status</c> (<c>valid</c>, <c>invalid</c>, <c>insufficient_scope</c>,
/// <c>unchecked</c>) and when it was last checked, plus the projects whose deployment targets are bound to it. <c>expiryWarning</c>: the token
/// expires within 14 days (FR-165).
/// </summary>
public sealed record PlatformConnectionDto(
    Guid Id,
    PlatformProvider Provider,
    string Label,
    string Last4,
    string? AccountId,
    string? AccountName,
    PlatformConnectionMetadataDto Metadata,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    PlatformConnectionStatus Status,
    DateTimeOffset? LastValidatedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PlatformBoundProjectDto> BoundProjects,
    bool ExpiryWarning = false);

/// <summary>
/// Add a platform connection (FR-142, §4.20): the provider, a label and the pasted token, plus optionally the account it
/// is for (<c>accountId</c>: a team, account, workspace or organisation id the token reaches; required for a Railway
/// workspace token, which can't name its workspace itself). The token is checked with the platform before anything is
/// saved.
/// </summary>
public sealed record CreatePlatformConnectionRequest
{
    public required PlatformProvider Provider { get; init; }

    [Required]
    [MaxLength(PlatformConnectionRules.MaxLabelLength)]
    public required string Label { get; init; }

    [Required]
    [MaxLength(PlatformConnectionRules.MaxTokenLength)]
    public required string Token { get; init; }

    [MaxLength(PlatformConnectionRules.MaxAccountIdLength)]
    public string? AccountId { get; init; }

    /// <summary>FR-165 Jira: the site, e.g. <c>acme.atlassian.net</c> (else 400 <c>TRACKER_SITE_NOT_ALLOWED</c>, nothing fetched).</summary>
    [MaxLength(300)]
    public string? Site { get; init; }

    /// <summary>FR-165 Jira: the Atlassian account email; stored encrypted with the token.</summary>
    [MaxLength(Domain.Trackers.TrackerRules.MaxEmailLength)]
    public string? Email { get; init; }

    /// <summary>FR-165: Jira and Linear don't report token expiry, so the dialog asks. Tevrin warns 14 days before it.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>FR-165 Jira: the custom field holding acceptance criteria, from <c>GET …/resources?type=fields</c>.</summary>
    [MaxLength(Domain.Trackers.TrackerRules.MaxFieldIdLength)]
    public string? AcceptanceCriteriaField { get; init; }
}

/// <summary>
/// Rename a platform connection (FR-142). FR-165: on Jira and Linear, set the expiry (<c>clearExpiresAt</c> removes it) and, on Jira, the
/// acceptance criteria field (an empty string clears it). Omitted fields are unchanged; at least one is needed.
/// </summary>
public sealed record UpdatePlatformConnectionRequest
{
    [MaxLength(PlatformConnectionRules.MaxLabelLength)]
    public string? Label { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public bool ClearExpiresAt { get; init; }

    [MaxLength(Domain.Trackers.TrackerRules.MaxFieldIdLength)]
    public string? AcceptanceCriteriaField { get; init; }
}

/// <summary>
/// An entry of a platform picker (FR-142 <c>GET …/resources</c>): the platform's id (send it as the resource to link), its
/// name, URL when there is one, and a short detail (e.g. the linked repository, the service type, a project's region and
/// status).
/// </summary>
public sealed record PlatformResourceDto(string Id, string Name, string? Url, string? Detail);
