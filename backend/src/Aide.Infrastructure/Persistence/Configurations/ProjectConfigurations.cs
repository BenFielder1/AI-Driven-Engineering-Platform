using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aide.Infrastructure.Persistence.Configurations;

internal static class ConfigurationExtensions
{
    /// <summary>Maps <paramref name="property"/> to PostgreSQL's <c>xmin</c> system column as an optimistic
    /// concurrency token (no physical column is created).</summary>
    public static void IsXminRowVersion(this PropertyBuilder<uint> property) =>
        property.HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
}

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Slug).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Idea).IsRequired();
        builder.Property(p => p.ComplianceThreshold).HasPrecision(3, 2);
        builder.Property(p => p.ReviewCycle).HasDefaultValue(0);
        builder.Property(p => p.ExtraReviewCycles).HasDefaultValue(0);
        builder.Property(p => p.ReleasedVersion).HasColumnType("text");
        builder.Property(p => p.DevelopmentVersion).HasColumnType("text");
        builder.Property(p => p.MergeStrategy).HasDefaultValue(MergeStrategy.AideMerges).IsRequired();
        builder.Property(p => p.TagPrefix).HasColumnType("text").HasDefaultValue(ReleaseVersion.DefaultTagPrefix).IsRequired();
        builder.Property(p => p.RowVersion).IsXminRowVersion();

        // current_release_version_id points at the version the lifecycle state belongs to. Its FK (ON DELETE SET NULL) is
        // DEFERRABLE INITIALLY DEFERRED and created in SQL by the ReleaseVersions migration, not mapped as a relationship:
        // a project and its first version reference each other and are inserted in one SaveChanges.

        builder.HasIndex(p => new { p.Status, p.CurrentPhase });
        builder.HasIndex(p => p.CurrentReleaseVersionId);
    }
}

