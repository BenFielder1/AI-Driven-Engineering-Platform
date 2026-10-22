using Aide.Domain.Common;
using Aide.Domain.Gates;
using Aide.Domain.Review;

namespace Aide.Domain.Lifecycle;

/// <summary>
/// The only way a project's phase, status and checkpoint change (§4.1, §4.10, FR-11, CLAUDE.md rule 9).
/// The checkpoint increments on every forward transition; never on release completion (v2, D-V2-2), a review cycle,
/// a fallback or a no-op; a rollback sets it to the target; starting a new release version resets it to 1.
/// </summary>
public static class LifecycleStateMachine
{
    public static Result<PhaseTransition> Apply(LifecycleState state, TransitionRequest request, LifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);

        return request.Kind switch
        {
            TransitionKind.Advance => Advance(state, request, snapshot),
            TransitionKind.ReviewCycle => ReviewCycle(state, request, snapshot),
            TransitionKind.Rollback => Rollback(state, request),
            TransitionKind.InvalidationFallback => Fallback(state, request),
            TransitionKind.ReleaseNoop => ReleaseNoop(state, request),
            TransitionKind.ReleaseCompleted => ReleaseCompleted(state, request),
            TransitionKind.NewVersion => NewVersion(state, request, snapshot),
            TransitionKind.ReleaseAwaitingMerge => ReleaseStatusChange(state, request, ProjectStatus.Releasing, ProjectStatus.AwaitingMerge, "Release awaiting merge"),
            TransitionKind.ReleaseMergeAbandoned => ReleaseStatusChange(state, request, ProjectStatus.AwaitingMerge, ProjectStatus.Releasing, "Awaited merge abandoned"),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, null),
        };
    }

    /// <summary>
    /// FR-12: a member asks to advance. If the approval mode requires a gate for this entry and it isn't
    /// approved yet, the caller creates that gate instead (or gets <c>GATE_PENDING</c> if one is open).
    /// </summary>
    public static Result<AdvanceOutcome> RequestAdvance(LifecycleState state, LifecycleSnapshot snapshot, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (PhaseOrder.Next(state.Phase) is not { } next)
        {
            return Result.Failure<AdvanceOutcome>(new DomainError(ErrorCodes.WrongPhase, "Release is the last phase; there is nothing to advance to."));
        }

        var unmet = UnmetEntryConditions(state, next, snapshot, includeGate: false);
        if (unmet.Count > 0)
        {
            return Result.Failure<AdvanceOutcome>(PreconditionFailed(next, unmet));
        }

        if (GateRules.RequiredGate(snapshot.Mode, next) is { } gate && !snapshot.EntryGateApproved)
        {
            return snapshot.PendingGates.Contains(gate)
                ? Result.Failure<AdvanceOutcome>(new DomainError(ErrorCodes.GatePending, $"A {gate.ToWire()} gate is already pending."))
                : Result.Success(AdvanceOutcome.NeedsGate(gate));
        }

        var applied = Advance(state, TransitionRequest.Advance(reason), snapshot);
        return applied.IsSuccess
            ? Result.Success(AdvanceOutcome.Applied(applied.Value))
            : Result.Failure<AdvanceOutcome>(applied.Error!);
    }

    /// <summary>Entry conditions for <paramref name="entering"/> that the snapshot doesn't meet (§4.1 table).</summary>
    public static IReadOnlyList<string> UnmetEntryConditions(LifecycleState state, Phase entering, LifecycleSnapshot snapshot, bool includeGate = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        var (decision, escalation) = snapshot.CurrentReview(state.Version);
        var unmet = new List<string>();

        switch (entering)
        {
            case Phase.Refinement:
                break;
            case Phase.Specification:
                Require(snapshot.ProjectRequestLocked, "The project request is not approved.");
                break;
            case Phase.Planning:
                Require(snapshot.TechnicalSpecificationLocked, "The technical specification is not approved.");
                break;
            case Phase.Execution:
                Require(snapshot.TechnicalSpecificationLocked, "The technical specification is not approved.");
                Require(snapshot.ImplementationPlanLocked, "The implementation plan is not approved.");
                Require(snapshot.CurrentPlanStepCount >= 1, "The implementation plan has no steps.");
                Require(snapshot.WorkingCopyPrepared, "The Git working copy is not prepared.");
                break;
            case Phase.Review:
                Require(
                    snapshot.CurrentPlanStepCount >= 1 && snapshot.CurrentPlanCompletedStepCount >= snapshot.CurrentPlanStepCount,
                    $"Not all steps of the current plan are completed ({snapshot.CurrentPlanCompletedStepCount} of {snapshot.CurrentPlanStepCount}).");
                break;
            case Phase.Release:
                Require(
                    decision == ReviewDecision.ReleaseEligible || escalation == EscalationDecision.Accept,
                    "The latest review decision for this project version is not release_eligible and no escalation was accepted.");
                var otherPending = snapshot.PendingGates.Where(g => g != GateType.PreRelease).Distinct().ToList();
                Require(otherPending.Count == 0, $"Other gates are pending: {string.Join(", ", otherPending.Select(g => g.ToWire()))}.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entering), entering, null);
        }

        if (includeGate && GateRules.RequiredGate(snapshot.Mode, entering) is { } gate)
        {
            Require(snapshot.EntryGateApproved, $"The {gate.ToWire()} gate is not approved.");
        }

        return unmet;

        void Require(bool condition, string reason)
        {
            if (!condition)
            {
                unmet.Add(reason);
            }
        }
    }

    private static Result<PhaseTransition> Advance(LifecycleState state, TransitionRequest request, LifecycleSnapshot snapshot)
    {
        if (PhaseOrder.Next(state.Phase) is not { } next)
        {
            return WrongPhase("Release is the last phase; there is nothing to advance to.");
        }

        if (request.TargetPhase is { } target && target != next)
        {
            return WrongPhase($"Forward transitions go to the next phase only: {state.Phase.ToWire()} → {next.ToWire()}, not {target.ToWire()}.");
        }

        var unmet = UnmetEntryConditions(state, next, snapshot);
        if (unmet.Count > 0)
        {
            return Result.Failure<PhaseTransition>(PreconditionFailed(next, unmet));
        }

        return Success(state, next, PhaseOrder.StatusFor(next), state.Version + 1, request, $"Advanced to {next.ToWire()}");
    }

    private static Result<PhaseTransition> ReviewCycle(LifecycleState state, TransitionRequest request, LifecycleSnapshot snapshot)
    {
        if (state.Phase != Phase.Review || (request.TargetPhase is { } target && target != Phase.Execution))
        {
            return WrongPhase("A review cycle moves review → execution only.");
        }

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

        if (unmet.Count > 0)
        {
            return Result.Failure<PhaseTransition>(PreconditionFailed(Phase.Execution, unmet));
        }

        return Success(state, Phase.Execution, ProjectStatus.Building, state.Version, request, "Review cycle: re-entering execution");
    }

    private static Result<PhaseTransition> Rollback(LifecycleState state, TransitionRequest request)
    {
        if (request.TargetVersion is not { } version || version < 1 || version >= state.Version)
        {
            return Result.Failure<PhaseTransition>(new DomainError(
                ErrorCodes.InvalidTargetVersion,
                $"The target checkpoint must be an existing checkpoint below the current checkpoint {state.Version}."));
        }

        if (request.TargetPhase is not { } target || state.Phase.IsBefore(target))
        {
            return WrongPhase("A rollback can't move to a later phase.");
        }

        return Success(state, target, ProjectStatus.RolledBack, version, request, $"Rolled back to checkpoint {version}");
    }

    private static Result<PhaseTransition> Fallback(LifecycleState state, TransitionRequest request)
    {
        if (request.TargetPhase is not { } target || !target.IsBefore(state.Phase))
        {
            return WrongPhase("An invalidation fallback moves to an earlier phase only.");
        }

        return Success(state, target, PhaseOrder.StatusFor(target), state.Version, request, $"Fell back to {target.ToWire()} after invalidation");
    }

    private static Result<PhaseTransition> ReleaseNoop(LifecycleState state, TransitionRequest request)
    {
        if (state.Phase != Phase.Release || (request.TargetPhase is { } target && target != Phase.Release))
        {
            return WrongPhase("release → release is only valid in the release phase.");
        }

        return Success(state, Phase.Release, state.Status, state.Version, request, "No-op");
    }

    /// <summary>
    /// The release job (from <c>releasing</c>) or the merge poll (from <c>awaiting_merge</c>, FR-65) finished: status
    /// <c>released</c>, checkpoint unchanged.
    /// </summary>
    private static Result<PhaseTransition> ReleaseCompleted(LifecycleState state, TransitionRequest request)
    {
        if (state.Phase != Phase.Release || state.Status is not (ProjectStatus.Releasing or ProjectStatus.AwaitingMerge))
        {
            return Result.Failure<PhaseTransition>(new DomainError(ErrorCodes.NotReleasing, "The project is not releasing."));
        }

        // §4.10 (amends §4.1): release completion no longer creates a checkpoint.
        return Success(state, Phase.Release, ProjectStatus.Released, state.Version, request, "Release completed");
    }

    /// <summary>FR-65: a status-only move within release (<paramref name="from"/> → <paramref name="to"/>), checkpoint unchanged.</summary>
    private static Result<PhaseTransition> ReleaseStatusChange(LifecycleState state, TransitionRequest request, ProjectStatus from, ProjectStatus to, string defaultReason)
    {
        if (request.TargetPhase is { } target && target != Phase.Release)
        {
            return WrongPhase("Awaiting a release merge stays in the release phase.");
        }

        if (state.Phase != Phase.Release || state.Status != from)
        {
            return Result.Failure<PhaseTransition>(new DomainError(
                ErrorCodes.NotReleasing,
                $"The project is in {state.Phase.ToWire()} with status {state.Status.ToWire()}, not release with status {from.ToWire()}."));
        }

        return Success(state, Phase.Release, to, state.Version, request, defaultReason);
    }

    /// <summary>
    /// §4.10, FR-60: <c>release → refinement</c> at checkpoint 1, status <c>active</c>; only from <c>release</c>/<c>released</c>
    /// (<c>NOT_RELEASED</c>) with no version in development (<c>VERSION_IN_PROGRESS</c>).
    /// </summary>
    private static Result<PhaseTransition> NewVersion(LifecycleState state, TransitionRequest request, LifecycleSnapshot snapshot)
    {
        if (request.TargetPhase is { } target && target != Phase.Refinement)
        {
            return WrongPhase("A new version starts in refinement.");
        }

        if (state.Phase != Phase.Release || state.Status != ProjectStatus.Released)
        {
            return Result.Failure<PhaseTransition>(new DomainError(
                ErrorCodes.NotReleased, $"A new version can only start once the project is released; it is in {state.Phase.ToWire()} with status {state.Status.ToWire()}."));
        }

        if (snapshot.DevelopmentVersionExists)
        {
            return Result.Failure<PhaseTransition>(new DomainError(ErrorCodes.VersionInProgress, "A version is already in development."));
        }

        return Success(state, Phase.Refinement, ProjectStatus.Active, LifecycleState.Initial.Version, request, "New version started");
    }

    private static Result<PhaseTransition> Success(
        LifecycleState state,
        Phase to,
        ProjectStatus status,
        int version,
        TransitionRequest request,
        string defaultReason) =>
        Result.Success(new PhaseTransition(
            state.Phase,
            to,
            status,
            version,
            request.Kind,
            string.IsNullOrWhiteSpace(request.Reason) ? defaultReason : request.Reason));

    private static Result<PhaseTransition> WrongPhase(string message) =>
        Result.Failure<PhaseTransition>(new DomainError(ErrorCodes.WrongPhase, message));

    private static DomainError PreconditionFailed(Phase entering, IReadOnlyList<string> unmet) =>
        new(ErrorCodes.PhasePreconditionFailed, $"Can't enter {entering.ToWire()}: {string.Join(" ", unmet)}") { Details = unmet };
}
