namespace Aide.Domain.Agents;

/// <summary>The cognitive agents (§1, FR-23), plus the Repository Analyst (FR-70) and the Stack Analyst (FR-180).</summary>
public enum AgentKind
{
    IdeaRefinement = 0,
    SpecGenerator = 1,
    PlanGenerator = 2,
    CodeGenerator = 3,
    CodeReview = 4,

    /// <summary>FR-70: summarises an existing codebase at a release version's base commit (<c>codebase_summary</c>).</summary>
    RepositoryAnalyst = 5,

    /// <summary>FR-180: infers a version's stack from its repository into a <c>stack_profile</c> draft (D-V5-7).</summary>
    StackAnalyst = 6,
}
