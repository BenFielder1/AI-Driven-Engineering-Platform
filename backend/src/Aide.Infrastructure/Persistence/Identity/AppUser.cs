using Aide.Domain.Access;
using Microsoft.AspNetCore.Identity;

namespace Aide.Infrastructure.Persistence.Identity;

/// <summary>An AIDE user (SPEC §5 <c>user</c>, table <c>users</c>) on top of ASP.NET Core Identity.
/// <see cref="IdentityUser{TKey}.Email"/> is <c>citext</c> with a unique index (case-insensitive uniqueness).</summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public AppUser()
    {
        Id = Guid.CreateVersion7();
        SecurityStamp = Guid.NewGuid().ToString();
    }

    public string Name { get; set; } = string.Empty;

    public SystemRole SystemRole { get; set; } = SystemRole.User;

    /// <summary>The linked GitHub account's numeric user id (FR-2), as text.</summary>
    public string? GithubId { get; set; }

    public string? AvatarUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
