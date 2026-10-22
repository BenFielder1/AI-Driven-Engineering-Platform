using Aide.Domain.Artifacts;
using Aide.Domain.Releases;
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
    Guid ReleaseVersionId,
    int Checkpoint,
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
    int Checkpoint,
    decimal OverallScore,
    DriftLevel DriftLevel,
    ReviewDecision Decision,
    DateTimeOffset CreatedAt,
    decimal Threshold);

/// <summary>The compliance score across review cycles, oldest first (FR-41), with the project's threshold. Empty before the first review.</summary>
public sealed record ComplianceTrendDto(decimal Threshold, IReadOnlyList<ComplianceTrendPointDto> Series);

/// <summary>The drift level and areas of the latest compliance report (FR-41, §4.7).</summary>
public sealed record ComplianceDriftDto(DriftLevel DriftLevel, IReadOnlyList<DriftAreaDto> Areas, int Checkpoint, int Cycle);

/// <summary>
/// One review cycle in the Review page's cycle list (FR-56): cycle number, overall score against the threshold it was
/// decided on, drift level, decision, date, the checkpoint it ran at and its release version. <c>id</c> is the report id:
/// cycle numbers restart after a rollback, so one release version can list two reports of the same cycle.
/// </summary>
public sealed record ComplianceReportSummaryDto(
    int Cycle,
    decimal Score,
    decimal Threshold,
    DriftLevel Drift,
    ReviewDecision Decision,
    DateTimeOffset CreatedAt,
    int Checkpoint,
    Guid ReleaseVersionId,
    Guid Id);

/// <summary>
/// An <c>[unchanged]</c> requirement the review found broken in an iteration (§4.13): the requirement id, the category it
/// was reported under, what is broken and the evidence. Regressions carry no deterministic penalty.
/// </summary>
public sealed record RegressionDto(string RequirementId, ReviewCategory Category, string Description, string? Evidence);

/// <summary>
/// FR-85: a GitHub check run or commit status on the PR head as review saw it. <c>conclusion</c> is <c>null</c> for a check
/// still pending when review stopped waiting. A failed <c>required</c> check is a major <c>testing</c> finding.
/// </summary>
public sealed record ExternalCheckDto(string Name, CheckRunConclusion? Conclusion, bool Required, string? DetailsUrl);

/// <summary>
/// The external inputs a review cycle collected (FR-85, FR-89): the PR head's <c>checks</c>, how long review waited for
/// pending checks (<c>checksWaitedSeconds</c>, up to 15 minutes; <c>null</c> when it didn't wait) and whether it gave up
/// waiting (<c>checksTimedOut</c>, recorded as a warning). <c>preview</c> is the preview deployment review waited for
/// (FR-90; <c>null</c> when none, e.g. Vercel not configured).
/// </summary>
public sealed record ExternalChecksDto(
    IReadOnlyList<ExternalCheckDto> Checks,
    int? ChecksWaitedSeconds,
    bool ChecksTimedOut,
    Deployments.PreviewCheckDto? Preview);

/// <summary>The optimisation plan a review cycle produced (FR-40): its artifact id, version and status.</summary>
public sealed record ReportPlanDto(Guid ArtifactId, int Version, ArtifactStatus Status);

/// <summary>
/// The full compliance report of one review cycle (FR-56): the category breakdown, every violation with its evidence,
/// the deterministic findings alone (<c>deterministicFindings</c>, the violations whose source is <c>deterministic</c>),
/// missing features, drift areas, the decision and its <c>escalationReason</c>, regressions (§4.13), the external checks
/// collected (FR-85 check runs, preview deployment; <c>null</c> when none), the optimisation plan it produced and
/// <c>executionRunId</c>, the review-fix run that plan produced (the plan artifact id, see <c>ListExecutionRuns</c>).
/// </summary>
public sealed record ComplianceReportDetailDto(
    Guid Id,
    Guid ReleaseVersionId,
    int Checkpoint,
    int Cycle,
    decimal OverallScore,
    decimal Threshold,
    IReadOnlyList<CategoryScoreDto> CategoryScores,
    IReadOnlyList<ViolationDto> Violations,
    IReadOnlyList<ViolationDto> DeterministicFindings,
    IReadOnlyList<string> MissingFeatures,
    DriftLevel DriftLevel,
    IReadOnlyList<DriftAreaDto> DriftAreas,
    ReviewDecision Decision,
    EscalationReason? EscalationReason,
    IReadOnlyList<RegressionDto> Regressions,
    ExternalChecksDto? ExternalChecks,
    ReportPlanDto? OptimizationPlan,
    Guid? ExecutionRunId,
    DateTimeOffset CreatedAt);
