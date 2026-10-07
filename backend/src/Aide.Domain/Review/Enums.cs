namespace Aide.Domain.Review;

public enum ReviewDecision
{
    ReleaseEligible = 0,
    ReviewRequired = 1,
    EscalationRequired = 2,
}

/// <summary>Why a review escalated (§4.5 rows 1, 3, 4).</summary>
public enum EscalationReason
{
    LowScore = 0,
    SevereDrift = 1,
    MaxCycles = 2,
    NoImprovement = 3,
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
