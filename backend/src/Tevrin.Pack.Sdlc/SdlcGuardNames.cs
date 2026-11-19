namespace Tevrin.Pack.Sdlc;

/// <summary>
/// FR-290, §4.52: the SDLC's guard names (namespaced by the pack id, FR-278), used in <c>sdlc.pipeline.yaml</c>. Their
/// logic is 0.6.1's entry rules in <c>Aide.Domain</c> (<c>SdlcGuards</c>), bound in the host's DI (D-0.7.0-23).
/// </summary>
public static class SdlcGuardNames
{
    /// <summary>Entering <c>analysis</c> by an advance: refused (it's only a start stage).</summary>
    public const string StartOnly = "sdlc.start-only";

    /// <summary>Leaving <c>analysis</c> for <c>refinement</c>: the stack profile is approved, and a composition is pinned
    /// or the profile is generalist.</summary>
    public const string LeavingAnalysis = "sdlc.leaving-analysis";

    /// <summary>Entering <c>execution</c>: the current plan has steps.</summary>
    public const string PlanHasSteps = "sdlc.plan-has-steps";

    /// <summary>Entering <c>execution</c> (and the review loop): the Git working copy is prepared.</summary>
    public const string WorkingCopyPrepared = "sdlc.working-copy-prepared";

    /// <summary>Entering <c>review</c>: every step of the current plan is completed.</summary>
    public const string PlanStepsCompleted = "sdlc.plan-steps-completed";

    /// <summary>Entering <c>release</c>: the latest review for this checkpoint is <c>release_eligible</c>, or an
    /// escalation chose <c>accept</c>.</summary>
    public const string ReleaseEligible = "sdlc.release-eligible";

    /// <summary>The review loop (<c>review → execution</c>): a review cycle is due and its optimisation plan is approved,
    /// with steps left, and the working copy is prepared.</summary>
    public const string ReviewCycleReady = "sdlc.review-cycle-ready";

    /// <summary>Every SDLC guard name.</summary>
    public static IReadOnlyList<string> All { get; } =
        [StartOnly, LeavingAnalysis, PlanHasSteps, WorkingCopyPrepared, PlanStepsCompleted, ReleaseEligible, ReviewCycleReady];
}
