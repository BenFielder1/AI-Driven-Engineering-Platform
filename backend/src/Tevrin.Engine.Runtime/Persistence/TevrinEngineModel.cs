using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;

namespace Tevrin.Engine.Runtime.Persistence;

/// <summary>
/// D-0.7.0-12: the engine's part of the one Tevrin database. The host's DbContext applies
/// <see cref="ApplyTevrinEngineConventions"/> in <c>ConfigureConventions</c> and <see cref="ApplyTevrinEngineModel"/> in
/// <c>OnModelCreating</c>; migrations stay with the host (Aide.Infrastructure). The engine configures its tables, columns
/// and indexes; relationships to host tables (projects, users, gates) and host-only indexes stay in the host's
/// configurations of the same entities.
/// </summary>
public static class TevrinEngineModel
{
    /// <summary>Max length of enum wire-value columns.</summary>
    public const int EnumMaxLength = 32;

    /// <summary>Every engine enum is stored as its snake_case wire value (<see cref="EnumWireConverter{TEnum}"/>).</summary>
    public static ModelConfigurationBuilder ApplyTevrinEngineConventions(this ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        MapEnum<ApprovalMode>(configurationBuilder);
        MapEnum<ArtifactStatus>(configurationBuilder);
        MapEnum<GateStatus>(configurationBuilder);
        MapEnum<EscalationDecision>(configurationBuilder);
        MapEnum<AgentRunStatus>(configurationBuilder);
        MapEnum<LlmProvider>(configurationBuilder);
        MapEnum<JobStatus>(configurationBuilder);
        MapEnum<NotificationDeliveryStatus>(configurationBuilder);
        MapEnum<ApprovalLinkDecision>(configurationBuilder);
        return configurationBuilder;
    }

    /// <summary>The engine's tables: <c>events</c>, <c>jobs</c>, <c>notifications</c>, <c>notification_deliveries</c>,
    /// <c>approval_links</c>, <c>artifacts</c> and <c>approval_gates</c>.</summary>
    public static ModelBuilder ApplyTevrinEngineModel(this ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Entity<AuditEvent>(b =>
        {
            b.ToTable("events");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Type).HasMaxLength(100).IsRequired();
            b.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
            b.Property(e => e.Actor).HasMaxLength(200).IsRequired();

            b.HasIndex(e => new { e.ProjectId, e.CreatedAt });
            b.HasIndex(e => e.Type);

            // FR-51 cross-project admin list: ORDER BY created_at DESC, id DESC.
            b.HasIndex(e => new { e.CreatedAt, e.Id }, "ix_events_created_at_id").IsDescending(true, true);
        });

        builder.Entity<Job>(b =>
        {
            b.ToTable("jobs");
            b.HasKey(j => j.Id);
            b.Property(j => j.Id).ValueGeneratedNever();
            b.Property(j => j.Type).HasMaxLength(100).IsRequired();
            b.Property(j => j.Payload).HasColumnType("jsonb").IsRequired();
            b.Property(j => j.LockedBy).HasMaxLength(200);

            // Claim query: WHERE status = 'queued' AND run_after <= now ORDER BY run_after FOR UPDATE SKIP LOCKED.
            b.HasIndex(j => new { j.Status, j.RunAfter });

            // Plain FK index (the host's partial unique index on project_id doesn't serve the project cascade or
            // per-project lookups).
            b.HasIndex(j => j.ProjectId, "ix_jobs_project_id");
        });

        builder.Entity<Notification>(b =>
        {
            b.ToTable("notifications");
            b.HasKey(n => n.Id);
            b.Property(n => n.Id).ValueGeneratedNever();
            b.Property(n => n.Type).HasMaxLength(100).IsRequired();
            b.Property(n => n.Title).HasMaxLength(300).IsRequired();
            b.Property(n => n.Body).IsRequired();
            b.Property(n => n.Link).HasMaxLength(1000).IsRequired();
            b.Property(n => n.NotificationKey).HasMaxLength(300);

            // Unread count: WHERE user_id = @u AND read_at IS NULL.
            b.HasIndex(n => new { n.UserId, n.ReadAt });

            // Keyset paging, newest first: WHERE user_id = @u AND (created_at, id) < (@c, @i) ORDER BY created_at DESC, id DESC.
            b.HasIndex(n => new { n.UserId, n.CreatedAt, n.Id }, "ix_notifications_user_id_created_at_id")
                .IsDescending(false, true, true)
                .HasDatabaseName("ix_notifications_user_id_created_at_id");

            // FR-49: one in-app notification per (user, logical notification); keyless notifications are not deduplicated.
            b.HasIndex(n => new { n.UserId, n.NotificationKey }, "ux_notifications_user_id_notification_key")
                .IsUnique()
                .HasFilter("notification_key IS NOT NULL")
                .HasDatabaseName("ux_notifications_user_id_notification_key");
        });

