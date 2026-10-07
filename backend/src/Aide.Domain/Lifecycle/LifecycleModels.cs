using Aide.Domain.Gates;
using Aide.Domain.Review;

namespace Aide.Domain.Lifecycle;

/// <summary>The project's lifecycle position.</summary>
public sealed record LifecycleState(Phase Phase, ProjectStatus Status, int Version)
{
    public static LifecycleState Initial { get; } = new(Phase.Refinement, ProjectStatus.Active, 1);
}

/// <summary>A requested lifecycle move.</summary>
public sealed record TransitionRequest(TransitionKind Kind, Phase? TargetPhase = null, int? TargetVersion = null, string? Reason = null)
{
    public static TransitionRequest Advance(string? reason = null) => new(TransitionKind.Advance, Reason: reason);

    public static TransitionRequest AdvanceTo(Phase target, string? reason = null) => new(TransitionKind.Advance, target, Reason: reason);

    public static TransitionRequest ReviewCycle(string? reason = null) => new(TransitionKind.ReviewCycle, Phase.Execution, Reason: reason);

    public static TransitionRequest Rollback(Phase targetPhase, int targetVersion, string? reason = null) =>
        new(TransitionKind.Rollback, targetPhase, targetVersion, reason);

    public static TransitionRequest Fallback(Phase targetPhase, string? reason = null) =>
        new(TransitionKind.InvalidationFallback, targetPhase, Reason: reason);

    public static TransitionRequest ReleaseNoop(string? reason = null) => new(TransitionKind.ReleaseNoop, Phase.Release, Reason: reason);

    public static TransitionRequest ReleaseCompleted(string? reason = null) => new(TransitionKind.ReleaseCompleted, Phase.Release, Reason: reason);
}

/// <summary>
/// Facts the state machine needs to check entry conditions (§4.1). The caller loads them; the machine
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

    /// <summary>The optimisation plan for the next review cycle is approved (locked).</summary>
    public bool OptimizationPlanLocked { get; init; }

    /// <summary>Steps of the approved optimisation plan.</summary>
    public int OptimizationPlanStepCount { get; init; }

    /// <summary>The latest review decision and escalation outcome, or nulls when the report isn't for <paramref name="projectVersion"/>.</summary>
    public (ReviewDecision? Decision, EscalationDecision? Escalation) CurrentReview(int projectVersion) =>
        LatestReviewProjectVersion == projectVersion ? (LatestReviewDecision, EscalationOutcome) : (null, null);

    /// <summary>True when the gate guarding entry to the target phase has been approved (the approval is the trigger).</summary>
    public bool EntryGateApproved { get; init; }

    public IReadOnlyCollection<GateType> PendingGates { get; init; } = [];
}

/// <summary>An applied lifecycle move: what to write as the state transition and the new project state.</summary>
public sealed record PhaseTransition(
    Phase From,
    Phase To,
    ProjectStatus Status,
    int Version,
    TransitionKind Kind,
    string Reason)
{
    public LifecycleState NewState => new(To, Status, Version);
}

/// <summary>Result of "advance to next phase": either the move was applied, or a gate must be created first.</summary>
public sealed record AdvanceOutcome(PhaseTransition? Transition, GateType? GateRequired)
{
    public static AdvanceOutcome Applied(PhaseTransition transition) => new(transition, null);

    public static AdvanceOutcome NeedsGate(GateType gate) => new(null, gate);
}
