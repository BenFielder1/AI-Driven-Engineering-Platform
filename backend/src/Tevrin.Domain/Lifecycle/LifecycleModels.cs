using Tevrin.Domain.Review;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Domain.Lifecycle;

/// <summary>
/// The project's lifecycle position. <see cref="Version"/> is the lifecycle <b>checkpoint</b> within the current release
/// version (§4.10; 0.1.0's "project version"), not the semver release version.
/// </summary>
public sealed record LifecycleState(Phase Phase, ProjectStatus Status, int Version)
{
    public static LifecycleState Initial { get; } = new(Phase.Refinement, ProjectStatus.Active, 1);
}

/// <summary>
/// Facts the SDLC guards need to check entry conditions (§4.1, §4.52). The caller loads them; the machine
/// stays pure. Fields not relevant to a move are ignored.
/// </summary>
public sealed record LifecycleSnapshot
{
    public static LifecycleSnapshot Empty { get; } = new();

    public ApprovalMode Mode { get; init; } = ApprovalMode.Advisory;

    public bool ProjectRequestLocked { get; init; }

    public bool TechnicalSpecificationLocked { get; init; }

    public bool ImplementationPlanLocked { get; init; }

    /// <summary>Steps of the current plan (implementation plan, or optimisation plan during a review cycle).</summary>
    public int CurrentPlanStepCount { get; init; }

    public int CurrentPlanCompletedStepCount { get; init; }

    public bool WorkingCopyPrepared { get; init; }

    /// <summary>Decision of the latest compliance report, if any.</summary>
    public ReviewDecision? LatestReviewDecision { get; init; }

    /// <summary>
    /// <c>project_version</c> of the latest compliance report. Its decision (and escalation outcome) only
    /// counts when this equals the current project version; a report from before a rollback is stale.
    /// </summary>
    public int? LatestReviewProjectVersion { get; init; }

    /// <summary>Decision of the escalation gate raised for the latest report, if one was decided.</summary>
    public EscalationDecision? EscalationOutcome { get; init; }

    /// <summary>
    /// The optimisation plan of the latest review's cycle (that report's draft, or a later version of it) is approved
    /// (locked). An optimisation plan of an earlier cycle doesn't count.
    /// </summary>
    public bool OptimizationPlanLocked { get; init; }

    /// <summary>Steps of that optimisation plan still to run (neither completed nor invalidated).</summary>
    public int OptimizationPlanStepCount { get; init; }

    /// <summary>The latest review decision and escalation outcome, or nulls when the report isn't for <paramref name="projectVersion"/>.</summary>
    public (ReviewDecision? Decision, EscalationDecision? Escalation) CurrentReview(int projectVersion) =>
        LatestReviewProjectVersion == projectVersion ? (LatestReviewDecision, EscalationOutcome) : (null, null);

    /// <summary>True when the gate guarding entry to the target phase has been approved (the approval is the trigger).</summary>
    public bool EntryGateApproved { get; init; }

    public IReadOnlyCollection<string> PendingGates { get; init; } = [];

    /// <summary>§4.10: a release version is <c>in_development</c> or <c>awaiting_merge</c> (blocks a new version).</summary>
    public bool DevelopmentVersionExists { get; init; }

    /// <summary>FR-179, §4.27: the release version's <c>stack_profile</c> has a locked version (leaving <c>analysis</c>).</summary>
    public bool StackProfileLocked { get; init; }

    /// <summary>FR-179, §4.27: the release version has a pinned stack composition (leaving <c>analysis</c>).</summary>
    public bool StackCompositionPinned { get; init; }

    /// <summary>FR-181, §4.27: the locked <c>stack_profile</c> chose "No stack (general)" (<c>generalist: true</c>), so no composition is needed.</summary>
    public bool StackProfileGeneralist { get; init; }

    /// <summary>FR-178, FR-186: the version being started infers its stack (<c>stack_source = infer</c>), so it starts in <c>analysis</c>.</summary>
    public bool InfersStack { get; init; }

    /// <summary>FR-178, §4.27: the version being started has a base commit with code (inference needs one).</summary>
    public bool BaseHasCode { get; init; }
}

