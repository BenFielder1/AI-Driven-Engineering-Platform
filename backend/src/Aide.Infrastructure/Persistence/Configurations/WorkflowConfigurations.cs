using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aide.Infrastructure.Persistence.Configurations;

// References between rows of the same project use NO ACTION: they all disappear together through the project
// cascade (FR-7), and NO ACTION is checked at the end of the statement, so the cascade order doesn't matter.

internal sealed class ArtifactConfiguration : IEntityTypeConfiguration<Artifact>
{
    public void Configure(EntityTypeBuilder<Artifact> builder)
    {
        builder.ToTable("artifacts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Content).IsRequired();
        builder.Property(a => a.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(a => a.RowVersion).IsXminRowVersion();

        builder.HasOne<Project>().WithMany().HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(a => a.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(a => a.ApprovedBy).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AgentRun>().WithMany().HasForeignKey(a => a.AgentRunId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(a => a.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);

        // Artifact versions keep counting across release versions (FR-58, FR-59), so uniqueness stays per project.
        builder.HasIndex(a => new { a.ProjectId, a.Type, a.Version }).IsUnique();
        builder.HasIndex(a => new { a.ProjectId, a.Type, a.Status });
        builder.HasIndex(a => new { a.ProjectId, a.ReleaseVersionId });
        builder.HasIndex(a => a.ReleaseVersionId);
    }
}

internal sealed class ApprovalGateConfiguration : IEntityTypeConfiguration<ApprovalGate>
{
    public void Configure(EntityTypeBuilder<ApprovalGate> builder)
    {
        builder.ToTable("approval_gates");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();
        builder.Property(g => g.RowVersion).IsXminRowVersion();

        builder.HasOne<Project>().WithMany().HasForeignKey(g => g.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Artifact>().WithMany().HasForeignKey(g => g.ArtifactId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(g => g.ReviewerId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(g => g.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);

        // At most one pending gate per (project, type) — enforced by the database (§4.2).
        builder.HasIndex(g => new { g.ProjectId, g.Type })
            .IsUnique()
            .HasFilter("status = 'pending'")
            .HasDatabaseName("ux_approval_gates_project_id_type_pending");
        builder.HasIndex(g => new { g.ProjectId, g.Status });
        builder.HasIndex(g => new { g.ProjectId, g.ReleaseVersionId });
        builder.HasIndex(g => g.ReleaseVersionId);
    }
}

internal sealed class ExecutionStepConfiguration : IEntityTypeConfiguration<ExecutionStep>
{
    public void Configure(EntityTypeBuilder<ExecutionStep> builder)
    {
        builder.ToTable("execution_steps");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Title).HasMaxLength(300).IsRequired();
        builder.Property(s => s.Task).IsRequired();
        builder.Property(s => s.DeclaredFiles).HasJsonbConversion().IsRequired();
        builder.PrimitiveCollection(s => s.Dependencies).HasColumnType("integer[]").IsRequired();
        builder.Property(s => s.Violations).HasJsonbConversion();
        builder.Property(s => s.FileManifest).HasJsonbConversion();
        builder.Property(s => s.BranchName).HasMaxLength(255);
        builder.Property(s => s.CommitSha).HasMaxLength(64);
        builder.Property(s => s.AgentKey).HasMaxLength(100);
        builder.PrimitiveCollection(s => s.Covers).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]").IsRequired();
        builder.Property(s => s.RowVersion).IsXminRowVersion();

        builder.HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Artifact>().WithMany().HasForeignKey(s => s.PlanArtifactId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(s => s.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(s => new { s.PlanArtifactId, s.StepNumber }).IsUnique();
        builder.HasIndex(s => new { s.ProjectId, s.Status });
        builder.HasIndex(s => new { s.ProjectId, s.ReleaseVersionId });
        builder.HasIndex(s => s.ReleaseVersionId);
    }
}

internal sealed class ExecutionStepAttemptConfiguration : IEntityTypeConfiguration<ExecutionStepAttempt>
{
    public void Configure(EntityTypeBuilder<ExecutionStepAttempt> builder)
    {
        builder.ToTable("execution_step_attempts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Violations).HasJsonbConversion().IsRequired();
        builder.Property(a => a.Warnings).HasJsonbConversion().IsRequired();
        builder.Property(a => a.ProducedFiles).HasJsonbConversion().IsRequired();

        builder.HasOne<ExecutionStep>().WithMany().HasForeignKey(a => a.StepId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AgentRun>().WithMany().HasForeignKey(a => a.AgentRunId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(a => new { a.StepId, a.Attempt }).IsUnique();
        builder.HasIndex(a => a.AgentRunId);
    }
}

internal sealed class AgentRunConfiguration : IEntityTypeConfiguration<AgentRun>
{
    public void Configure(EntityTypeBuilder<AgentRun> builder)
    {
        builder.ToTable("agent_runs");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Model).HasMaxLength(100).IsRequired();
        builder.Property(r => r.PromptVersion).HasMaxLength(50).IsRequired();
        builder.Property(r => r.InputArtifacts).HasJsonbConversion().IsRequired();
        builder.Property(r => r.Output).HasColumnType("jsonb");
        builder.Property(r => r.CostEstimate).HasPrecision(10, 4);
        builder.Property(r => r.AgentKey).HasMaxLength(100);
        builder.Property(r => r.PresetId).HasMaxLength(100);
        builder.Property(r => r.PresetVersion).HasMaxLength(50);
        builder.Property(r => r.ToolCalls).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").IsRequired();

        builder.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ExecutionStep>().WithMany().HasForeignKey(r => r.StepId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(r => r.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);

        // The connection that paid for the run (FR-78/79); deleting the connection keeps the run.
        builder.HasOne<LlmCredential>().WithMany().HasForeignKey(r => r.CredentialId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(r => r.CredentialId);

        builder.HasIndex(r => new { r.ProjectId, r.CreatedAt });
        builder.HasIndex(r => new { r.Status, r.CreatedAt });
        builder.HasIndex(r => new { r.ProjectId, r.ReleaseVersionId });
        builder.HasIndex(r => r.ReleaseVersionId);

        // FR-51 cross-project admin list: ORDER BY created_at DESC, id DESC.
        builder.HasIndex(r => new { r.CreatedAt, r.Id }, "ix_agent_runs_created_at_id").IsDescending(true, true);
    }
}

internal sealed class ComplianceReportConfiguration : IEntityTypeConfiguration<ComplianceReport>
{
    public void Configure(EntityTypeBuilder<ComplianceReport> builder)
    {
        builder.ToTable("compliance_reports");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Score).HasPrecision(4, 3);
        builder.Property(r => r.Threshold).HasPrecision(4, 3);
        builder.Property(r => r.CategoryScores).HasJsonbConversion().IsRequired();
        builder.Property(r => r.Violations).HasJsonbConversion().IsRequired();
        builder.Property(r => r.MissingFeatures).HasJsonbConversion().IsRequired();
        builder.Property(r => r.DriftAreas).HasJsonbConversion().IsRequired();
        builder.Property(r => r.Regressions).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").IsRequired();
        builder.Property(r => r.ExternalChecks).HasColumnType("jsonb");

        builder.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AgentRun>().WithMany().HasForeignKey(r => r.AgentRunId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Artifact>().WithMany().HasForeignKey(r => r.OptimizationPlanArtifactId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(r => r.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(r => new { r.ProjectId, r.ReviewCycle });
        builder.HasIndex(r => new { r.ProjectId, r.ReleaseVersionId });
        builder.HasIndex(r => r.ReleaseVersionId);
        builder.HasIndex(r => r.OptimizationPlanArtifactId);
    }
}
