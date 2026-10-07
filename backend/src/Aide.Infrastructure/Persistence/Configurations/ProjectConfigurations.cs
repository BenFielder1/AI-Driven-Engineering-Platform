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
        builder.Property(p => p.RowVersion).IsXminRowVersion();

        builder.HasIndex(p => new { p.Status, p.CurrentPhase });
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
        builder.Property(s => s.BranchName).HasMaxLength(255);
        builder.Property(s => s.PullRequestUrl).HasMaxLength(500);
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
        builder.HasIndex(t => new { t.ProjectId, t.CreatedAt });
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
        builder.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.ProjectId, r.CreatedAt });
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
        builder.PrimitiveCollection(r => r.ConflictingFiles).HasColumnType("text[]").IsRequired();
        builder.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.ProjectId, r.ProjectVersion })
            .IsUnique()
            .HasFilter("status = 'succeeded'")
            .HasDatabaseName("ux_releases_project_id_project_version_succeeded");
    }
}
