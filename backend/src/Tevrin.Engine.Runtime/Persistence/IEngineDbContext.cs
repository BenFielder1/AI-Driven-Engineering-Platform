using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Runtime.Persistence;

/// <summary>
/// D-0.7.0-12, D-0.7.0-20: the engine's view of the host's one DbContext: the engine tables (configured by
/// <see cref="TevrinEngineModel"/>) and the unit of work. The host's DbContext implements it and is registered as it, so
/// the engine and the host share one scoped instance and one transaction.
/// </summary>
public interface IEngineDbContext
{
    DbSet<Job> Jobs { get; }

    DbSet<AuditEvent> Events { get; }

    DbSet<Notification> Notifications { get; }

    DbSet<NotificationDelivery> NotificationDeliveries { get; }

    DbSet<ApprovalLink> ApprovalLinks { get; }

    DatabaseFacade Database { get; }

    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
