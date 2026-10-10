using Aide.Domain.Review;

namespace Aide.Api.Features.Review;

/// <summary>A review was queued (FR-38): the review job's id and the cycle number the review will record.</summary>
public sealed record ReviewStartedDto(Guid JobId, int Cycle);

/// <summary>Why the review agent scored a category as it did: a file (when the evidence points at one) and the reason.</summary>
public sealed record EvidenceDto(string? File, string Reason);

/// <summary>
/// One compliance category (§4.7): its weight, the agent's score (0–1), the deterministic penalty (sum of the fixed
/// critical/major/minor penalties of its deterministic violations) and the final score (agent score − penalty, floored
/// at 0), with the agent's evidence.
/// </summary>
public sealed record CategoryScoreDto(
    ReviewCategory Category,
    decimal Weight,
    decimal AgentScore,
    decimal Penalty,
    decimal Score,
    IReadOnlyList<EvidenceDto> Evidence);

/// <summary>A violation found by a deterministic check or the review agent (§4.7). <c>file</c> and <c>evidence</c> are set when known.</summary>
public sealed record ViolationDto(
    ReviewCategory Category,
    Severity Severity,
    FindingSource Source,
    string Message,
    string? File,
    string? Evidence);

/// <summary>A drift area reported by the review agent (§4.7).</summary>
public sealed record DriftAreaDto(string Area, Severity Severity, string Description);

/// <summary>
/// The compliance report of one review cycle (FR-38, FR-41). <c>overallScore</c> is Σ category score × weight (3 dp);
/// <c>threshold</c> is the project's compliance threshold. <c>decision</c> follows §4.5; <c>decisionReason</c> is set
/// when the decision is <c>escalation_required</c> (<c>low_score</c>, <c>severe_drift</c>, <c>max_cycles</c>,
/// <c>no_improvement</c> or <c>no_optimization_plan</c> — another cycle was required but the review proposed no
/// optimisation steps), else <c>null</c>. <c>optimizationPlanArtifactId</c> is the optimisation plan draft the review
/// produced (FR-40), if any.
/// </summary>
public sealed record ComplianceReportDto(
    Guid Id,
    int ProjectVersion,
    int Cycle,
    decimal OverallScore,
    decimal Threshold,
    IReadOnlyList<CategoryScoreDto> CategoryScores,
    IReadOnlyList<ViolationDto> Violations,
    IReadOnlyList<string> MissingFeatures,
    DriftLevel DriftLevel,
    IReadOnlyList<DriftAreaDto> DriftAreas,
    ReviewDecision Decision,
    EscalationReason? DecisionReason,
    Guid? OptimizationPlanArtifactId,
    DateTimeOffset CreatedAt);

/// <summary>
/// One review cycle's result in the compliance trend (FR-41). <c>threshold</c> is the compliance threshold that cycle was
/// decided against (the report's snapshot; the project's threshold may have changed since).
/// </summary>
public sealed record ComplianceTrendPointDto(
    int Cycle,
    int ProjectVersion,
    decimal OverallScore,
    DriftLevel DriftLevel,
    ReviewDecision Decision,
    DateTimeOffset CreatedAt,
    decimal Threshold);

/// <summary>The compliance score across review cycles, oldest first (FR-41), with the project's threshold. Empty before the first review.</summary>
public sealed record ComplianceTrendDto(decimal Threshold, IReadOnlyList<ComplianceTrendPointDto> Series);

/// <summary>The drift level and areas of the latest compliance report (FR-41, §4.7).</summary>
public sealed record ComplianceDriftDto(DriftLevel DriftLevel, IReadOnlyList<DriftAreaDto> Areas, int ProjectVersion, int Cycle);
