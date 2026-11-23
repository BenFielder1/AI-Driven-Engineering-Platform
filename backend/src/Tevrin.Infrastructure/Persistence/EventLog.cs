using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Infrastructure.Persistence.Conversion;

namespace Tevrin.Infrastructure.Persistence;

/// <summary>Appends audit events (table <c>events</c>) in the caller's unit of work, so an event is saved in the
/// same transaction as the change it records. Payloads are camelCase JSON and must never contain secret values.
/// Events added to one unit of work get strictly increasing timestamps (1 µs apart, PostgreSQL's precision) in the order
/// they were added, so feeds ordered by time show them in the order they happened.</summary>
public static class EventLog
{
    /// <summary>The smallest step PostgreSQL <c>timestamptz</c> stores (1 µs).</summary>
    public static readonly TimeSpan Step = TimeSpan.FromTicks(10);

    public static AuditEvent AddEvent(this TevrinDbContext db, string type, Guid? projectId, object payload, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(payload);
        var entry = new AuditEvent
        {
            ProjectId = projectId,
            Type = type,
            Payload = TevrinJson.Serialize(payload),
            Actor = actor,
            CreatedAt = NextTimestamp(db, now),
        };
        db.Events.Add(entry);
        return entry;
    }

    private static DateTimeOffset NextTimestamp(TevrinDbContext db, DateTimeOffset now)
    {
        var latest = db.ChangeTracker.Entries<AuditEvent>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => (DateTimeOffset?)e.Entity.CreatedAt)
            .Max();
        return latest is { } last && now < last + Step ? last + Step : now;
    }
}
