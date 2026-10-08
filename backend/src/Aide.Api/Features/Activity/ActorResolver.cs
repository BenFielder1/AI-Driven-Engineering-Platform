using Aide.Domain.Access;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Activity;

/// <summary>
/// Resolves audit actors to display names (FR-50). Actors are stored as a user id string or <see cref="SystemActor.Name"/>;
/// a user id that no longer exists shows as "Deleted user".
/// </summary>
public static class ActorResolver
{
    public const string SystemDisplayName = "AIDE";
    public const string DeletedUserName = "Deleted user";

    public static async Task<Func<string, ActorDto>> ForActorsAsync(AideDbContext db, IEnumerable<string> actors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actors);
        var ids = actors.Select(a => Guid.TryParse(a, out var id) ? id : (Guid?)null).OfType<Guid>();
        var names = await NamesAsync(db, ids, cancellationToken);
        return actor => Guid.TryParse(actor, out var id)
            ? new ActorDto(id, names.GetValueOrDefault(id, DeletedUserName), false)
            : new ActorDto(null, actor == SystemActor.Name ? SystemDisplayName : actor, true);
    }

    public static async Task<Dictionary<Guid, string>> NamesAsync(AideDbContext db, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
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
