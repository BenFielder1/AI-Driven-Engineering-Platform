using System.Text.Json;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Entities;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Databases;

/// <summary>
/// FR-155, §4.21: finds databases about to go away and tells Owners once (<c>DATABASE_EXPIRING</c>): a database within 7 days of
/// its <c>expiresAt</c> (Render free) or a paused Supabase project. Each change (<see cref="ManagedDatabaseRules.ExpirySweep"/>) is a
/// conditional update of the stored status in one transaction with its event, so repeated sweeps, restarts and concurrent
/// pods act once. A database that came back warns again only for a new expiry: the same date is never announced twice.
/// </summary>
public sealed partial class DatabaseExpirySweepService(
    AideDbContext db,
    ManagedDatabaseService databases,
    IDatabaseProviderResolver providers,
    TimeProvider time,
    ILogger<DatabaseExpirySweepService> logger)
{
    /// <summary>Sweeps every database that may need a change; returns how many warned.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var rows = await db.ManagedDatabases.AsNoTracking()
            .Where(d => (d.Status == ManagedDatabaseStatus.Available || d.Status == ManagedDatabaseStatus.Expiring || d.Status == ManagedDatabaseStatus.Paused)
                && ((d.Provider == ManagedDatabaseProvider.Supabase && d.ResourceId != null) || d.ExpiresAt != null || d.Status == ManagedDatabaseStatus.Expiring))
            .OrderBy(d => d.Id)
            .ToListAsync(cancellationToken);
        var warned = 0;
        foreach (var row in rows)
        {
            var now = time.GetUtcNow();
            var live = await ReadLiveAsync(row, now, cancellationToken);
            if (ManagedDatabaseRules.ExpirySweep(row.Provider, row.Status, row.ExpiresAt, live, now) is { } change
                && await ApplyAsync(row, change, now, cancellationToken))
            {
                warned++;
            }
        }

        return warned;
    }

    /// <summary>
    /// The provider's word on the database: always for a Supabase project (only it knows about a pause); for an expiring one
    /// only near the date (the plan may have been upgraded). A failed read isn't news: <c>null</c>, and the stored expiry stands.
    /// </summary>
    private async Task<DatabaseLiveState?> ReadLiveAsync(ManagedDatabase row, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var asks = row.ResourceId is not null
            && (row.Provider == ManagedDatabaseProvider.Supabase || row.Status == ManagedDatabaseStatus.Expiring || ManagedDatabaseRules.IsExpiring(row.ExpiresAt, now));
        if (!asks)
        {
            return null;
        }

        var auth = await databases.AuthAsync(row, cancellationToken);
        if (auth.IsFailure)
        {
            LogNotRead(logger, row.Id, auth.Error!.Code);
            return null;
        }

        try
        {
            return await providers.ForHost(row.Provider).ReadStateAsync(auth.Value.Auth, row.ResourceId!, cancellationToken);
        }
        catch (DatabaseProviderException ex)
        {
            LogReadRefused(logger, row.Id, ex.Failure);
            return null;
        }
    }

    /// <summary>The conditional change with its event; <c>false</c> when nothing was announced (another sweep got there, or it is silent).</summary>
    private async Task<bool> ApplyAsync(ManagedDatabase row, DatabaseSweepChange change, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.ManagedDatabases
            .Where(d => d.Id == row.Id && d.Status == row.Status && d.ResourceId == row.ResourceId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(d => d.Status, change.Status).SetProperty(d => d.ExpiresAt, change.ExpiresAt).SetProperty(d => d.UpdatedAt, now),
                cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        var announces = change.ExpiringReason is not null
            && (change.ExpiringReason != ManagedDatabaseRules.ExpiringReasonExpiry || !await AnnouncedAsync(row, change.ExpiresAt, cancellationToken));
        if (announces)
        {
            // The outbox interceptor queues the database_expiring notification job with this event.
            db.AddEvent(
                EventTypes.DatabaseExpiring,
                row.ProjectId,
                new { databaseId = row.Id, environment = row.Environment.ToWire(), provider = row.Provider.ToWire(), reason = change.ExpiringReason, expiresAt = change.ExpiresAt },
                SystemActor.Name,
                now);
            await db.SaveChangesAsync(cancellationToken);
            LogWarned(logger, row.ProjectId, row.Id, change.ExpiringReason!);
        }

        await transaction.CommitAsync(cancellationToken);
        return announces;
    }

    /// <summary>Whether this expiry of this database was already announced (the row may have been linked again since).</summary>
    private async Task<bool> AnnouncedAsync(ManagedDatabase row, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var payloads = await db.Events.AsNoTracking()
            .Where(e => e.ProjectId == row.ProjectId && e.Type == EventTypes.DatabaseExpiring)
            .Select(e => e.Payload)
            .ToListAsync(cancellationToken);
        return payloads.Any(payload => SameExpiry(payload, row.Id, expiresAt));
    }

    private static bool SameExpiry(string payload, Guid databaseId, DateTimeOffset? expiresAt)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("databaseId", out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var announced) && announced == databaseId
                && root.TryGetProperty("expiresAt", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var date) && date == expiresAt;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database {DatabaseId} of project {ProjectId}: DATABASE_EXPIRING ({Reason})")]
    private static partial void LogWarned(ILogger logger, Guid projectId, Guid databaseId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database {DatabaseId}: its state couldn't be read for the expiry sweep ({Reason}); what is stored stands")]
    private static partial void LogNotRead(ILogger logger, Guid databaseId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database {DatabaseId}: the platform refused the expiry sweep's read ({Failure}); what is stored stands")]
    private static partial void LogReadRefused(ILogger logger, Guid databaseId, Aide.Domain.Deployments.DeploymentFailure failure);
}

/// <summary>FR-155: the <c>database_expiry_sweep</c> job. Idempotent: running it again changes nothing that already changed.</summary>
public sealed class DatabaseExpirySweepJobHandler(DatabaseExpirySweepService sweep) : IJobHandler
{
    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken) =>
        await sweep.RunAsync(cancellationToken);
}
