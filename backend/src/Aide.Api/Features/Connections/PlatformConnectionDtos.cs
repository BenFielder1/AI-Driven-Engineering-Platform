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
/// <c>workspace</c> or <c>account</c>, Cloudflare <c>user</c> (<c>null</c> when it doesn't matter).
/// </summary>
public sealed record PlatformConnectionMetadataDto(
    string? TeamId,
    string? TeamSlug,
    IReadOnlyList<PlatformAccountDto> Accounts,
    IReadOnlyList<string> Policies,
    IReadOnlyList<string> MissingPermissions,
    string? TokenKind);

/// <summary>
/// One of the caller's platform connections (FR-142). The token is never returned: only the label and its last 4
/// characters. <c>accountId</c>/<c>accountName</c> are the account it is for (the Vercel or Netlify user or team, Cloudflare
/// account, Render workspace, Railway workspace, Supabase organisation; <c>null</c> when not chosen or unknown),
/// <c>metadata</c> what else the token reaches, <c>scopes</c> its scopes where the platform reports them, <c>expiresAt</c>
/// its expiry (<c>null</c>: none or unknown), <c>status</c> (<c>valid</c>, <c>invalid</c>, <c>insufficient_scope</c>,
/// <c>unchecked</c>) and when it was last checked, plus the projects whose deployment targets are bound to it.
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
    IReadOnlyList<PlatformBoundProjectDto> BoundProjects);

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
}

/// <summary>Rename a platform connection (FR-142).</summary>
public sealed record UpdatePlatformConnectionRequest
{
    [Required]
    [MaxLength(PlatformConnectionRules.MaxLabelLength)]
    public required string Label { get; init; }
}

/// <summary>
/// An entry of a platform picker (FR-142 <c>GET …/resources</c>): the platform's id (send it as the resource to link), its
/// name, URL when there is one, and a short detail (e.g. the linked repository, the service type, a project's region and
/// status).
/// </summary>
public sealed record PlatformResourceDto(string Id, string Name, string? Url, string? Detail);
