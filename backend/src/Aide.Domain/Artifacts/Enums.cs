namespace Aide.Domain.Artifacts;

public enum ArtifactType
{
    ProjectRequest = 0,
    TechnicalSpecification = 1,
    ImplementationPlan = 2,
    OptimizationPlan = 3,

    /// <summary>FR-70: the Repository Analyst's summary of the codebase at the version's base commit.</summary>
    CodebaseSummary = 4,
}

/// <summary>Stored artifact status (§4.3). <c>approved</c> is a moment, not a stored status.</summary>
public enum ArtifactStatus
{
    Draft = 0,
    Locked = 1,
    Rejected = 2,
    Superseded = 3,
    Invalidated = 4,
    Corrupted = 5,
}
