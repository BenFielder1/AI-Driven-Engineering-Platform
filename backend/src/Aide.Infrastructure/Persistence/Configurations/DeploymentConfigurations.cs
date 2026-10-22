using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aide.Infrastructure.Persistence.Configurations;

// FR-87–FR-91, §4.15, §5: Vercel connections, project deployment settings, env vars and deployments.

internal sealed class VercelConnectionConfiguration : IEntityTypeConfiguration<VercelConnection>
{
    public void Configure(EntityTypeBuilder<VercelConnection> builder)
    {
        // A team-scoped token always names its team.
        builder.ToTable("vercel_connections", t => t.HasCheckConstraint(
            "ck_vercel_connections_team_scope", "scope <> 'team' OR team_id IS NOT NULL"));
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Label).HasColumnType("text").IsRequired();
        builder.Property(c => c.TokenEncrypted).HasColumnType("text").IsRequired();
        builder.Property(c => c.Last4).HasColumnType("text").IsRequired();
        builder.Property(c => c.VercelUserId).HasColumnType("text").IsRequired();
        builder.Property(c => c.VercelUsername).HasColumnType("text");
        builder.Property(c => c.TeamId).HasColumnType("text");
        builder.Property(c => c.TeamSlug).HasColumnType("text");
        builder.Property(c => c.Scope).IsRequired();
        builder.Property(c => c.Status).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => c.UserId);
    }
}

internal sealed class ProjectDeploymentSettingsConfiguration : IEntityTypeConfiguration<ProjectDeploymentSettings>
{
    public void Configure(EntityTypeBuilder<ProjectDeploymentSettings> builder)
    {
        builder.ToTable("project_deployment_settings");
        builder.HasKey(s => s.ProjectId);
        builder.Property(s => s.ProjectId).ValueGeneratedNever();
        builder.Property(s => s.Provider).HasDefaultValue(DeploymentProvider.Vercel).IsRequired();
        builder.Property(s => s.VercelProjectId).HasColumnType("text");
        builder.Property(s => s.VercelProjectName).HasColumnType("text");
        builder.Property(s => s.RootDirectory).HasColumnType("text")
            .HasDefaultValue(ProjectDeploymentSettings.DefaultRootDirectory).IsRequired();
        builder.Property(s => s.Framework).HasColumnType("text")
            .HasDefaultValue(ProjectDeploymentSettings.DefaultFramework).IsRequired();
        builder.Property(s => s.ProductionUrl).HasColumnType("text");
        builder.Property(s => s.UpdatedAt).IsRequired();
        builder.Property(s => s.RowVersion).IsXminRowVersion();
        builder.HasOne<Project>().WithOne().HasForeignKey<ProjectDeploymentSettings>(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);

        // FR-88: the bound connection and the Owner who bound it; deleting either clears the binding.
        builder.HasOne<VercelConnection>().WithMany().HasForeignKey(s => s.ConnectionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(s => s.BoundByUserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(s => s.ConnectionId);
        builder.HasIndex(s => s.BoundByUserId);
    }
}

internal sealed class DeploymentEnvVarConfiguration : IEntityTypeConfiguration<DeploymentEnvVar>
{
    public void Configure(EntityTypeBuilder<DeploymentEnvVar> builder)
    {
        // FR-88: targets are a non-empty subset of {preview, production}.
        builder.ToTable("deployment_env_vars", t => t.HasCheckConstraint(
            "ck_deployment_env_vars_targets",
            "cardinality(targets) > 0 AND targets <@ ARRAY['preview', 'production']::text[]"));
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Key).HasMaxLength(DeploymentEnvVar.KeyMaxLength).IsRequired();
        builder.PrimitiveCollection(v => v.Targets).HasColumnType("text[]").IsRequired();
        builder.Property(v => v.ValueEncrypted).HasColumnType("text").IsRequired();
        builder.Property(v => v.Sensitive).IsRequired();
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
        builder.Property(d => d.Target).IsRequired();
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

        // Deployments page (FR-91): newest first per project.
        builder.HasIndex(d => new { d.ProjectId, d.CreatedAt }).IsDescending(false, true);

        // §4.15: review waits for the latest preview of the PR head SHA; production adopts Vercel's deployment of a SHA.
        builder.HasIndex(d => new { d.ProjectId, d.GitSha });

        // §4.15: the automatic production deployment is created once per release, idempotent by (release_id, target).
        // Redeploy, promote and manual rows are new rows, so they are outside the index.
        builder.HasIndex(d => new { d.ReleaseId, d.Target })
            .IsUnique()
            .HasFilter("target = 'production' AND trigger = 'auto'")
            .HasDatabaseName("ux_deployments_release_id_target_auto_production");

        // Pollers pick up unfinished deployments.
        builder.HasIndex(d => d.Status)
            .HasFilter("status IN ('queued', 'building')")
            .HasDatabaseName("ix_deployments_status_active");

        builder.HasIndex(d => d.ReleaseId);
        builder.HasIndex(d => d.ReleaseVersionId);
        builder.HasIndex(d => d.TriggeredBy);
        builder.HasIndex(d => d.PromotedFromDeploymentId);
    }
}
