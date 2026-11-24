using Microsoft.EntityFrameworkCore;
using Tevrin.Domain.Access;
using Tevrin.Infrastructure.Persistence;

namespace Tevrin.Api.Features.Activity;

/// <summary>
/// Resolves audit actors to display names (FR-50). Actors are stored as a user id string or <see cref="SystemActor.Name"/>;
/// a user id that no longer exists shows as "Deleted user".
/// </summary>
public static class ActorResolver
{
    public const string SystemDisplayName = "Tevrin";
    public const string DeletedUserName = "Deleted user";

    public static async Task<Func<string, ActorDto>> ForActorsAsync(TevrinDbContext db, IEnumerable<string> actors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actors);
        var ids = actors.Select(a => Guid.TryParse(a, out var id) ? id : (Guid?)null).OfType<Guid>();
        var names = await NamesAsync(db, ids, cancellationToken);
        return actor => Guid.TryParse(actor, out var id)
            ? new ActorDto(id, names.GetValueOrDefault(id, DeletedUserName), false)
            : new ActorDto(null, SystemActor.IsSystemName(actor) ? SystemDisplayName : actor, true);
    }

    public static async Task<Dictionary<Guid, string>> NamesAsync(TevrinDbContext db, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken);
    }
}