/// <summary>
/// An applied lifecycle move (FR-292: the engine's <see cref="RunTransition"/>, or a new version's run start, in SDLC
/// terms): what to write as the state transition and the new project state. <see cref="Kind"/> is the generic kind stored
/// in <c>state_transitions.kind</c> (<see cref="PipelineTransitionKinds"/>, D-0.7.0-28) and <see cref="EventKind"/> the
/// kind 0.6.1 wrote in the <c>PHASE_CHANGED</c> payload (unchanged on the wire).
/// </summary>
public sealed record PhaseTransition(
    Phase From,
    Phase To,
    ProjectStatus Status,
    int Version,
    string Kind,
    string EventKind,
    string Reason)
{
    /// <summary>0.6.1's <c>PHASE_CHANGED</c> kinds that aren't a status change name.</summary>
    public const string AdvanceEvent = "advance";
    public const string ReviewCycleEvent = "review_cycle";
    public const string RollbackEvent = "rollback";
    public const string FallbackEvent = "invalidation_fallback";
    public const string NoopEvent = "release_noop";
    public const string NewVersionEvent = "new_version";

    public LifecycleState NewState => new(To, Status, Version);

    /// <summary>The entered stage's <c>enqueue_on_enter</c> job, for an advance only (replaces 0.6.1's <c>PhaseJobs</c>).</summary>
    public string? EnqueueOnEnter { get; init; }

    /// <summary>
    /// §4.8: a rollback clears the review state (review cycle and the extra cycles granted by escalation <c>continue</c>);
    /// so does a cascade fallback (§4.4), whose new upstream artifacts make the earlier review cycles moot, and starting a
    /// new release version (§4.10).
    /// </summary>
    public bool ClearsReviewState { get; init; }

    /// <summary>§4.10: starting a new release version also clears <c>execution_paused</c>.</summary>
    public bool ClearsExecutionPause { get; init; }

    /// <summary>The engine's applied move in SDLC terms.</summary>
    public static PhaseTransition Of(RunTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        var (kind, eventKind) = transition.Kind switch
        {
            RunTransitionKind.Advance => (PipelineTransitionKinds.Advance, AdvanceEvent),
            RunTransitionKind.Loop => (PipelineTransitionKinds.Loop, ReviewCycleEvent),
            RunTransitionKind.Rollback => (PipelineTransitionKinds.Rollback, RollbackEvent),
            RunTransitionKind.Fallback => (PipelineTransitionKinds.Fallback, FallbackEvent),
            RunTransitionKind.Noop => (PipelineTransitionKinds.Noop, NoopEvent),
            RunTransitionKind.StatusChange => (PipelineTransitionKinds.StatusChange, transition.StatusChange!),
            _ => throw new ArgumentOutOfRangeException(nameof(transition), transition.Kind, null),
        };
        return new PhaseTransition(
            SdlcStages.PhaseOf(transition.From),
            SdlcStages.PhaseOf(transition.To),
            EnumStrings.Parse<ProjectStatus>(transition.PackStatus),
            transition.Checkpoint,
            kind,
            eventKind,
            transition.Reason)
        {
            EnqueueOnEnter = transition.EnqueueOnEnter,
            ClearsReviewState = transition.ClearsLoopState,
        };
    }

    /// <summary>§4.10, FR-60: a new version's first transition, the start of its run (stored kind <c>start</c>).</summary>
    public static PhaseTransition NewVersion(Phase from, LifecycleState started, string reason)
    {
        ArgumentNullException.ThrowIfNull(started);
        return new PhaseTransition(from, started.Phase, started.Status, started.Version, PipelineTransitionKinds.Start, NewVersionEvent, reason)
        {
            ClearsReviewState = true,
            ClearsExecutionPause = true,
        };
    }
}

/// <summary>Result of "advance to next phase": either the move was applied, or a gate must be created first.</summary>
public sealed record AdvanceOutcome(PhaseTransition? Transition, string? GateRequired)
{
    public static AdvanceOutcome Applied(PhaseTransition transition) => new(transition, null);

    public static AdvanceOutcome NeedsGate(string gate) => new(null, gate);
}
