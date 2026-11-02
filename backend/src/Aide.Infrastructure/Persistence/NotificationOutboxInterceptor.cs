using System.Text.Json;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aide.Infrastructure.Persistence;

/// <summary>
/// FR-47, §4.9 outbox: whoever writes a notification-worthy project event (<see cref="NotificationRules.ForEvent(string, string, string)"/>), the
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
            .Where(e => e.State == EntityState.Added)
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
            // Project events notify their project; an event outside any project only notifies personally (FR-92: a GitHub
            // connection marked invalid tells its owner).
            var (agent, artifactType) = Subject(entry);
            if (NotificationRules.ForEvent(entry.Type, agent, artifactType) is not { } type || (entry.ProjectId is null && !NotificationRules.IsPersonal(type)))
            {
                continue;
            }

            var sourceId = NotificationRules.IsAboutGate(type) ? GateId(entry.Payload) : entry.Id;
            if (sourceId is not { } source)
            {
                continue;
            }

            var payload = AideJson.Serialize(new NotificationJobPayload(entry.ProjectId, type, source));
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

    /// <summary>
    /// FR-193: the <c>agent</c> of a <c>RUN_FAILED</c> and the artifact <c>type</c> of an <c>ARTIFACT_CREATED</c> or
    /// <c>ARTIFACT_RESTORED</c>, which pick the notification type; other payloads aren't read.
    /// </summary>
    private static (string? Agent, string? ArtifactType) Subject(AuditEvent entry)
    {
        if (entry.Type is not (EventTypes.RunFailed or EventTypes.ArtifactCreated or EventTypes.ArtifactRestored))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(entry.Payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return (Text("agent"), Text("type"));
        }
        catch (JsonException)
        {
            return (null, null);
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
