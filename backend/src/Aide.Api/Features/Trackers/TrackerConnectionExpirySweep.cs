using System.Text.Json;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Trackers;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Trackers;

/// <summary>
/// FR-165: Jira and Linear don't report token expiry, so their connections carry the date the user entered. Every
/// <c>Notifications:ConnectionExpirySweepInterval</c> the notification loop runs this sweep: each connection that
/// <see cref="TrackerRules.WarnsOfExpiry"/> gets one <c>CONNECTION_EXPIRING</c> event (project_id null), and the outbox queues its
/// owner's personal <c>connection_invalid</c> notification (in-app and account email, unless muted), worded as a warning.
/// <para>
/// Idempotent: one warning per connection and expiry date. The check and the insert run in one transaction holding an advisory
/// lock, so repeated sweeps, restarts and concurrent pods warn once. A new expiry date warns again. No provider is called.
/// </para>
/// </summary>
public sealed partial class TrackerConnectionExpirySweepService(AideDbContext db, TimeProvider time, ILogger<TrackerConnectionExpirySweepService> logger)
{
    /// <summary>The transaction-scoped advisory lock of the sweep ("AIDE" 0002).</summary>
    public const long LockKey = 0x41_49_44_45_00_02;

    /// <summary>Warns every connection due a warning; returns how many were warned.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var horizon = now + TrackerRules.ExpiryWarningWindow;
        var providers = TrackerRules.ConnectionProviders.ToList();
        var due = await db.PlatformConnections.AsNoTracking()
            .Where(c => providers.Contains(c.Provider) && c.ExpiresAt != null && c.ExpiresAt > now && c.ExpiresAt <= horizon && c.Status != PlatformConnectionStatus.Invalid)
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.UserId, c.Provider, c.Status, c.ExpiresAt })
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);
        var warned = (await db.Events.AsNoTracking()
                .Where(e => e.ProjectId == null && e.Type == EventTypes.ConnectionExpiring)
                .Select(e => e.Payload)
                .ToListAsync(cancellationToken))
            .Select(Warned)
            .OfType<(Guid ConnectionId, DateTimeOffset ExpiresAt)>()
            .ToHashSet();

        var count = 0;
        foreach (var connection in due)
        {
            if (!TrackerRules.WarnsOfExpiry(connection.Provider, connection.Status, connection.ExpiresAt, now, warned.Contains((connection.Id, connection.ExpiresAt!.Value))))
            {
                continue;
            }

            // The outbox interceptor queues the owner's connection_invalid notification job with this event.
            db.AddEvent(
                EventTypes.ConnectionExpiring,
                projectId: null,
                new { connectionId = connection.Id, userId = connection.UserId, provider = connection.Provider.ToWire(), expiresAt = connection.ExpiresAt },
                SystemActor.Name,
                now);
            LogWarned(logger, connection.Id, connection.Provider, connection.ExpiresAt.Value);
            count++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    /// <summary>The connection and date a <c>CONNECTION_EXPIRING</c> payload warned of, or <c>null</c> when unreadable.</summary>
    private static (Guid ConnectionId, DateTimeOffset ExpiresAt)? Warned(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("connectionId", out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var connectionId)
                && root.TryGetProperty("expiresAt", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var expiresAt)
                    ? (connectionId, expiresAt)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection {ConnectionId} ({Provider}): CONNECTION_EXPIRING, its token expires at {ExpiresAt}")]
    private static partial void LogWarned(ILogger logger, Guid connectionId, PlatformProvider provider, DateTimeOffset expiresAt);
}
