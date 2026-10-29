using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aide.Infrastructure.Persistence.Configurations;

// FR-87–FR-91, FR-142–FR-151, §4.15, §5: platform connections, deployment targets, env vars and deployments.

internal sealed class PlatformConnectionConfiguration : IEntityTypeConfiguration<PlatformConnection>
{
    public void Configure(EntityTypeBuilder<PlatformConnection> builder)
    {
        builder.ToTable("platform_connections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Provider).IsRequired();
        builder.Property(c => c.Label).HasColumnType("text").IsRequired();
        builder.Property(c => c.SecretEncrypted).HasColumnType("text").IsRequired();
        builder.Property(c => c.Last4).HasColumnType("text").IsRequired();
        builder.Property(c => c.AccountId).HasColumnType("text");
        builder.Property(c => c.AccountName).HasColumnType("text");
        builder.Property(c => c.Metadata).HasJsonbConversion().HasDefaultValueSql("'{}'::jsonb").IsRequired();
        builder.PrimitiveCollection(c => c.Scopes).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]").IsRequired();
        builder.Property(c => c.Status).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);

        // §5: the Connections page lists a user's connections per provider; the leading user_id serves user lookups.
        builder.HasIndex(c => new { c.UserId, c.Provider });
    }
}

internal sealed class DeploymentTargetConfiguration : IEntityTypeConfiguration<DeploymentTarget>
{
    public void Configure(EntityTypeBuilder<DeploymentTarget> builder)
    {
        builder.ToTable("deployment_targets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Component).IsRequired();
        builder.Property(t => t.Provider).IsRequired();
        builder.Property(t => t.ResourceId).HasColumnType("text");
        builder.Property(t => t.PreviewResourceId).HasColumnType("text");
        builder.Property(t => t.Name).HasColumnType("text");
        builder.Property(t => t.RootDirectory).HasColumnType("text")
            .HasDefaultValue(DeploymentTarget.DefaultRootDirectory).IsRequired();
        builder.Property(t => t.BuildConfig).HasJsonbConversion().HasDefaultValueSql("'{}'::jsonb").IsRequired();
        builder.Property(t => t.ProductionUrl).HasColumnType("text");
        builder.Property(t => t.PreviewUrl).HasColumnType("text");
        builder.Property(t => t.Status).IsRequired();
        builder.Property(t => t.StatusReason).HasColumnType("text");
        builder.Property(t => t.UpdatedAt).IsRequired();
        builder.Property(t => t.RowVersion).IsXminRowVersion();
        builder.HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);

        // FR-144: the bound connection and the Owner who bound it; deleting either clears the binding.
        builder.HasOne<PlatformConnection>().WithMany().HasForeignKey(t => t.ConnectionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(t => t.BoundByUserId).OnDelete(DeleteBehavior.SetNull);

        // §5: one target per component of a project.
        builder.HasIndex(t => new { t.ProjectId, t.Component })
            .IsUnique()
            .HasDatabaseName("ux_deployment_targets_project_id_component");
        builder.HasIndex(t => t.ConnectionId);
        builder.HasIndex(t => t.BoundByUserId);
    }
}

internal sealed class DeploymentEnvVarConfiguration : IEntityTypeConfiguration<DeploymentEnvVar>
{
    public void Configure(EntityTypeBuilder<DeploymentEnvVar> builder)
    {
        // FR-88: targets are a non-empty subset of {preview, production}; FR-145: applies_to of {frontend, backend}.
        builder.ToTable("deployment_env_vars", t =>
        {
            t.HasCheckConstraint(
                "ck_deployment_env_vars_targets",
                "cardinality(targets) > 0 AND targets <@ ARRAY['preview', 'production']::text[]");
            t.HasCheckConstraint(
                "ck_deployment_env_vars_applies_to",
                "cardinality(applies_to) > 0 AND applies_to <@ ARRAY['frontend', 'backend']::text[]");
        });
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Key).HasMaxLength(DeploymentEnvVar.KeyMaxLength).IsRequired();
        builder.PrimitiveCollection(v => v.Targets).HasColumnType("text[]").IsRequired();
        builder.Property(v => v.ValueEncrypted).HasColumnType("text").IsRequired();
        builder.Property(v => v.Sensitive).IsRequired();
        builder.PrimitiveCollection(v => v.AppliesTo).HasColumnType("text[]").IsRequired();
        builder.Property(v => v.Public).IsRequired();
        builder.Property(v => v.Source).IsRequired();
        builder.Property(v => v.DerivedFrom).HasColumnType("text");
        builder.Property(v => v.VercelEnvIds).HasJsonbConversion();
        builder.Property(v => v.UpdatedAt).IsRequired();
        builder.Property(v => v.SyncError).HasColumnType("text");
        builder.HasOne<Project>().WithMany().HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.Cascade);

