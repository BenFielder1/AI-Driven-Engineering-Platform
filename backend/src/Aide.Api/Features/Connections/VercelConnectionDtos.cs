using System.ComponentModel.DataAnnotations;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;

namespace Aide.Api.Features.Connections;

/// <summary>A project that has a Vercel connection bound for deployment (FR-88).</summary>
public sealed record VercelBoundProjectDto(Guid Id, string Name);

/// <summary>
/// One of the caller's Vercel connections (FR-87). The token is never returned: only the label, its last 4 characters,
/// the Vercel account (<c>accountName</c>, the Vercel username; <c>null</c> when unknown) and, for a team-scoped token,
/// <c>teamId</c>/<c>teamSlug</c>, the <c>scope</c> (<c>account</c> or <c>team</c>), the token's expiry (<c>null</c>: none),
/// the validation status (<c>valid</c>, <c>invalid</c>, <c>unchecked</c>) and when it was last checked, plus the projects
/// it is bound to.
/// </summary>
public sealed record VercelConnectionDto(
    Guid Id,
    string Label,
    string Last4,
    string? AccountName,
    string? TeamId,
    string? TeamSlug,
    VercelTokenScope Scope,
    DateTimeOffset? ExpiresAt,
    VercelConnectionStatus Status,
    DateTimeOffset? LastValidatedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<VercelBoundProjectDto> BoundProjects);

/// <summary>Add a Vercel connection (FR-87): a label and an access token (team- or account-scoped, ideally with an expiry).
/// The token is checked with Vercel before anything is saved.</summary>
public sealed record CreateVercelConnectionRequest
{
    [Required]
    [MaxLength(VercelConnectionRules.MaxLabelLength)]
    public required string Label { get; init; }

    [Required]
    [MaxLength(VercelConnectionRules.MaxTokenLength)]
    public required string Token { get; init; }
}

/// <summary>Rename a Vercel connection (FR-87).</summary>
public sealed record UpdateVercelConnectionRequest
{
    [Required]
    [MaxLength(VercelConnectionRules.MaxLabelLength)]
    public required string Label { get; init; }
}
