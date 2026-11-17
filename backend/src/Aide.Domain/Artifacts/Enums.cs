namespace Aide.Domain.Artifacts;

public enum ArtifactType
{
    ProjectRequest = 0,
    TechnicalSpecification = 1,
    ImplementationPlan = 2,
    OptimizationPlan = 3,

    /// <summary>FR-70: the Repository Analyst's summary of the codebase at the version's base commit.</summary>
    CodebaseSummary = 4,

    /// <summary>FR-170: the version's active tracker imports rendered to Markdown (§4.26); auto-locked, invalidates nothing.</summary>
    SourceItems = 5,

    /// <summary>
    /// FR-180, §4.27: the Stack Analyst's findings for a version that infers its stack (layers, hosts, structure), edited,
    /// approved or rejected on the Analysis page. Approving it pins the composition.
    /// </summary>
    StackProfile = 6,
}
