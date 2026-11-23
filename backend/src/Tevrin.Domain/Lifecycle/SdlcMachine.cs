using Tevrin.Domain.Artifacts;
using Tevrin.Domain.Common;
using Tevrin.Domain.Review;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Domain.Lifecycle;

/// <summary>
/// The project's review-loop position for the engine: <see cref="ReviewCycle"/> is <c>projects.review_cycle</c> (the
/// authoritative review counter, mirrored into the run's <c>review</c> iteration count, D-0.7.0-27) and
/// <see cref="ReviewCycles"/> the cap <c>maxReviewCycles + extraReviewCycles</c> (the <c>review_cycles</c> setting, FR-14,
/// never below the cycle + 1: D-0.7.0-30).
/// </summary>
public sealed record ReviewLoopState(int ReviewCycle, int ReviewCycles)
{
    /// <summary>No review cycle yet (moves that don't loop ignore the loop position).</summary>
    public static ReviewLoopState None { get; } = new(0, 1);

    /// <summary>The loop position of a project with these counters (<see cref="SdlcReviewLoop.ReviewCycles"/>).</summary>
    public static ReviewLoopState Of(int reviewCycle, int maxReviewCycles, int extraReviewCycles) =>
        new(reviewCycle, SdlcReviewLoop.ReviewCycles(maxReviewCycles, extraReviewCycles, reviewCycle));
}

/// <summary>
/// D-0.7.0-30, FR-14: the SDLC's <c>review_cycles</c> run setting, the engine's review-loop cap. It is
/// <c>maxReviewCycles + extraReviewCycles</c>, but never below the current cycle + 1, so the engine's backstop never refuses
/// a review cycle 0.6.1 allows (e.g. after an Owner lowers <c>maxReviewCycles</c> below the current cycle). The review
/// decision rules (escalation at the max, <see cref="ReviewDecisionRules"/>) stay the real limit.
/// </summary>
public static class SdlcReviewLoop
{
    public static int ReviewCycles(int maxReviewCycles, int extraReviewCycles, int reviewCycle) =>
        Math.Max(ReviewCycleRules.EffectiveMaxCycles(maxReviewCycles, extraReviewCycles), reviewCycle + 1);
}

/// <summary>FR-65, §4.51: the SDLC definition's <c>status_changes</c> names (also 0.6.1's <c>PHASE_CHANGED</c> kinds).</summary>
public static class SdlcStatusChanges
{
    public const string ReleaseCompleted = "release_completed";
    public const string ReleaseAwaitingMerge = "release_awaiting_merge";
    public const string ReleaseMergeAbandoned = "release_merge_abandoned";
}

/// <summary>
/// FR-292: the SDLC's lifecycle moves as the engine's <see cref="Transition"/>s (§4.51). A review cycle is the
/// <c>review</c> loop with outcome <c>review_required</c> (the SDLC caller decides the cycle is due, D-0.7.0-27); the
/// release's status-only moves are the definition's <c>status_changes</c>. Starting a new version is not a move: it is a
/// new run (<see cref="SdlcMachine.NewVersion"/>, D-0.7.0-29).
/// </summary>
public static class SdlcTransitions
{
    public static Transition Advance(string? reason = null) => Transition.Advance(reason);

    public static Transition AdvanceTo(Phase target, string? reason = null) => Transition.AdvanceTo(target.ToWire(), reason);

    /// <summary>FR-14, FR-40: <c>review → execution</c> for another review cycle (<paramref name="target"/> is checked).</summary>
    public static Transition ReviewCycle(string? reason = null, Phase target = Phase.Execution) =>
        new(RunTransitionKind.Loop, target.ToWire(), LoopOutcome: ReviewDecision.ReviewRequired.ToWire(), Reason: reason);

    public static Transition Rollback(Phase target, int checkpoint, string? reason = null) =>
        Transition.Rollback(target.ToWire(), checkpoint, reason: reason);

    public static Transition Fallback(Phase target, string? reason = null) => Transition.Fallback(target.ToWire(), reason);

    public static Transition ReleaseNoop(string? reason = null, Phase target = Phase.Release) =>
        new(RunTransitionKind.Noop, target.ToWire(), Reason: reason);

    public static Transition ReleaseCompleted(string? reason = null) => Transition.Change(SdlcStatusChanges.ReleaseCompleted, reason);

