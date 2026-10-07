using System.Security.Claims;
using Aide.Domain.Access;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The AIDE user id of an authenticated principal (cookie or API token), else <c>null</c>.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Identity?.IsAuthenticated == true
            && Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                ? id
                : null;
    }
}

/// <summary><see cref="ICurrentUser"/> for HTTP requests, from the authenticated principal.</summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId => accessor.HttpContext?.User.GetUserId();

    public bool IsSystem => false;
}

/// <summary>Reads a user's system role from the database (once per request), so role changes apply immediately
/// rather than when the cookie is next refreshed.</summary>
public sealed class SystemRoleLookup(AideDbContext db)
{
    private readonly Dictionary<Guid, SystemRole?> _cache = [];

    /// <summary>The user's system role, or <c>null</c> if the user no longer exists.</summary>
    public async Task<SystemRole?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        var role = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (SystemRole?)u.SystemRole)
            .FirstOrDefaultAsync(cancellationToken);
        _cache[userId] = role;
        return role;
    }
}
