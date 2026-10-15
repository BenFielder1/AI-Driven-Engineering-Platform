using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Domain.Projects;

namespace Aide.Domain.Entities;

/// <summary>The project aggregate (FR-5, §4.1). Phase and status change only through the lifecycle state machine.</summary>
public sealed class Project
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Idea { get; set; } = string.Empty;

    public Phase CurrentPhase { get; set; } = Phase.Refinement;

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    /// <summary>The lifecycle checkpoint within the current release version (§4.10; v1's "project version").</summary>
    public int Checkpoint { get; set; } = ProjectDefaults.InitialVersion;

    /// <summary>The release version the lifecycle state belongs to (the one in development, or the latest released).</summary>
    public Guid? CurrentReleaseVersionId { get; set; }

    /// <summary>Denormalised: the highest <c>released</c> or <c>imported</c> version (§4.10); null when never released.</summary>
    public string? ReleasedVersion { get; set; }

    /// <summary>Denormalised: the version <c>in_development</c> or <c>awaiting_merge</c>; null when none.</summary>
    public string? DevelopmentVersion { get; set; }

    /// <summary>FR-65 release merge strategy.</summary>
    public MergeStrategy MergeStrategy { get; set; } = MergeStrategy.AideMerges;

    /// <summary>Tag prefix for release tags (§4.10, default <c>v</c>).</summary>
    public string TagPrefix { get; set; } = ReleaseVersion.DefaultTagPrefix;

    public ApprovalMode ApprovalMode { get; set; } = ProjectDefaults.Mode;

    public decimal ComplianceThreshold { get; set; } = ProjectDefaults.ComplianceThreshold;

    public int MaxFilesPerStep { get; set; } = ProjectDefaults.MaxFilesPerStep;

    public int MaxReviewCycles { get; set; } = ProjectDefaults.MaxReviewCycles;

    /// <summary>Current review cycle (0 before the first review, §4.5 <c>c</c>).</summary>
    public int ReviewCycle { get; set; }

    /// <summary>Extra cycles granted by escalation <c>continue</c> decisions (FR-14); added to max review cycles.</summary>
    public int ExtraReviewCycles { get; set; }

    public long? TokenBudget { get; set; }

    public long TokensUsed { get; set; }

    public bool ExecutionPaused { get; set; }

    /// <summary>FR-45: one rollback per project per minute.</summary>
    public DateTimeOffset? LastRollbackAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>); a stale update raises a conflict (409).</summary>
    public uint RowVersion { get; set; }
}
