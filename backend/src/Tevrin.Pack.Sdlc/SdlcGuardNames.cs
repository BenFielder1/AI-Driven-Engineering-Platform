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

    /// <summary>
    /// FR-314, FR-321: the <c>planning</c> stage's <c>validate:</c> guard: the generated plan reads and passes the plan rules
    /// (<c>INVALID_PLAN</c>). An output check, not an entry rule: it has no lifecycle condition.
    /// </summary>
    public const string PlanValid = "sdlc.plan-valid";

    /// <summary>FR-314, D-0.7.0-44: the <c>specification</c> stage's <c>validate:</c> guard: the spec has its required
    /// sections (<c>MISSING_SECTIONS</c>). An output check, like <see cref="PlanValid"/>.</summary>
    public const string SpecSections = "sdlc.spec-sections";

    /// <summary>FR-314, D-0.7.0-44: the <c>analysis</c> stage's guard: the stack profile reads and names only allowed
    /// options (<c>STACK_PROFILE_INVALID</c>).</summary>
    public const string StackProfileValid = "sdlc.stack-profile-valid";

    /// <summary>FR-314, D-0.7.0-44: the <c>refinement</c> stage's guard: the refined request reads (<c>INVALID_OUTPUT</c>).</summary>
    public const string RefinementValid = "sdlc.refinement-valid";

    /// <summary>FR-314, D-0.7.0-44: the <c>repository_analyst</c> binding's guard: the codebase summary has its sections
    /// (<c>MISSING_SECTIONS</c>).</summary>
    public const string SummarySections = "sdlc.summary-sections";

    /// <summary>FR-314, D-0.7.0-44: the <c>review</c> stage's guard: the first reply reads and meets the requirement, the
    /// corrective re-run only reads.</summary>
    public const string ReviewValid = "sdlc.review-valid";

    /// <summary>FR-314, D-0.7.0-44: the <c>build_fix</c> binding's guard: the first fix plan reads and passes the plan
    /// rules; the corrective re-run is accepted as sent.</summary>
    public const string FixPlanValid = "sdlc.fix-plan-valid";

    /// <summary>Every SDLC guard name.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        StartOnly, LeavingAnalysis, PlanHasSteps, WorkingCopyPrepared, PlanStepsCompleted, ReleaseEligible, ReviewCycleReady, PlanValid,
        SpecSections, StackProfileValid, RefinementValid, SummarySections, ReviewValid, FixPlanValid,
    ];

    /// <summary>The output guards, one per checked agent (D-0.7.0-44): the guard that runs the agent's reply check.</summary>
    public static IReadOnlyDictionary<string, string> OutputCheckOf { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SdlcAgents.PlanGenerator] = PlanValid,
        [SdlcAgents.SpecGenerator] = SpecSections,
        [SdlcAgents.StackAnalyst] = StackProfileValid,
        [SdlcAgents.IdeaRefinement] = RefinementValid,
        [SdlcAgents.RepositoryAnalyst] = SummarySections,
        [SdlcAgents.CodeReview] = ReviewValid,
        [SdlcAgents.BuildFix] = FixPlanValid,
    };

    /// <summary>The output guards other than <see cref="PlanValid"/>: they apply the attempt's own reply check.</summary>
    public static IReadOnlyList<string> ReplyChecks { get; } = [SpecSections, StackProfileValid, RefinementValid, SummarySections, ReviewValid, FixPlanValid];
}
