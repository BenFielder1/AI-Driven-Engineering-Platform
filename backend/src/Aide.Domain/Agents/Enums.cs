namespace Aide.Domain.Agents;

/// <summary>The five cognitive agents (§1, FR-23).</summary>
public enum AgentKind
{
    IdeaRefinement = 0,
    SpecGenerator = 1,
    PlanGenerator = 2,
    CodeGenerator = 3,
    CodeReview = 4,
}