        // Env var keys are case-sensitive (as in Vercel), so plain text equality.
        builder.HasIndex(v => new { v.ProjectId, v.Key }).IsUnique();
    }
}

internal sealed class DeploymentConfiguration : IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> builder)
    {
        builder.ToTable("deployments");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Provider).IsRequired();
        builder.Property(d => d.Environment).IsRequired();
        builder.Property(d => d.Component).IsRequired();
        builder.Property(d => d.Kind).IsRequired();
        builder.Property(d => d.ProviderDeploymentId).HasColumnType("text").IsRequired();
        builder.Property(d => d.Url).HasColumnType("text");
        builder.Property(d => d.InspectorUrl).HasColumnType("text");
        builder.Property(d => d.GitRef).HasMaxLength(255).IsRequired();
        builder.Property(d => d.GitSha).HasMaxLength(64).IsRequired();
        builder.Property(d => d.Status).IsRequired();
        builder.Property(d => d.ErrorCode).HasMaxLength(64);
        builder.Property(d => d.ErrorMessage).HasColumnType("text");
        builder.Property(d => d.BuildLogTail).HasMaxLength(Deployment.BuildLogTailMaxLength);
        builder.Property(d => d.Trigger).IsRequired();
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.UpdatedAt).IsRequired();
        builder.Property(d => d.RowVersion).IsXminRowVersion();

        builder.HasOne<Project>().WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Cascade);

        // Same-project references use NO ACTION (see WorkflowConfigurations): they go together through the project cascade.
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(d => d.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Release>().WithMany().HasForeignKey(d => d.ReleaseId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(d => d.TriggeredBy).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Deployment>().WithMany().HasForeignKey(d => d.PromotedFromDeploymentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<DeploymentTarget>().WithMany().HasForeignKey(d => d.TargetId).OnDelete(DeleteBehavior.SetNull);

        // Deployments page (FR-91): newest first per project.
        builder.HasIndex(d => new { d.ProjectId, d.CreatedAt }).IsDescending(false, true);

        // §4.15: review waits for the latest preview of the PR head SHA; production adopts Vercel's deployment of a SHA.
        builder.HasIndex(d => new { d.ProjectId, d.GitSha });

        // FR-149, §5: the automatic production deployment is created once per release and component, idempotent by
        // (release_id, component). Redeploy, promote and manual rows are new rows (with the same release), so they are
        // outside the index.
        builder.HasIndex(d => new { d.ReleaseId, d.Component })
            .IsUnique()
            .HasFilter("environment = 'production' AND trigger = 'auto'")
            .HasDatabaseName("ux_deployments_release_id_component_auto_production");

        // Pollers pick up unfinished deployments.
        builder.HasIndex(d => d.Status)
            .HasFilter("status IN ('queued', 'building')")
            .HasDatabaseName("ix_deployments_status_active");

        builder.HasIndex(d => d.ReleaseId);
        builder.HasIndex(d => d.ReleaseVersionId);
        builder.HasIndex(d => d.TriggeredBy);
        builder.HasIndex(d => d.PromotedFromDeploymentId);
        builder.HasIndex(d => d.TargetId);
    }
}
