using System.ComponentModel.DataAnnotations;
using Aide.Domain.Access;
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

/// <summary>Which sign-in providers are available (the frontend hides GitHub when it isn't configured).</summary>
public sealed record AuthProvidersDto(bool Github);
