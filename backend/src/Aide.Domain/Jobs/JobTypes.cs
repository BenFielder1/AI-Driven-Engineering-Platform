using Aide.Domain.Agents;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;

namespace Aide.Domain.Jobs;

/// <summary>
/// Job types of the PostgreSQL work queue (§4.9, D1). Values are stored in <c>jobs.type</c>, so they never change once
/// used. A job type is only claimed by a worker once a handler is registered for it; until then its jobs stay queued.
/// </summary>
public static class JobTypes
{
    /// <summary>FR-27: one idea refinement round.</summary>
    public const string IdeaRefinement = "idea_refinement";

    /// <summary>FR-28: generate the technical specification (also re-run after a rejected spec_approval gate, D16).</summary>
    public const string SpecGeneration = "spec_generation";

    /// <summary>FR-29: generate the implementation plan (also re-run after a rejected plan_approval gate).</summary>
    public const string PlanGeneration = "plan_generation";

    /// <summary>FR-31–FR-34: run the current plan's steps. At most one queued or running per project (§4.9).</summary>
    public const string Execution = Job.ExecutionType;

    /// <summary>
    /// FR-36: when every step of the current plan is completed, push the project branch, open (or reuse) the one project PR
    /// (GitHub) or record the branch comparison (Local), then advance execution → review.
    /// </summary>
    public const string PullRequest = "pull_request";

    /// <summary>FR-38: one review cycle.</summary>
    public const string Review = "review";

    /// <summary>FR-42: merge and tag the release.</summary>
    public const string Release = "release";

    /// <summary>The job that runs <paramref name="agent"/>.</summary>
    public static string ForAgent(AgentKind agent) => agent switch
    {
        AgentKind.IdeaRefinement => IdeaRefinement,
        AgentKind.SpecGenerator => SpecGeneration,
        AgentKind.PlanGenerator => PlanGeneration,
        AgentKind.CodeGenerator => Execution,
        AgentKind.CodeReview => Review,
        _ => throw new ArgumentOutOfRangeException(nameof(agent), agent, null),
    };

    /// <summary>
    /// The follow-up job written in the same transaction as a forward transition into <paramref name="entered"/>:
    /// spec generation on entering specification (FR-28), plan generation on entering planning (FR-29), a review
    /// cycle on entering review (FR-38) and the release job on entering release (FR-42). Refinement is started by
    /// the user (FR-27) and execution by <c>POST /execution/start</c> (FR-35, FR-37), so they have none.
    /// </summary>
    public static string? OnEnter(Phase entered) => entered switch
    {
        Phase.Specification => SpecGeneration,
        Phase.Planning => PlanGeneration,
        Phase.Review => Review,
        Phase.Release => Release,
        _ => null,
    };
}
