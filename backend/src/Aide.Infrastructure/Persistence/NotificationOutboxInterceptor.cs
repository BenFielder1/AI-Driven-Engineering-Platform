using System.Text.Json;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aide.Infrastructure.Persistence;

/// <summary>
/// FR-47, §4.9 outbox: whoever writes a notification-worthy project event (<see cref="NotificationRules.ForEvent"/>), the
/// same <c>SaveChanges</c> — the same transaction — also queues one <c>notification</c> job for it. Nothing is queued for a
/// save that fails or a transaction that rolls back, and a retried save doesn't queue a second job for the same event.
/// Delivery itself happens in the job, so a channel failure never fails the transaction that recorded the event.
/// </summary>
public sealed class NotificationOutboxInterceptor(TimeProvider time) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Enqueue(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Enqueue(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Enqueue(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var added = context.ChangeTracker.Entries<AuditEvent>()
            .Where(e => e.State == EntityState.Added && e.Entity.ProjectId is not null)
            .Select(e => e.Entity)
            .ToList();
        if (added.Count == 0)
        {
            return;
        }

        var queued = context.ChangeTracker.Entries<Job>()
            .Where(e => e.State == EntityState.Added && e.Entity.Type == JobTypes.Notification)
            .Select(e => e.Entity.Payload)
            .ToList();
        var now = time.GetUtcNow();
        foreach (var entry in added)
        {
            if (NotificationRules.ForEvent(entry.Type) is not { } type)
            {
                continue;
            }

            var sourceId = NotificationRules.IsAboutGate(type) ? GateId(entry.Payload) : entry.Id;
            if (sourceId is not { } source)
            {
                continue;
            }

            var payload = AideJson.Serialize(new NotificationJobPayload(entry.ProjectId!.Value, type, source));
            if (queued.Contains(payload, StringComparer.Ordinal))
            {
                continue;
            }

            queued.Add(payload);
            context.Set<Job>().Add(new Job
            {
                Type = JobTypes.Notification,
                Payload = payload,
                ProjectId = entry.ProjectId,
                Status = JobStatus.Queued,
                RunAfter = now,
                CreatedAt = now,
            });
        }
    }

    private static Guid? GateId(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("gateId", out var id)
                && id.TryGetGuid(out var gateId)
                    ? gateId
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
