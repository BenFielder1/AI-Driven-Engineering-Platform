using System.Globalization;
using Aide.Domain.Artifacts;
using Aide.Domain.Jobs;
using Aide.Domain.Review;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Pack.Sdlc;

namespace Aide.Domain.Lifecycle;

/// <summary>
/// FR-291: the project's review-loop position for the engine: <see cref="ReviewCycle"/> is <c>projects.review_cycle</c>
/// (the run's <c>review</c> iteration count, D-0.7.0-27) and <see cref="ReviewCycles"/> the cap
/// <c>maxReviewCycles + extraReviewCycles</c> (the <c>review_cycles</c> setting, FR-14, never below the cycle + 1: D-0.7.0-30).
/// </summary>
public sealed record ReviewLoopState(int ReviewCycle, int ReviewCycles)
{
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

/// <summary>FR-291: the old machine's result (which the app uses) and how the engine's differed, if at all.</summary>
public sealed record ParityCheck<T>(T Old, string Engine, IReadOnlyList<string> Differences)
{
    public bool Agrees => Differences.Count == 0;
}

/// <summary>
/// FR-291 (M58 parity mode): maps 0.6.1's lifecycle inputs to the engine's (§4.51) and its results back for comparison.
/// Pure. The SDLC definition's stage ids are the phases' wire values and its pack statuses the project statuses'.
/// </summary>
public static class LifecycleParityMapper
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

    /// <summary>
    /// 0.6.1's request as the engine's transition (§4.51), or <c>null</c> for <see cref="TransitionKind.NewVersion"/>,
    /// which is a new run (the start selector), not a machine move (D-0.7.0-29). A review cycle is the <c>review</c> loop
    /// with outcome <c>review_required</c>: the SDLC's caller decides the cycle is due (D-0.7.0-27).
    /// </summary>
    public static Transition? TransitionOf(TransitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.TargetPhase?.ToWire();
        return request.Kind switch
        {
            TransitionKind.Advance => new Transition(RunTransitionKind.Advance, target, Reason: request.Reason),
            TransitionKind.ReviewCycle => new Transition(RunTransitionKind.Loop, target, LoopOutcome: ReviewDecision.ReviewRequired.ToWire(), Reason: request.Reason),
            TransitionKind.Rollback => new Transition(RunTransitionKind.Rollback, target, request.TargetVersion, Reason: request.Reason),
            TransitionKind.InvalidationFallback => new Transition(RunTransitionKind.Fallback, target, Reason: request.Reason),
            TransitionKind.ReleaseNoop => new Transition(RunTransitionKind.Noop, target, Reason: request.Reason),
            TransitionKind.ReleaseCompleted => Transition.Change(SdlcStatusChanges.ReleaseCompleted, request.Reason),
            TransitionKind.ReleaseAwaitingMerge => Transition.Change(SdlcStatusChanges.ReleaseAwaitingMerge, request.Reason),
            TransitionKind.ReleaseMergeAbandoned => Transition.Change(SdlcStatusChanges.ReleaseMergeAbandoned, request.Reason),
            TransitionKind.NewVersion => null,
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, null),
        };
    }

    /// <summary>The engine kind 0.6.1's kind maps to (§4.51); <c>null</c> for a new version.</summary>
    public static RunTransitionKind? KindOf(TransitionKind kind) => kind switch
    {
        TransitionKind.Advance => RunTransitionKind.Advance,
        TransitionKind.ReviewCycle => RunTransitionKind.Loop,
        TransitionKind.Rollback => RunTransitionKind.Rollback,
        TransitionKind.InvalidationFallback => RunTransitionKind.Fallback,
        TransitionKind.ReleaseNoop => RunTransitionKind.Noop,
        TransitionKind.ReleaseCompleted or TransitionKind.ReleaseAwaitingMerge or TransitionKind.ReleaseMergeAbandoned => RunTransitionKind.StatusChange,
        _ => null,
    };
}