    /// <summary>FR-65: the release merge was refused or the strategy is manual (<c>releasing → awaiting_merge</c>).</summary>
    public static Transition ReleaseAwaitingMerge(string? reason = null) => Transition.Change(SdlcStatusChanges.ReleaseAwaitingMerge, reason);

    /// <summary>FR-65: <c>PR_CLOSED</c> or <c>MERGE_TIMEOUT</c> (<c>awaiting_merge → releasing</c>).</summary>
    public static Transition ReleaseMergeAbandoned(string? reason = null) => Transition.Change(SdlcStatusChanges.ReleaseMergeAbandoned, reason);
}

/// <summary>
/// FR-293: the SDLC's phase and gate lookups, read from its pipeline definition (they replace 0.6.1's phase order,
/// status-follows-phase table and gate tables). Stage ids are the <see cref="Phase"/> wire values and stage statuses the
/// <see cref="ProjectStatus"/> wire values.
/// </summary>
public static class SdlcStages
{
    /// <summary>Every phase in the definition's stage order.</summary>
    public static IReadOnlyList<Phase> All(PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return [.. definition.Stages.Select(s => PhaseOf(s.Id))];
    }

    public static Phase PhaseOf(string stageId) => EnumStrings.Parse<Phase>(stageId);

    public static StageDefinition Stage(PipelineDefinition definition, Phase phase)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.FindStage(phase.ToWire())
            ?? throw new ArgumentOutOfRangeException(nameof(phase), phase, $"Not a stage of '{definition.Pipeline}'.");
    }

    /// <summary>The stage an advance enters from <paramref name="phase"/>, or <c>null</c> from the last stage.</summary>
    public static Phase? Next(PipelineDefinition definition, Phase phase)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.SuccessorOf(phase.ToWire()) is { } next ? PhaseOf(next) : null;
    }

    /// <summary>The status a stage is entered with (status follows phase).</summary>
    public static ProjectStatus StatusFor(PipelineDefinition definition, Phase phase) =>
        EnumStrings.Parse<ProjectStatus>(Stage(definition, phase).Status);

    /// <summary><paramref name="phase"/> comes before <paramref name="other"/> in the definition's stage order.</summary>
    public static bool IsBefore(PipelineDefinition definition, Phase phase, Phase other)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.IndexOfStage(phase.ToWire()) < definition.IndexOfStage(other.ToWire());
    }

    /// <summary>§4.53: the gate required to enter <paramref name="entering"/> in <paramref name="mode"/>, or <c>null</c>.</summary>
    public static string? RequiredGate(PipelineDefinition definition, ApprovalMode mode, Phase entering) =>
        BuiltInRequirements.RequiredGate(Stage(definition, entering), mode);

    /// <summary>The phase whose entry a gate guards; <c>null</c> for a gate no stage declares (escalation).</summary>
    public static Phase? GuardedEntry(PipelineDefinition definition, string gateType) =>
        GatedStage(definition, gateType) is { } stage ? PhaseOf(stage.Id) : null;

    /// <summary>The artifact a gate approves (its latest locked version is attached to the gate), or <c>null</c>.</summary>
    public static string? GatedArtifact(PipelineDefinition definition, string gateType) => GatedStage(definition, gateType)?.Gate?.Approves;

    /// <summary>FR-48: the gate's notifications carry one-click approve/reject links.</summary>
    public static bool SupportsApprovalLinks(PipelineDefinition definition, string gateType) => GatedStage(definition, gateType)?.Gate?.ApprovalLinks == true;

    private static StageDefinition? GatedStage(PipelineDefinition definition, string gateType)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Stages.FirstOrDefault(s => string.Equals(s.Gate?.Type, gateType, StringComparison.Ordinal));
    }
}

