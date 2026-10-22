using Aide.Domain.Review;

namespace Aide.Domain.Entities;

/// <summary>The result of one review cycle (FR-38, §4.5, §4.7).</summary>
public sealed class ComplianceReport
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    /// <summary>The release version the report belongs to (§4.10).</summary>
    public Guid ReleaseVersionId { get; set; }

    public int ReviewCycle { get; set; }

    /// <summary>The checkpoint the review ran at.</summary>
    public int Checkpoint { get; set; }

    public decimal Score { get; set; }

    public IReadOnlyList<CategoryScoreEntry> CategoryScores { get; set; } = [];

    public IReadOnlyList<ReviewFinding> Violations { get; set; } = [];

    public IReadOnlyList<string> MissingFeatures { get; set; } = [];

    public DriftLevel DriftLevel { get; set; }

    public IReadOnlyList<DriftAreaEntry> DriftAreas { get; set; } = [];

    public ReviewDecision Decision { get; set; }

    public EscalationReason? EscalationReason { get; set; }

    public Guid AgentRunId { get; set; }

    /// <summary>The project's compliance threshold when the review ran (FR-39), so later settings changes don't
    /// reinterpret past decisions.</summary>
    public decimal Threshold { get; set; }

    /// <summary>The optimisation plan artifact the review produced (FR-38), if any. Cleared if that artifact row is deleted.</summary>
    public Guid? OptimizationPlanArtifactId { get; set; }

    /// <summary>Regressions on unchanged requirements (§4.13) as raw JSON (array); <c>[]</c> when none.</summary>
    public string Regressions { get; set; } = "[]";

    /// <summary>External checks (check runs, preview deployment) as raw JSON; null when none were collected.</summary>
    public string? ExternalChecks { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
