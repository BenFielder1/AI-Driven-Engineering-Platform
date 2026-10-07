using Aide.Domain.Gates;

namespace Aide.Domain.Review;

/// <summary>
/// Inputs to the §4.5 table: score s, threshold t, drift d, cycle c (the project's <c>review_cycle</c>),
/// max cycles m = <c>max_review_cycles</c> + <c>extra_review_cycles</c>, and the previous cycle's score.
/// </summary>
public sealed record ReviewDecisionInput(
    decimal Score,
    decimal Threshold,
    DriftLevel Drift,
    int Cycle,
    int MaxReviewCycles,
    int ExtraReviewCycles = 0,
    decimal? PreviousScore = null)
{
    public int EffectiveMaxCycles => ReviewCycleRules.EffectiveMaxCycles(MaxReviewCycles, ExtraReviewCycles);
}

/// <summary>
/// Review cycle counters on the project: <c>review_cycle</c> (current cycle; 0 = none yet; reset by rollback)
/// and <c>extra_review_cycles</c> (raised by an escalation <c>continue</c>).
/// </summary>
public static class ReviewCycleRules
{
    /// <summary>§4.8: rollback clears the review state, so the cycle counter restarts.</summary>
    public const int CycleAfterRollback = 0;

    public static int EffectiveMaxCycles(int maxReviewCycles, int extraReviewCycles) => maxReviewCycles + extraReviewCycles;

    /// <summary>The cycle number for a review that is starting now.</summary>
    public static int NextCycle(int currentCycle) => currentCycle + 1;

    /// <summary>FR-14: an escalation <c>continue</c> allows one more cycle.</summary>
    public static int ExtraAfterContinue(int extraReviewCycles) => extraReviewCycles + GateRules.ExtraCyclesPerContinue;
}

public sealed record ReviewDecisionResult(ReviewDecision Decision, EscalationReason? Reason);

/// <summary>
/// §4.5 / FR-39 / D2: the single review decision table, evaluated in row order. Low compliance escalates
/// to a human; nothing rolls back automatically.
/// </summary>
public static class ReviewDecisionRules
{
    /// <summary>Row 1: a score below this always escalates.</summary>
    public const decimal EscalationFloor = 0.75m;

    public static ReviewDecisionResult Decide(ReviewDecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Row 1
        if (input.Score < EscalationFloor)
        {
            return Escalate(EscalationReason.LowScore);
        }

        if (input.Drift == DriftLevel.Severe)
        {
            return Escalate(EscalationReason.SevereDrift);
        }

        // Row 2
        if (input.Score >= input.Threshold)
        {
            return new ReviewDecisionResult(ReviewDecision.ReleaseEligible, null);
        }

        // Row 3
        if (input.Cycle >= input.EffectiveMaxCycles)
        {
            return Escalate(EscalationReason.MaxCycles);
        }

        // Row 4
        if (input.Cycle >= 2 && input.PreviousScore is { } previous && input.Score <= previous)
        {
            return Escalate(EscalationReason.NoImprovement);
        }

        // Row 5
        return new ReviewDecisionResult(ReviewDecision.ReviewRequired, null);
    }

    private static ReviewDecisionResult Escalate(EscalationReason reason) => new(ReviewDecision.EscalationRequired, reason);
}
