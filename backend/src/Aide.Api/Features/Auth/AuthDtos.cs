using System.ComponentModel.DataAnnotations;
using Aide.Domain.Access;
using Aide.Domain.GitHub;
using Aide.Infrastructure.Persistence.Identity;

namespace Aide.Api.Features.Auth;

/// <summary>FR-1 registration.</summary>
public sealed record RegisterRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name { get; init; } = string.Empty;

    /// <summary>At least 8 characters with a letter and a digit.</summary>
    [Required]
    [MinLength(8)]
    [MaxLength(128)]
    public string Password { get; init; } = string.Empty;
}

/// <summary>FR-1 sign-in.</summary>
public sealed record LoginRequest
{
    [Required]
    [MaxLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string Password { get; init; } = string.Empty;
}

/// <summary>The signed-in user (or a user, for admins).</summary>
public sealed record UserDto(
    Guid Id,
    string Email,
    string Name,
    SystemRole SystemRole,
    string? AvatarUrl,
    bool GithubLinked)
{
    public static UserDto From(AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserDto(user.Id, user.Email ?? string.Empty, user.Name, user.SystemRole, user.AvatarUrl, user.GithubId is not null);
    }
}

/// <summary>
/// Which sign-in providers are available (the frontend hides "Continue with GitHub" when <c>github</c> is false).
/// <c>githubMode</c> (D-V2-7) says how GitHub sign-in works: <c>app</c> (the AIDE GitHub App's user authorization flow
/// with PKCE), <c>oauth</c> (the v1 OAuth App, only while no App is registered) or <c>none</c>; <c>github</c> is
/// <c>githubMode != none</c>. <c>gitlab</c> (FR-131) lists the enabled GitLab instances with an OAuth application: one
/// "Continue with GitLab" button each (<c>GET /api/auth/gitlab/start</c> with <c>instanceId</c> and <c>mode=signin</c>).
/// <c>bitbucket</c> (FR-134) is true when a Bitbucket OAuth consumer is configured: "Continue with Bitbucket"
/// (<c>GET /api/auth/bitbucket/start?mode=signin</c>) and OAuth connect are offered.
/// </summary>
public sealed record AuthProvidersDto(bool Github, GitHubSignInMode GithubMode, IReadOnlyList<GitLab.GitLabInstanceSummaryDto> Gitlab, bool Bitbucket);
