using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aide.Infrastructure.Persistence.Configurations;

// FR-152–FR-154, §4.21, §5: managed databases and the Supabase BaaS migration runs.

internal sealed class ManagedDatabaseConfiguration : IEntityTypeConfiguration<ManagedDatabase>
{
    public void Configure(EntityTypeBuilder<ManagedDatabase> builder)
    {
        builder.ToTable("managed_databases");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Environment).IsRequired();
        builder.Property(d => d.Provider).IsRequired();
        builder.Property(d => d.ResourceId).HasColumnType("text");
        builder.Property(d => d.Region).HasColumnType("text");
        builder.Property(d => d.Plan).HasColumnType("text");
        builder.Property(d => d.Status).IsRequired();
        builder.Property(d => d.ConnectionEncrypted).HasColumnType("text").IsRequired();
        builder.Property(d => d.DbPasswordEncrypted).HasColumnType("text");
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.UpdatedAt).IsRequired();
        builder.Property(d => d.RowVersion).IsXminRowVersion();
        builder.HasOne<Project>().WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Cascade);

        // FR-152: the Owner's connection the database was made with; deleting it keeps the database, unbound.
        builder.HasOne<PlatformConnection>().WithMany().HasForeignKey(d => d.ConnectionId).OnDelete(DeleteBehavior.SetNull);

        // §5: one database per environment of a project.
        builder.HasIndex(d => new { d.ProjectId, d.Environment })
            .IsUnique()
            .HasDatabaseName("ux_managed_databases_project_id_environment");
        builder.HasIndex(d => d.ConnectionId);
    }
}

internal sealed class DatabaseMigrationRunConfiguration : IEntityTypeConfiguration<DatabaseMigrationRun>
{
    public void Configure(EntityTypeBuilder<DatabaseMigrationRun> builder)
    {
        builder.ToTable("database_migration_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.GitSha).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Environment).IsRequired();
        builder.Property(r => r.Status).IsRequired();
        builder.Property(r => r.Applied).HasJsonbConversion().HasDefaultValueSql("'[]'::jsonb").IsRequired();
        builder.Property(r => r.ErrorCode).HasMaxLength(64);
        builder.Property(r => r.ErrorMessage).HasColumnType("text");
        builder.Property(r => r.StartedAt).IsRequired();
        builder.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ManagedDatabase>().WithMany().HasForeignKey(r => r.ManagedDatabaseId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Release>().WithMany().HasForeignKey(r => r.ReleaseId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(r => r.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);

        // §5: a project's runs, newest first.
        builder.HasIndex(r => new { r.ProjectId, r.StartedAt });
        builder.HasIndex(r => r.ManagedDatabaseId);
        builder.HasIndex(r => r.ReleaseId);
        builder.HasIndex(r => r.ReleaseVersionId);
    }
}
