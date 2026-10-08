using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence.Conversion;

namespace Aide.Infrastructure.Persistence;

/// <summary>Appends audit events (table <c>events</c>) in the caller's unit of work, so an event is saved in the
/// same transaction as the change it records. Payloads are camelCase JSON and must never contain secret values.</summary>
public static class EventLog
{
    public static AuditEvent AddEvent(this AideDbContext db, string type, Guid? projectId, object payload, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(payload);
        var entry = new AuditEvent
        {
            ProjectId = projectId,
            Type = type,
            Payload = AideJson.Serialize(payload),
            Actor = actor,
            CreatedAt = now,
        };
        db.Events.Add(entry);
        return entry;
    }
}
