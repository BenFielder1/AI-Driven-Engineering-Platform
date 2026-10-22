namespace Aide.Domain.Agents;

/// <summary>The cognitive agents (§1, FR-23), plus the Repository Analyst (FR-70).</summary>
public enum AgentKind
{
    IdeaRefinement = 0,
    SpecGenerator = 1,
    PlanGenerator = 2,
    CodeGenerator = 3,
    CodeReview = 4,

    /// <summary>FR-70: summarises an existing codebase at a release version's base commit (<c>codebase_summary</c>).</summary>
    RepositoryAnalyst = 5,
}
