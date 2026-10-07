using Aide.Domain.Review;

namespace Aide.Domain.Entities;

/// <summary>The result of one review cycle (FR-38, §4.5, §4.7).</summary>
public sealed class ComplianceReport
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public int ReviewCycle { get; set; }

    /// <summary>The project version the review ran at.</summary>
    public int ProjectVersion { get; set; }

    public decimal Score { get; set; }

    public IReadOnlyList<CategoryScoreEntry> CategoryScores { get; set; } = [];

    public IReadOnlyList<ReviewFinding> Violations { get; set; } = [];

    public IReadOnlyList<string> MissingFeatures { get; set; } = [];

    public DriftLevel DriftLevel { get; set; }

    public IReadOnlyList<DriftAreaEntry> DriftAreas { get; set; } = [];

    public ReviewDecision Decision { get; set; }

    public EscalationReason? EscalationReason { get; set; }

    public Guid AgentRunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
