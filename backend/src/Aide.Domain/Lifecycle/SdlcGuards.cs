using Aide.Domain.Review;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Pack.Sdlc;

namespace Aide.Domain.Lifecycle;

/// <summary>
/// FR-290, §4.52: the fact the SDLC guards read from a guard context's fact bag (keyed by this type): the run's lifecycle
/// position and the snapshot <c>LifecycleSnapshotLoader</c> loads. Putting it in the bag is the caller's job (M58).
/// </summary>
public sealed record SdlcLifecycleFacts(LifecycleState State, LifecycleSnapshot Snapshot);

/// <summary>
/// FR-290, §4.52: the SDLC guards of <c>sdlc.pipeline.yaml</c> (<see cref="SdlcGuardNames"/>), pure functions of the
/// lifecycle state and snapshot returning 0.6.1's unmet reasons verbatim. With the engine's built-ins
/// (<c>artifact_locked</c>, <c>no_pending_gates</c>) in the order the definition declares them, they give 0.6.1's entry
/// conditions without the gate (<see cref="SdlcMachine.UnmetEntryConditions"/>), and
/// <see cref="ReviewCycleReady"/> the review cycle's reasons. The host adapts them to <c>IGuard</c> (D-0.7.0-23).
/// </summary>
public static class SdlcGuards
{
    /// <summary>The unmet reasons of guard <paramref name="name"/>; throws <see cref="ArgumentOutOfRangeException"/> for
    /// a name that isn't an SDLC guard.</summary>
    public static IReadOnlyList<string> Evaluate(string name, LifecycleState state, LifecycleSnapshot snapshot) => name switch
    {
        SdlcGuardNames.StartOnly => StartOnly(),
        SdlcGuardNames.LeavingAnalysis => LeavingAnalysis(state, snapshot),
        SdlcGuardNames.PlanHasSteps => PlanHasSteps(snapshot),
        SdlcGuardNames.WorkingCopyPrepared => WorkingCopyPrepared(snapshot),
        SdlcGuardNames.PlanStepsCompleted => PlanStepsCompleted(snapshot),
        SdlcGuardNames.ReleaseEligible => ReleaseEligible(state, snapshot),
        SdlcGuardNames.ReviewCycleReady => ReviewCycleReady(state, snapshot),
        // FR-314: an output check (the planning stage's validate:), never an entry rule.
        SdlcGuardNames.PlanValid => [],
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not an SDLC guard."),
    };

    /// <summary>§4.27: <c>analysis</c> is only a start stage; entering it any other way is refused.</summary>
    public static IReadOnlyList<string> StartOnly() => ["Analysis is only entered when a version that infers its stack starts."];

    /// <summary>
    /// FR-179, D-V5-5: leaving <c>analysis</c> for <c>refinement</c> (every approval mode). Applies only while the run is in
    /// <c>analysis</c>; entering <c>refinement</c> from anywhere else has no condition.
    /// </summary>
    public static IReadOnlyList<string> LeavingAnalysis(LifecycleState state, LifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (state.Phase != Phase.Analysis)
        {
            return [];
        }

        var unmet = new List<string>();
        if (!snapshot.StackProfileLocked)
        {
            unmet.Add("The stack profile is not approved.");
        }

        if (snapshot.StackProfileLocked && !snapshot.StackCompositionPinned && !snapshot.StackProfileGeneralist)
        {
            unmet.Add("No stack composition is pinned and the profile doesn't choose No stack (general).");
        }

        return unmet;
    }

    /// <summary>Entering <c>execution</c>: the current plan has at least one step.</summary>
    public static IReadOnlyList<string> PlanHasSteps(LifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.CurrentPlanStepCount >= 1 ? [] : ["The implementation plan has no steps."];
    }

    /// <summary>Entering <c>execution</c>: the Git working copy is prepared.</summary>
    public static IReadOnlyList<string> WorkingCopyPrepared(LifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.WorkingCopyPrepared ? [] : ["The Git working copy is not prepared."];
    }

    /// <summary>Entering <c>review</c>: the current plan has steps and all of them are completed.</summary>
    public static IReadOnlyList<string> PlanStepsCompleted(LifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.CurrentPlanStepCount >= 1 && snapshot.CurrentPlanCompletedStepCount >= snapshot.CurrentPlanStepCount
            ? []
            : [$"Not all steps of the current plan are completed ({snapshot.CurrentPlanCompletedStepCount} of {snapshot.CurrentPlanStepCount})."];
    }

    /// <summary>
    /// Entering <c>release</c>: the latest review for the current checkpoint is <c>release_eligible</c>, or its escalation
    /// chose <c>accept</c>. A report from another checkpoint (before a rollback) doesn't count.
    /// </summary>
    public static IReadOnlyList<string> ReleaseEligible(LifecycleState state, LifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        var (decision, escalation) = snapshot.CurrentReview(state.Version);
        return decision == ReviewDecision.ReleaseEligible || escalation == EscalationDecision.Accept
            ? []
            : ["The latest review decision for this project version is not release_eligible and no escalation was accepted."];
    }

    /// <summary>
    /// FR-14, FR-40: the review loop (<c>review → execution</c>): a review cycle is due (<c>review_required</c>, or an
    /// escalation chose <c>continue</c>), the cycle's optimisation plan is approved with steps left, and the working copy
    /// is prepared. The four reasons are 0.6.1's, in its order.
    /// </summary>
    public static IReadOnlyList<string> ReviewCycleReady(LifecycleState state, LifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        var (decision, escalation) = snapshot.CurrentReview(state.Version);
        var unmet = new List<string>();
        if (decision != ReviewDecision.ReviewRequired && escalation != EscalationDecision.Continue)
        {
            unmet.Add("The latest review decision for this project version is not review_required and no escalation chose continue.");
        }

        if (!snapshot.OptimizationPlanLocked)
        {
            unmet.Add("The latest review's optimization plan is not approved.");
        }

        if (snapshot.OptimizationPlanStepCount < 1)
        {
            unmet.Add("The optimization plan has no steps left to run.");
        }

        if (!snapshot.WorkingCopyPrepared)
        {
            unmet.Add("The Git working copy is not prepared.");
        }

        return unmet;
    }

    /// <summary>FR-186, §4.27: a new run's start stage: <c>analysis</c> for a version that infers its stack, else
    /// <c>refinement</c> (stage ids are the phases' wire values). <c>STACK_INFER_NO_CODE</c> stays in the SDLC service.</summary>
    public static string StartStage(bool infersStack) => (infersStack ? Phase.Analysis : Phase.Refinement).ToWire();
}
