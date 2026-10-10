namespace Aide.Domain.Review;

public enum ReviewDecision
{
    ReleaseEligible = 0,
    ReviewRequired = 1,
    EscalationRequired = 2,
}

/// <summary>Why a review escalated (§4.5 rows 1, 3, 4; a blocked step; a review_required reply without an optimisation plan).</summary>
public enum EscalationReason
{
    LowScore = 0,
    SevereDrift = 1,
    MaxCycles = 2,
    NoImprovement = 3,

    /// <summary>FR-33: an execution step was rejected on its fourth attempt and is blocked.</summary>
    StepBlocked = 4,

    /// <summary>
    /// The review required another cycle but the Code Review agent proposed no optimisation steps, even after the
    /// corrective re-run: nothing could re-enter execution, so a human decides (FR-38, FR-39).
    /// </summary>
    NoOptimizationPlan = 5,
}

public enum DriftLevel
{
    None = 0,
    Low = 1,
    Moderate = 2,
    Severe = 3,
}

/// <summary>Severity of a review finding or drift area (§4.7).</summary>
public enum Severity
{
    Minor = 0,
    Major = 1,
    Critical = 2,
}

/// <summary>Compliance categories (§4.7).</summary>
public enum ReviewCategory
{
    FeatureCoverage = 0,
    ApiContracts = 1,
    DataModel = 2,
    Architecture = 3,
    Security = 4,
    Testing = 5,
}

/// <summary>Where a review violation came from (§4.7): a deterministic check or the Code Review agent.</summary>
public enum FindingSource
{
    Deterministic = 0,
    Agent = 1,
}

/// <summary>The review job's state for the project (FR-38): no review in the current cycle, queued or running, finished, or failed.</summary>
public enum ReviewRunStatus
{
    Idle = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}
