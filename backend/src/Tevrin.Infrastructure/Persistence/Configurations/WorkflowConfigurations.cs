using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tevrin.Domain.Entities;
using Tevrin.Domain.Execution;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Infrastructure.Persistence.Conversion;
using Tevrin.Infrastructure.Persistence.Identity;

namespace Tevrin.Infrastructure.Persistence.Configurations;

// References between rows of the same project use NO ACTION: they all disappear together through the project
// cascade (FR-7), and NO ACTION is checked at the end of the statement, so the cascade order doesn't matter.

/// <summary>D-0.7.0-12: <c>artifacts</c> is configured by the engine (<see cref="TevrinEngineModel"/>); the host adds the
/// relationships to its tables.</summary>
internal sealed class ArtifactConfiguration : IEntityTypeConfiguration<Artifact>
{
    public void Configure(EntityTypeBuilder<Artifact> builder)
    {
        builder.HasOne<Project>().WithMany().HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(a => a.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(a => a.ApprovedBy).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AgentRun>().WithMany().HasForeignKey(a => a.AgentRunId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(a => a.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary>D-0.7.0-12: <c>approval_gates</c> is configured by the engine (<see cref="TevrinEngineModel"/>); the host adds
/// the relationships to its tables.</summary>
internal sealed class ApprovalGateConfiguration : IEntityTypeConfiguration<ApprovalGate>
{
    public void Configure(EntityTypeBuilder<ApprovalGate> builder)
    {
        builder.HasOne<Project>().WithMany().HasForeignKey(g => g.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(g => g.ReviewerId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(g => g.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary>FR-294, D-0.7.0-11: <c>pipeline_runs</c> is configured by the engine (<see cref="TevrinEngineModel"/>); every
/// run belongs to a project and goes with it (FR-7).</summary>
internal sealed class PipelineRunConfiguration : IEntityTypeConfiguration<PipelineRun>
{
    public void Configure(EntityTypeBuilder<PipelineRun> builder) =>
        builder.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
}

/// <summary>FR-336: <c>pipeline_schedules</c> is configured by the engine (<see cref="TevrinEngineModel"/>); a schedule goes
/// with its project, and keeps running after its creator is deleted.</summary>
internal sealed class PipelineScheduleConfiguration : IEntityTypeConfiguration<PipelineSchedule>
{
    public void Configure(EntityTypeBuilder<PipelineSchedule> builder)
    {
        builder.HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(s => s.CreatedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>FR-337: <c>pipeline_webhooks</c> is configured by the engine (<see cref="TevrinEngineModel"/>); a webhook goes
/// with its project, and keeps working after its creator is deleted.</summary>
internal sealed class PipelineWebhookConfiguration : IEntityTypeConfiguration<PipelineWebhook>
{
    public void Configure(EntityTypeBuilder<PipelineWebhook> builder)
    {
        builder.HasOne<Project>().WithMany().HasForeignKey(w => w.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(w => w.CreatedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>FR-351: <c>agent_feedback</c> is configured by the engine (<see cref="TevrinEngineModel"/>); an entry goes with
/// its project, and keeps its text after its author is deleted.</summary>
internal sealed class AgentFeedbackConfiguration : IEntityTypeConfiguration<AgentFeedback>
{
    public void Configure(EntityTypeBuilder<AgentFeedback> builder)
    {
        builder.HasOne<Project>().WithMany().HasForeignKey(f => f.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(f => f.CreatedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>FR-353: <c>outcome_scores</c> is configured by the engine (<see cref="TevrinEngineModel"/>); a score keeps its
/// value after the user who posted it is deleted.</summary>
internal sealed class OutcomeScoreConfiguration : IEntityTypeConfiguration<OutcomeScore>
{
    public void Configure(EntityTypeBuilder<OutcomeScore> builder) =>
        builder.HasOne<AppUser>().WithMany().HasForeignKey(o => o.CreatedBy).OnDelete(DeleteBehavior.SetNull);
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
        builder.Property(s => s.LegacyAgentKey).HasColumnType("text");
        builder.PrimitiveCollection(s => s.Covers).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]").IsRequired();

        // FR-256, §5: existing rows become plan steps; additive, safe for rolling updates.
        builder.Property(s => s.Origin).HasDefaultValue(StepOrigin.Plan).HasSentinel(StepOrigin.Plan).IsRequired();
        builder.Property(s => s.RowVersion).IsXminRowVersion();

        builder.HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Artifact>().WithMany().HasForeignKey(s => s.PlanArtifactId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(s => s.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<BuildCheck>().WithMany().HasForeignKey(s => s.BuildCheckId).OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(s => new { s.PlanArtifactId, s.StepNumber }).IsUnique();
        builder.HasIndex(s => s.BuildCheckId);
        builder.HasIndex(s => new { s.ProjectId, s.Status });
        builder.HasIndex(s => new { s.ProjectId, s.ReleaseVersionId });
        builder.HasIndex(s => s.ReleaseVersionId);
    }
}

internal sealed class BuildCheckConfiguration : IEntityTypeConfiguration<BuildCheck>
{
    public void Configure(EntityTypeBuilder<BuildCheck> builder)
    {
        builder.ToTable("build_checks");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.HeadSha).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Sources).HasJsonbConversion().IsRequired();
        builder.Property(c => c.Failures).HasJsonbConversion().IsRequired();
        builder.Property(c => c.RowVersion).IsXminRowVersion();

        builder.HasOne<Project>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(c => c.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);

        // §4.47: one check per (version, review cycle, attempt) among the checks in force; a rollback or invalidation
        // supersedes the earlier ones (kept as history), so re-execution starts again at attempt 0.
        builder.HasIndex(c => new { c.ReleaseVersionId, c.ReviewCycle, c.Attempt }).IsUnique().HasFilter("superseded_at IS NULL");
        builder.HasIndex(c => new { c.ProjectId, c.ReleaseVersionId });
        builder.HasIndex(c => new { c.ProjectId, c.Status });
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

/// <summary>FR-268, D-0.7.0-32: <c>agent_runs</c> is configured by the engine (<see cref="TevrinEngineModel"/>); the host adds
/// the relationships to its tables.</summary>
internal sealed class AgentRunConfiguration : IEntityTypeConfiguration<AgentRun>
{
    public void Configure(EntityTypeBuilder<AgentRun> builder)
    {
        builder.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ExecutionStep>().WithMany().HasForeignKey(r => r.StepId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<ReleaseVersion>().WithMany().HasForeignKey(r => r.ReleaseVersionId).OnDelete(DeleteBehavior.NoAction);

        // The connection that paid for the run (FR-78/79); deleting the connection keeps the run.
        builder.HasOne<LlmCredential>().WithMany().HasForeignKey(r => r.CredentialId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(r => r.CredentialId);
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
