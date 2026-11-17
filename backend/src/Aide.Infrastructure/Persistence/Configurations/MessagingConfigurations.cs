using Aide.Domain.Entities;
using Aide.Domain.Notifications;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Pack.Sdlc;

namespace Aide.Infrastructure.Persistence.Configurations;

/// <summary>D-0.7.0-12: <c>events</c> is configured by the engine (<see cref="TevrinEngineModel"/>); the host adds the
/// project relationship.</summary>
internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder) =>
        builder.HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
}

/// <summary>D-0.7.0-12: <c>jobs</c> is configured by the engine (<see cref="TevrinEngineModel"/>); the host adds the
/// project relationship and the SDLC's one-execution-job index.</summary>
internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.HasOne<Project>().WithMany().HasForeignKey(j => j.ProjectId).OnDelete(DeleteBehavior.Cascade);

        // §4.9: at most one execution job queued or running per project. Status values are otherwise
        // unconstrained (plain varchar), so new JobStatus members need no schema change.
        builder.HasIndex(j => j.ProjectId, "ux_jobs_project_id_active_execution")
            .IsUnique()
            .HasFilter($"type = '{SdlcJobTypes.Execution}' AND status IN ('queued', 'running')")
            .HasDatabaseName("ux_jobs_project_id_active_execution");
    }
}

/// <summary>D-0.7.0-12: <c>notifications</c> is configured by the engine (<see cref="TevrinEngineModel"/>); the host
/// adds the user and project relationships.</summary>
internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.HasOne<AppUser>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Project>().WithMany().HasForeignKey(n => n.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>D-0.7.0-12: <c>notification_deliveries</c> is configured by the engine (<see cref="TevrinEngineModel"/>); the
/// host adds the project relationship.</summary>
internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder) =>
        builder.HasOne<Project>().WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Cascade);
}

internal sealed class UserNotificationPreferencesConfiguration : IEntityTypeConfiguration<UserNotificationPreferences>
{
    public void Configure(EntityTypeBuilder<UserNotificationPreferences> builder)
    {
        builder.ToTable("user_notification_preferences");
        builder.HasKey(p => p.UserId);
        builder.Property(p => p.UserId).ValueGeneratedNever();
        // A text[] of snake_case wire values. Npgsql ignores element converters on enum collections (it would store
        // "GateReminder"), so the whole list is converted; filter by type in memory after loading the row.
        builder.Property(p => p.MutedTypes)
            .HasConversion(
                v => v.Select(t => t.ToWire()).ToArray(),
                v => v.Select(EnumStrings.Parse<NotificationType>).ToList(),
                new ValueComparer<List<NotificationType>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (h, t) => HashCode.Combine(h, t)),
                    v => v.ToList()))
            .HasColumnType("text[]")
            .HasDefaultValueSql("'{}'::text[]")
            .IsRequired();

        builder.HasOne<AppUser>().WithOne().HasForeignKey<UserNotificationPreferences>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>D-0.7.0-12: <c>approval_links</c> is configured by the engine (<see cref="TevrinEngineModel"/>); the host adds
/// the gate relationship.</summary>
internal sealed class ApprovalLinkConfiguration : IEntityTypeConfiguration<ApprovalLink>
{
    public void Configure(EntityTypeBuilder<ApprovalLink> builder) =>
        builder.HasOne<ApprovalGate>().WithMany().HasForeignKey(l => l.GateId).OnDelete(DeleteBehavior.Cascade);
}