/// <summary>
/// FR-292, CLAUDE.md rule 8: the SDLC lifecycle on the engine. Every move goes through <see cref="PipelineStateMachine"/>
/// with the SDLC definition; this class maps the lifecycle state and snapshot to the engine's run state and facts (§4.51,
/// <see cref="SdlcGuards"/>) and the engine's result back to the <see cref="PhaseTransition"/> the writers apply. Pure. The
/// review loop position is <c>projects.review_cycle</c> (never the engine's <see cref="RunTransition.Iterations"/>).
/// <see cref="Start"/> and <see cref="NewVersion"/> are the SDLC service's own rules around the engine's run start
/// (D-0.7.0-29).
/// </summary>
public static class SdlcMachine
{
    /// <summary>The run's position: the phase, status and checkpoint, and the <c>review</c> iteration count.</summary>
    public static RunState RunStateOf(LifecycleState state, int reviewCycle)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new RunState(
            state.Phase.ToWire(),
            state.Status.ToWire(),
            state.Version,
            new Dictionary<string, int>(StringComparer.Ordinal) { [Phase.Review.ToWire()] = reviewCycle });
    }

    /// <summary>
    /// The run facts the SDLC computes: approval mode, locked artifacts, pending gates, entry-gate approval, every SDLC
    /// guard evaluated on the state and snapshot (<see cref="SdlcGuards"/>) and the <c>review_cycles</c> setting.
    /// </summary>
    public static RunFacts FactsOf(LifecycleState state, LifecycleSnapshot snapshot, int reviewCycles)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        var locked = new List<string>(4);
        if (snapshot.ProjectRequestLocked)
        {
            locked.Add(SdlcArtifacts.ProjectRequest);
        }

        if (snapshot.TechnicalSpecificationLocked)
        {
            locked.Add(SdlcArtifacts.TechnicalSpecification);
        }

        if (snapshot.ImplementationPlanLocked)
        {
            locked.Add(SdlcArtifacts.ImplementationPlan);
        }

        if (snapshot.StackProfileLocked)
        {
            locked.Add(SdlcArtifacts.StackProfile);
        }

        var guards = new Dictionary<string, GuardResult>(StringComparer.Ordinal);
        foreach (var name in SdlcGuardNames.All)
        {
            var reasons = SdlcGuards.Evaluate(name, state, snapshot);
            guards[name] = reasons.Count == 0 ? GuardResult.Pass : new GuardResult(false, reasons);
        }

        return new RunFacts
        {
            Mode = snapshot.Mode,
            LockedArtifacts = locked,
            PendingGates = snapshot.PendingGates,
            EntryGateApproved = snapshot.EntryGateApproved,
            Guards = guards,
            Settings = new Dictionary<string, int>(StringComparer.Ordinal) { [SdlcPack.ReviewCycles] = reviewCycles },
        };
    }

    /// <summary>Applies <paramref name="move"/> to the run at <paramref name="state"/> (§4.51).</summary>
    public static Result<PhaseTransition> Apply(
        LifecycleState state, Transition move, LifecycleSnapshot snapshot, ReviewLoopState loop, PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(move);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(definition);
        var applied = PipelineStateMachine.Apply(RunStateOf(state, loop.ReviewCycle), move, FactsOf(state, snapshot, loop.ReviewCycles), definition);
        return applied.IsSuccess
            ? Result.Success(PhaseTransition.Of(applied.Value))
            : Result.Failure<PhaseTransition>(applied.Error!);
    }

    /// <summary>
    /// FR-12, FR-288: a member asks to advance. If the approval mode requires a gate for this entry and it isn't approved
    /// yet, the caller creates that gate instead (or gets <c>GATE_PENDING</c> if one is open).
    /// </summary>
    public static Result<AdvanceOutcome> RequestAdvance(
        LifecycleState state, LifecycleSnapshot snapshot, ReviewLoopState loop, PipelineDefinition definition, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(definition);
        var outcome = PipelineStateMachine.RequestAdvance(RunStateOf(state, loop.ReviewCycle), FactsOf(state, snapshot, loop.ReviewCycles), definition, reason);
        if (outcome.IsFailure)
        {
            return Result.Failure<AdvanceOutcome>(outcome.Error!);
        }

        return outcome.Value.GateRequired is { } gate
            ? Result.Success(AdvanceOutcome.NeedsGate(gate))
            : Result.Success(AdvanceOutcome.Applied(PhaseTransition.Of(outcome.Value.Transition!)));
    }

    /// <summary>§4.52: the unmet entry conditions of <paramref name="entering"/> (and its gate with <paramref name="includeGate"/>).</summary>
    public static IReadOnlyList<string> UnmetEntryConditions(
        LifecycleState state, Phase entering, LifecycleSnapshot snapshot, ReviewLoopState loop, PipelineDefinition definition, bool includeGate = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(loop);
        return PipelineStateMachine.UnmetEntryConditions(
            RunStateOf(state, loop.ReviewCycle), entering.ToWire(), FactsOf(state, snapshot, loop.ReviewCycles), definition, includeGate);
    }

    /// <summary>
    /// FR-178, §4.27 "project or version created": a new project's first run. A version that infers its stack
    /// (<paramref name="infersStack"/>) starts in <c>analysis</c> and needs a base commit with code (<c>STACK_INFER_NO_CODE</c>
    /// otherwise); every other starts in <c>refinement</c> (the engine's start in the start selector's stage).
    /// </summary>
    public static Result<LifecycleState> Start(bool infersStack, bool baseHasCode, PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (infersStack && !baseHasCode)
        {
            return Result.Failure<LifecycleState>(NoCodeToInfer());
        }

        return Result.Success(StateOf(PipelineStateMachine.Start(definition, SdlcGuards.StartStage(infersStack))));
    }

    /// <summary>
    /// §4.10, FR-60, FR-186: start a new release version (a new run) entering <paramref name="entering"/>: <c>refinement</c>,
    /// or <c>analysis</c> for a version that infers its stack (<see cref="LifecycleSnapshot.InfersStack"/>); only from
    /// <c>release</c>/<c>released</c> (<c>NOT_RELEASED</c>) with no version in development (<c>VERSION_IN_PROGRESS</c>), and an
    /// inferring version needs a base commit with code (<c>STACK_INFER_NO_CODE</c>). The new run's start comes from the engine.
    /// </summary>
    public static Result<PhaseTransition> NewVersion(
        LifecycleState state, Phase entering, LifecycleSnapshot snapshot, PipelineDefinition definition, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(definition);
        if (entering is not (Phase.Refinement or Phase.Analysis))
        {
            return WrongPhase("A new version starts in refinement, or in analysis when it infers its stack.");
        }

        if (entering == Phase.Analysis && !snapshot.InfersStack)
        {
            return WrongPhase("Only a version that infers its stack starts in analysis.");
        }

        if (entering == Phase.Refinement && snapshot.InfersStack)
        {
            return WrongPhase("A version that infers its stack starts in analysis.");
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

        if (entering == Phase.Analysis && !snapshot.BaseHasCode)
        {
            return Result.Failure<PhaseTransition>(NoCodeToInfer());
        }

        var started = StateOf(PipelineStateMachine.Start(definition, SdlcGuards.StartStage(snapshot.InfersStack)));
        return Result.Success(PhaseTransition.NewVersion(state.Phase, started, string.IsNullOrWhiteSpace(reason) ? "New version started" : reason));
    }

    /// <summary>
    /// §4.4, §4.54, FR-19: what approving <paramref name="approved"/> invalidates (the definition's cascade), the steps the
    /// SDLC hook resets (<see cref="SdlcInvalidationHook.StepResetFor"/>) and the phase to fall back to (the earliest
    /// producing stage, steps counting as produced in execution), only when the project is in a later phase.
    /// </summary>
    public static InvalidationEffect CascadeEffect(string approved, Phase currentPhase, PipelineDefinition definition)
    {
        var effect = DefinitionCascade.Effect(approved, currentPhase.ToWire(), definition, SdlcInvalidationHook.Instance);
        return new InvalidationEffect(
            effect.InvalidatedArtifactKeys,
            SdlcInvalidationHook.StepResetFor(effect.ApprovedArtifactKey),
            effect.FallbackStage is { } stage ? SdlcStages.PhaseOf(stage) : null);
    }

    /// <summary>The lifecycle state of a run position.</summary>
    public static LifecycleState StateOf(RunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new LifecycleState(SdlcStages.PhaseOf(state.Stage), EnumStrings.Parse<ProjectStatus>(state.PackStatus), state.Checkpoint);
    }

    private static DomainError NoCodeToInfer() =>
        new(ErrorCodes.StackInferNoCode, "This repository has no code to analyse. Choose a stack instead.");

    private static Result<PhaseTransition> WrongPhase(string message) =>
        Result.Failure<PhaseTransition>(new DomainError(ErrorCodes.WrongPhase, message));
}