/// <summary>FR-65, §4.51: the SDLC definition's <c>status_changes</c> names.</summary>
public static class SdlcStatusChanges
{
    public const string ReleaseCompleted = "release_completed";
    public const string ReleaseAwaitingMerge = "release_awaiting_merge";
    public const string ReleaseMergeAbandoned = "release_merge_abandoned";
}

/// <summary>
/// FR-291: compares the old machine's results with the engine's and lists every difference (empty when they agree):
/// success or failure, error code, message and details (unmet reasons, in order), stage, pack and generic status,
/// checkpoint, kind, reason, loop/review-state clearing, the job queued on entry, run completion and the gate needed.
/// </summary>
public static class LifecycleParityComparer
{
    public static IReadOnlyList<string> Compare(Result<PhaseTransition> old, Result<RunTransition> engine)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(engine);
        var differences = new List<string>();
        if (!SameOutcome(old, engine, differences))
        {
            return differences;
        }

        if (old.IsSuccess)
        {
            CompareTransition(old.Value, engine.Value, differences);
        }

        return differences;
    }

    public static IReadOnlyList<string> Compare(Result<AdvanceOutcome> old, Result<RunAdvanceOutcome> engine)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(engine);
        var differences = new List<string>();
        if (!SameOutcome(old, engine, differences) || old.IsFailure)
        {
            return differences;
        }

        var (was, now) = (old.Value, engine.Value);
        Diff(differences, "gate needed", was.GateRequired, now.GateRequired);
        if ((was.Transition is null) != (now.Transition is null))
        {
            differences.Add($"applied: old {was.Transition is not null}, engine {now.Transition is not null}");
        }
        else if (was.Transition is { } transition)
        {
            CompareTransition(transition, now.Transition!, differences);
        }

        return differences;
    }

    /// <summary>Unmet entry conditions: the same reasons in the same order.</summary>
    public static IReadOnlyList<string> Compare(IReadOnlyList<string> old, IReadOnlyList<string> engine)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(engine);
        return old.SequenceEqual(engine, StringComparer.Ordinal)
            ? []
            : [$"unmet reasons: old [{string.Join(" | ", old)}], engine [{string.Join(" | ", engine)}]"];
    }

    /// <summary>§4.54: the same invalidated artifacts (in order), step reset and fallback stage.</summary>
    public static IReadOnlyList<string> Compare(InvalidationEffect old, CascadeEffect engine)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(engine);
        var differences = new List<string>();
        if (!old.InvalidatedTypes.SequenceEqual(engine.InvalidatedArtifactKeys, StringComparer.Ordinal))
        {
            differences.Add($"invalidated: old [{string.Join(", ", old.InvalidatedTypes)}], engine [{string.Join(", ", engine.InvalidatedArtifactKeys)}]");
        }

        Diff(differences, "step reset", old.StepReset.ToString(), SdlcInvalidationHook.StepResetFor(engine.ApprovedArtifactKey).ToString());
        Diff(differences, "pack effect", (old.StepReset != StepReset.None).ToString(), (engine.PackFallbackStages.Count > 0).ToString());
        Diff(differences, "fallback", old.FallbackPhase?.ToWire(), engine.FallbackStage);
        return differences;
    }

    /// <summary>A new run's start: the same stage, status and checkpoint.</summary>
    public static IReadOnlyList<string> Compare(LifecycleState old, RunState engine)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(engine);
        var differences = new List<string>();
        Diff(differences, "stage", old.Phase.ToWire(), engine.Stage);
        Diff(differences, "status", old.Status.ToWire(), engine.PackStatus);
        Diff(differences, "checkpoint", Text(old.Version), Text(engine.Checkpoint));
        return differences;
    }

    public static string Describe<T>(Result<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsFailure
            ? $"{result.Error!.Code}: {result.Error.Message} [{string.Join(" | ", result.Error.Details)}]"
            : result.Value switch
            {
                PhaseTransition t => $"{t.From.ToWire()} → {t.To.ToWire()}/{t.Status.ToWire()} @{Text(t.Version)} ({t.Kind}, \"{t.Reason}\")",
                RunTransition t => $"{t.From} → {t.To}/{t.PackStatus} ({t.Status}) @{Text(t.Checkpoint)} ({t.Kind}, \"{t.Reason}\")",
                AdvanceOutcome { GateRequired: { } gate } => $"gate needed: {gate}",
                AdvanceOutcome a => Describe(Result.Success(a.Transition!)),
                RunAdvanceOutcome { GateRequired: { } gate } => $"gate needed: {gate}",
                RunAdvanceOutcome a => Describe(Result.Success(a.Transition!)),
                LifecycleState s => $"{s.Phase.ToWire()}/{s.Status.ToWire()} @{Text(s.Version)}",
                var other => other?.ToString() ?? "null",
            };
    }

    private static bool SameOutcome(Result old, Result engine, List<string> differences)
    {
        if (old.IsSuccess != engine.IsSuccess)
        {
            differences.Add(old.IsSuccess
                ? $"success: old applied, engine refused ({engine.Error!.Code}: {engine.Error.Message})"
                : $"success: old refused ({old.Error!.Code}: {old.Error.Message}), engine applied");
            return false;
        }

        if (old.IsFailure)
        {
            var (was, now) = (old.Error!, engine.Error!);
            Diff(differences, "error code", was.Code, now.Code);
            Diff(differences, "message", was.Message, now.Message);
            if (!was.Details.SequenceEqual(now.Details, StringComparer.Ordinal))
            {
                differences.Add($"details: old [{string.Join(" | ", was.Details)}], engine [{string.Join(" | ", now.Details)}]");
            }
        }

        return true;
    }

    private static void CompareTransition(PhaseTransition old, RunTransition engine, List<string> differences)
    {
        Diff(differences, "from", old.From.ToWire(), engine.From);
        Diff(differences, "to", old.To.ToWire(), engine.To);
        Diff(differences, "status", old.Status.ToWire(), engine.PackStatus);
        Diff(differences, "generic status", (old.Status == ProjectStatus.Released ? GenericRunStatus.Completed : GenericRunStatus.Active).ToString(), engine.Status.ToString());
        Diff(differences, "checkpoint", Text(old.Version), Text(engine.Checkpoint));
        Diff(differences, "kind", LifecycleParityMapper.KindOf(old.Kind)?.ToString(), engine.Kind.ToString());
        Diff(differences, "reason", old.Reason, engine.Reason);
        Diff(differences, "clears review state", old.ClearsReviewState.ToString(), engine.ClearsLoopState.ToString());
        Diff(differences, "completes run", (old.Kind == TransitionKind.ReleaseCompleted).ToString(), engine.CompletesRun.ToString());
        Diff(differences, "job on entry", old.Kind == TransitionKind.Advance ? PhaseJobs.OnEnter(old.To) : null, engine.EnqueueOnEnter);
    }

    private static void Diff(List<string> differences, string field, string? old, string? engine)
    {
        if (!string.Equals(old, engine, StringComparison.Ordinal))
        {
            differences.Add($"{field}: old '{old ?? "null"}', engine '{engine ?? "null"}'");
        }
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// FR-291: runs a lifecycle decision through 0.6.1's <see cref="LifecycleStateMachine"/> and the engine's
/// <see cref="PipelineStateMachine"/> with the SDLC definition, and returns the old result with the
/// differences. Pure; an engine exception propagates (the caller treats it as a mismatch).
/// </summary>
public static class LifecycleParity
{
    public static ParityCheck<Result<PhaseTransition>> Apply(
        LifecycleState state, TransitionRequest request, LifecycleSnapshot snapshot, ReviewLoopState loop, PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(definition);
        var old = LifecycleStateMachine.Apply(state, request, snapshot);
        if (LifecycleParityMapper.TransitionOf(request) is not { } transition)
        {
            // D-0.7.0-29: a new version is a new run. Its refusals (NOT_RELEASED, VERSION_IN_PROGRESS, STACK_INFER_NO_CODE,
            // the start phase) stay in the SDLC service; an applied one must equal the engine's start.
            if (old.IsFailure)
            {
                return new(old, "(SDLC service)", []);
            }

            var started = PipelineStateMachine.Start(definition, SdlcGuards.StartStage(snapshot.InfersStack));
            var startDifferences = LifecycleParityComparer.Compare(old.Value.NewState, started).ToList();
            if (!old.Value.ClearsReviewState)
            {
                startDifferences.Add("clears review state: old 'False', engine 'True' (a new run)");
            }

            return new(old, $"start {started.Stage}/{started.PackStatus} @{started.Checkpoint.ToString(CultureInfo.InvariantCulture)}", startDifferences);
        }

        var engine = PipelineStateMachine.Apply(
            LifecycleParityMapper.RunStateOf(state, loop.ReviewCycle),
            transition,
            LifecycleParityMapper.FactsOf(state, snapshot, loop.ReviewCycles),
            definition);
        return new(old, LifecycleParityComparer.Describe(engine), LifecycleParityComparer.Compare(old, engine));
    }

    public static ParityCheck<Result<AdvanceOutcome>> RequestAdvance(
        LifecycleState state, LifecycleSnapshot snapshot, ReviewLoopState loop, PipelineDefinition definition, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(loop);
        var old = LifecycleStateMachine.RequestAdvance(state, snapshot, reason);
        var engine = PipelineStateMachine.RequestAdvance(
            LifecycleParityMapper.RunStateOf(state, loop.ReviewCycle),
            LifecycleParityMapper.FactsOf(state, snapshot, loop.ReviewCycles),
            definition,
            reason);
        return new(old, LifecycleParityComparer.Describe(engine), LifecycleParityComparer.Compare(old, engine));
    }

    public static ParityCheck<IReadOnlyList<string>> UnmetEntryConditions(
        LifecycleState state, Phase entering, LifecycleSnapshot snapshot, bool includeGate, ReviewLoopState loop, PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(loop);
        var old = LifecycleStateMachine.UnmetEntryConditions(state, entering, snapshot, includeGate);
        var engine = PipelineStateMachine.UnmetEntryConditions(
            LifecycleParityMapper.RunStateOf(state, loop.ReviewCycle),
            entering.ToWire(),
            LifecycleParityMapper.FactsOf(state, snapshot, loop.ReviewCycles),
            definition,
            includeGate);
        return new(old, $"[{string.Join(" | ", engine)}]", LifecycleParityComparer.Compare(old, engine));
    }

    /// <summary>§4.54: <see cref="InvalidationCascade.Effect"/> against <see cref="DefinitionCascade.Effect"/> with the
    /// <see cref="SdlcInvalidationHook"/>.</summary>
    public static ParityCheck<InvalidationEffect> CascadeEffect(string approved, Phase currentPhase, PipelineDefinition definition)
    {
        var old = InvalidationCascade.Effect(approved, currentPhase);
        var engine = DefinitionCascade.Effect(approved, currentPhase.ToWire(), definition, SdlcInvalidationHook.Instance);
        return new(
            old,
            $"[{string.Join(", ", engine.InvalidatedArtifactKeys)}] → {engine.FallbackStage ?? "none"} (pack: {string.Join(", ", engine.PackFallbackStages)})",
            LifecycleParityComparer.Compare(old, engine));
    }

    /// <summary>A new project's first version: <see cref="LifecycleStateMachine.Start"/> against the engine's start
    /// stage. <c>STACK_INFER_NO_CODE</c> stays in the SDLC service.</summary>
    public static ParityCheck<Result<LifecycleState>> Start(bool infersStack, bool baseHasCode, PipelineDefinition definition)
    {
        var old = LifecycleStateMachine.Start(infersStack, baseHasCode);
        if (old.IsFailure)
        {
            return new(old, "(SDLC service)", []);
        }

        var started = PipelineStateMachine.Start(definition, SdlcGuards.StartStage(infersStack));
        return new(old, $"start {started.Stage}/{started.PackStatus} @{started.Checkpoint.ToString(CultureInfo.InvariantCulture)}", LifecycleParityComparer.Compare(old.Value, started));
    }
}
