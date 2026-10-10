using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Domain.Review;

/// <summary>Everything one review cycle decides from (§4.5, §4.7).</summary>
public sealed record ReviewAssessmentInput(
    CodeReviewResult Agent,
    IReadOnlyList<ReviewFinding> DeterministicFindings,
    decimal Threshold,
    int Cycle,
    int MaxReviewCycles,
    int ExtraReviewCycles,
    decimal? PreviousScore);

/// <summary>A review cycle's outcome: the category breakdown, overall score, drift, every violation and the decision.</summary>
public sealed record ReviewAssessmentResult(
    IReadOnlyList<CategoryScoreEntry> Categories,
    decimal Overall,
    DriftLevel Drift,
    IReadOnlyList<ReviewFinding> Violations,
    ReviewDecisionResult Decision);

/// <summary>
/// FR-38, FR-39: turns the agent's scores and the deterministic findings into the compliance report. Only deterministic
/// findings carry the fixed penalties (§4.7; the agent's own violations are already reflected in its scores); each
/// category's penalty is the sum of its findings' penalties and its score is the agent's score minus that, floored at 0.
/// Overall = Σ score × weight (3 dp); drift from the agent's drift areas; the decision from the §4.5 table.
/// </summary>
public static class ReviewAssessment
{
    public static ReviewAssessmentResult Assess(ReviewAssessmentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var agentScores = input.Agent.Categories.ToDictionary(c => c.Category, c => c.Score);
        var scored = ComplianceScoring.Score(agentScores, input.DeterministicFindings);
        var penalties = input.DeterministicFindings
            .GroupBy(f => f.Category)
            .ToDictionary(g => g.Key, g => g.Sum(f => ComplianceScoring.Penalty(f.Severity)));
        var categories = ComplianceScoring.Weights.Keys
            .OrderBy(c => c)
            .Select(c => new CategoryScoreEntry(
                c,
                scored.CategoryScores[c],
                Math.Clamp(agentScores[c], 0m, 1m),
                penalties.GetValueOrDefault(c),
                input.Agent.Categories.First(a => a.Category == c).Evidence))
            .ToList();

        var drift = DriftRules.Level(input.Agent.DriftAreas.Select(d => d.Severity));
        var decision = ReviewDecisionRules.Decide(new ReviewDecisionInput(
            scored.Overall,
            input.Threshold,
            drift,
            input.Cycle,
            input.MaxReviewCycles,
            input.ExtraReviewCycles,
            input.PreviousScore));
        var violations = input.DeterministicFindings
            .Select(f => f with { Source = FindingSource.Deterministic })
            .Concat(input.Agent.Violations)
            .ToList();
        return new ReviewAssessmentResult(categories, scored.Overall, drift, violations, decision);
    }

    /// <summary>
    /// Whether a review's optimisation plan becomes a draft (FR-38): when the decision is not <c>release_eligible</c> and the
    /// plan has steps. On <c>review_required</c> approving it starts the next cycle; on <c>escalation_required</c> it is there
    /// for an escalation <c>continue</c> (FR-14).
    /// </summary>
    public static bool DraftsOptimizationPlan(ReviewDecision decision, int stepCount) =>
        decision != ReviewDecision.ReleaseEligible && stepCount > 0;

    /// <summary>
    /// A <c>review_required</c> decision needs an optimisation plan with at least one step: without one there is nothing to
    /// approve and nothing to run, so the cycle could never continue. Such a reply is re-run once with
    /// <see cref="MissingOptimizationPlanError"/> as feedback.
    /// </summary>
    public static bool LacksOptimizationPlan(ReviewDecision decision, int stepCount) =>
        decision == ReviewDecision.ReviewRequired && stepCount < 1;

    /// <summary>
    /// The decision recorded for <paramref name="assessment"/> given the plan's <paramref name="stepCount"/>: a
    /// <c>review_required</c> decision without optimisation steps (still, after the corrective re-run) escalates with
    /// <see cref="EscalationReason.NoOptimizationPlan"/> so a human decides; every other decision is unchanged.
    /// </summary>
    public static ReviewAssessmentResult RequireOptimizationPlan(ReviewAssessmentResult assessment, int stepCount)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return LacksOptimizationPlan(assessment.Decision.Decision, stepCount)
            ? assessment with { Decision = new ReviewDecisionResult(ReviewDecision.EscalationRequired, EscalationReason.NoOptimizationPlan) }
            : assessment;
    }

    /// <summary>The corrective feedback for a reply that needs optimisation steps and has none (<c>INVALID_OUTPUT</c>).</summary>
    public static DomainError MissingOptimizationPlanError() =>
        new(ErrorCodes.InvalidOutput, "The review requires another cycle but proposes no optimization plan steps.")
        {
            Details =
            [
                "Another review cycle is required, but \"optimizationPlan\" has no steps. Propose at least one step that addresses the violations, missing features and drift you reported.",
            ],
        };
}