        builder.Entity<NotificationDelivery>(b =>
        {
            b.ToTable("notification_deliveries");
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).ValueGeneratedNever();
            b.Property(d => d.NotificationKey).HasMaxLength(300).IsRequired();
            b.Property(d => d.Channel).HasMaxLength(100).IsRequired();

            b.HasIndex(d => new { d.NotificationKey, d.Channel }).IsUnique();

            // Retry sweep: WHERE status = 'pending' AND next_attempt_at <= now ORDER BY next_attempt_at.
            b.HasIndex(d => new { d.Status, d.NextAttemptAt });
        });

        builder.Entity<ApprovalLink>(b =>
        {
            b.ToTable("approval_links");
            b.HasKey(l => l.Id);
            b.Property(l => l.Id).ValueGeneratedNever();
            b.Property(l => l.NonceHash).HasMaxLength(64).IsRequired();

            b.HasIndex(l => l.NonceHash).IsUnique();
        });

        builder.Entity<Artifact>(b =>
        {
            b.ToTable("artifacts");
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).ValueGeneratedNever();

            // FR-270, FR-274: the artifact key (a pack's artifact keys), the same column as the 0.6.1 enum.
            b.Property(a => a.Type).HasMaxLength(EnumMaxLength);
            b.Property(a => a.Content).IsRequired();
            b.Property(a => a.ContentHash).HasMaxLength(64).IsRequired();
            XminRowVersion(b.Property(a => a.RowVersion));

            // Artifact versions keep counting across release versions (FR-58, FR-59), so uniqueness stays per project.
            b.HasIndex(a => new { a.ProjectId, a.Type, a.Version }).IsUnique();
            b.HasIndex(a => new { a.ProjectId, a.Type, a.Status });
            b.HasIndex(a => new { a.ProjectId, a.ReleaseVersionId });
            b.HasIndex(a => a.ReleaseVersionId);
        });

        builder.Entity<ApprovalGate>(b =>
        {
            b.ToTable("approval_gates");
            b.HasKey(g => g.Id);
            b.Property(g => g.Id).ValueGeneratedNever();

            // FR-271, FR-274: the gate key (EngineGates and a pack's gate keys), the same column as the 0.6.1 enum.
            b.Property(g => g.Type).HasMaxLength(EnumMaxLength);
            XminRowVersion(b.Property(g => g.RowVersion));

            b.HasOne<Artifact>().WithMany().HasForeignKey(g => g.ArtifactId).OnDelete(DeleteBehavior.NoAction);

            // At most one pending gate per (project, type) — enforced by the database (§4.2).
            b.HasIndex(g => new { g.ProjectId, g.Type })
                .IsUnique()
                .HasFilter("status = 'pending'")
                .HasDatabaseName("ux_approval_gates_project_id_type_pending");
            b.HasIndex(g => new { g.ProjectId, g.Status });
            b.HasIndex(g => new { g.ProjectId, g.ReleaseVersionId });
            b.HasIndex(g => g.ReleaseVersionId);
        });

        return builder;
    }

    /// <summary>Maps the property to PostgreSQL's <c>xmin</c> system column as an optimistic concurrency token (no physical
    /// column is created).</summary>
    private static void XminRowVersion(PropertyBuilder<uint> property) =>
        property.HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

    private static void MapEnum<TEnum>(ModelConfigurationBuilder configurationBuilder)
        where TEnum : struct, Enum =>
        configurationBuilder.Properties<TEnum>()
            .HaveConversion<EnumWireConverter<TEnum>>()
            .HaveMaxLength(EnumMaxLength);
}
