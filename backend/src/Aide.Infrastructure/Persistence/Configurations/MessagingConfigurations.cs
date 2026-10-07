using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aide.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Type).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.Actor).HasMaxLength(200).IsRequired();

        builder.HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.ProjectId, e.CreatedAt });
        builder.HasIndex(e => e.Type);
    }
}

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.Type).HasMaxLength(100).IsRequired();
        builder.Property(j => j.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(j => j.LockedBy).HasMaxLength(200);

        builder.HasOne<Project>().WithMany().HasForeignKey(j => j.ProjectId).OnDelete(DeleteBehavior.Cascade);

        // Claim query: WHERE status = 'queued' AND run_after <= now ORDER BY run_after FOR UPDATE SKIP LOCKED.
        builder.HasIndex(j => new { j.Status, j.RunAfter });

        // §4.9: at most one execution job queued or running per project. Status values are otherwise
        // unconstrained (plain varchar), so new JobStatus members need no schema change.
        builder.HasIndex(j => j.ProjectId, "ux_jobs_project_id_active_execution")
            .IsUnique()
            .HasFilter($"type = '{Job.ExecutionType}' AND status IN ('queued', 'running')")
            .HasDatabaseName("ux_jobs_project_id_active_execution");

        // Plain FK index (the partial one above doesn't serve the project cascade or per-project lookups).
        builder.HasIndex(j => j.ProjectId, "ix_jobs_project_id");
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.Type).HasMaxLength(100).IsRequired();
        builder.Property(n => n.Title).HasMaxLength(300).IsRequired();
        builder.Property(n => n.Body).IsRequired();
        builder.Property(n => n.Link).HasMaxLength(1000).IsRequired();

        builder.HasOne<AppUser>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Project>().WithMany().HasForeignKey(n => n.ProjectId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(n => new { n.UserId, n.ReadAt });
    }
}

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.NotificationKey).HasMaxLength(300).IsRequired();
        builder.Property(d => d.Channel).HasMaxLength(100).IsRequired();

        builder.HasOne<Project>().WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(d => new { d.NotificationKey, d.Channel }).IsUnique();
    }
}

internal sealed class ApprovalLinkConfiguration : IEntityTypeConfiguration<ApprovalLink>
{
    public void Configure(EntityTypeBuilder<ApprovalLink> builder)
    {
        builder.ToTable("approval_links");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.NonceHash).HasMaxLength(64).IsRequired();

        builder.HasOne<ApprovalGate>().WithMany().HasForeignKey(l => l.GateId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(l => l.NonceHash).IsUnique();
    }
}