internal sealed class ReleaseVersionConfiguration : IEntityTypeConfiguration<ReleaseVersion>
{
    public void Configure(EntityTypeBuilder<ReleaseVersion> builder)
    {
        builder.ToTable("release_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Version).HasColumnType("text").IsRequired();
        builder.Property(v => v.Tag).HasMaxLength(100);
        builder.Property(v => v.TagPrefix).HasColumnType("text").HasDefaultValue(ReleaseVersion.DefaultTagPrefix).IsRequired();
        builder.Property(v => v.BaseTag).HasMaxLength(100);
        builder.Property(v => v.BaseCommitSha).HasMaxLength(64);
        builder.Property(v => v.MergeCommitSha).HasMaxLength(64);
        builder.Property(v => v.BranchName).HasMaxLength(255);
        builder.Property(v => v.PullRequestUrl).HasMaxLength(500);
        builder.Property(v => v.Request).HasColumnType("text").IsRequired();
        builder.Property(v => v.PresetId).HasMaxLength(100);
        builder.Property(v => v.PresetVersion).HasMaxLength(50);
        builder.Property(v => v.PresetHash).HasMaxLength(64);
        builder.Property(v => v.RowVersion).IsXminRowVersion();

        builder.HasOne<Project>().WithMany().HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(v => v.StartedBy).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(v => new { v.ProjectId, v.Version }).IsUnique();

        // §4.10: at most one version in development or awaiting merge per project, enforced by the database.
        builder.HasIndex(v => v.ProjectId)
            .IsUnique()
            .HasFilter("status IN ('in_development', 'awaiting_merge')")
            .HasDatabaseName("ux_release_versions_project_id_open");
        builder.HasIndex(v => v.StartedBy);
    }
}

internal sealed class ProjectMembershipConfiguration : IEntityTypeConfiguration<ProjectMembership>
{
    public void Configure(EntityTypeBuilder<ProjectMembership> builder)
    {
        builder.ToTable("project_members");
        builder.HasKey(m => new { m.ProjectId, m.UserId });
        builder.HasOne<Project>().WithMany().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProjectGitSettingsConfiguration : IEntityTypeConfiguration<ProjectGitSettings>
{
    public void Configure(EntityTypeBuilder<ProjectGitSettings> builder)
    {
        builder.ToTable("project_git_settings");
        builder.HasKey(s => s.ProjectId);
        builder.Property(s => s.RepoUrl).HasMaxLength(500);
        builder.Property(s => s.DefaultBranch).HasMaxLength(255).IsRequired();
        builder.HasOne<Project>().WithOne().HasForeignKey<ProjectGitSettings>(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProjectLlmSettingsConfiguration : IEntityTypeConfiguration<ProjectLlmSettings>
{
    public void Configure(EntityTypeBuilder<ProjectLlmSettings> builder)
    {
        builder.ToTable("project_llm_settings");
        builder.HasKey(s => s.ProjectId);
        builder.Property(s => s.DefaultModel).HasMaxLength(100).IsRequired();
        builder.Property(s => s.ModelsByAgent).HasJsonbConversion().IsRequired();
        builder.HasOne<Project>().WithOne().HasForeignKey<ProjectLlmSettings>(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProjectNotificationSettingsConfiguration : IEntityTypeConfiguration<ProjectNotificationSettings>
{
    public void Configure(EntityTypeBuilder<ProjectNotificationSettings> builder)
    {
        builder.ToTable("project_notification_settings");
        builder.HasKey(s => s.ProjectId);
        builder.Property(s => s.Channels).HasJsonbConversion().IsRequired();
        builder.HasOne<Project>().WithOne().HasForeignKey<ProjectNotificationSettings>(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StateTransitionConfiguration : IEntityTypeConfiguration<StateTransition>
{
    public void Configure(EntityTypeBuilder<StateTransition> builder)
    {
        builder.ToTable("state_transitions");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Actor).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Reason).IsRequired();
        builder.HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(t => t.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(t => new { t.ProjectId, t.CreatedAt });
        builder.HasIndex(t => new { t.ProjectId, t.ReleaseVersionId });
        builder.HasIndex(t => t.ReleaseVersionId);
    }
}

internal sealed class RollbackEventConfiguration : IEntityTypeConfiguration<RollbackEvent>
{
    public void Configure(EntityTypeBuilder<RollbackEvent> builder)
    {
        builder.ToTable("rollback_events");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Reason).IsRequired();
        builder.Property(r => r.TriggeredBy).HasMaxLength(200).IsRequired();
        builder.PrimitiveCollection(r => r.InvalidatedArtifactIds).HasColumnType("uuid[]").IsRequired();
        builder.PrimitiveCollection(r => r.ResetStepIds).HasColumnType("uuid[]").IsRequired();
        builder.PrimitiveCollection(r => r.RequeuedStepIds).HasColumnType("uuid[]").HasDefaultValueSql("'{}'::uuid[]").IsRequired();
        builder.PrimitiveCollection(r => r.CancelledGateIds).HasColumnType("uuid[]").HasDefaultValueSql("'{}'::uuid[]").IsRequired();
        builder.PrimitiveCollection(r => r.CancelledJobIds).HasColumnType("uuid[]").HasDefaultValueSql("'{}'::uuid[]").IsRequired();
        builder.Property(r => r.RestoredPhase).IsRequired();
        builder.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(r => r.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(r => new { r.ProjectId, r.CreatedAt });
        builder.HasIndex(r => new { r.ProjectId, r.ReleaseVersionId });
        builder.HasIndex(r => r.ReleaseVersionId);
    }
}

internal sealed class ReleaseConfiguration : IEntityTypeConfiguration<Release>
{
    public void Configure(EntityTypeBuilder<Release> builder)
    {
        builder.ToTable("releases");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Tag).HasMaxLength(100).IsRequired();
        builder.Property(r => r.MergeCommitSha).HasMaxLength(64);
        builder.Property(r => r.PrUrl).HasMaxLength(500);
        builder.Property(r => r.GithubReleaseUrl).HasMaxLength(500);
        builder.Property(r => r.MergeReasons).HasColumnType("jsonb");
        builder.Property(r => r.Attempt).HasDefaultValue(1);
        builder.Property(r => r.ErrorCode).HasMaxLength(32);
        builder.Property(r => r.ErrorMessage).HasColumnName("error_message");
        builder.PrimitiveCollection(r => r.ConflictingFiles).HasColumnType("text[]").IsRequired();
        builder.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(r => r.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);

        // One row per attempt, numbered per (release version, checkpoint): checkpoints restart in every release version,
        // and v1 data may hold several succeeded attempts of one checkpoint (re-release after a rollback).
        builder.HasIndex(r => new { r.ReleaseVersionId, r.Checkpoint, r.Attempt })
            .IsUnique()
            .HasDatabaseName("ux_releases_release_version_id_checkpoint_attempt");
        builder.HasIndex(r => new { r.ProjectId, r.ReleaseVersionId });
    }
}
