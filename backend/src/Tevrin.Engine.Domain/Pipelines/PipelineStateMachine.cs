using System.Globalization;
using Tevrin.Engine.Domain.Common;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// FR-287, FR-288, §4.51–§4.53: the generic run state machine, driven by a <see cref="PipelineDefinition"/>. Pure: guards
/// are evaluated by the caller and passed in <see cref="RunFacts"/>. Checkpoints: <c>advance</c> +1; <c>loop</c>,
/// <c>fallback</c>, <c>status_change</c> and <c>noop</c> unchanged; <c>rollback</c> sets the target (≥ 1, below the
/// current). <c>requires:</c> runs only on <c>advance</c> (the entered stage's, then its gate) and <c>loop</c> (the
/// loop's own, never the target stage's or its gate; D-0.7.0-27).
/// <para>
/// Messages are 0.6.1's lifecycle messages with the definition's stage ids, titles and names filled in; they keep the
/// word "phase" because the SDLC shows them as they were (see <see cref="TransitionMessages"/>).
/// </para>
/// </summary>
public static class PipelineStateMachine
{
    /// <summary>A new run's position in <paramref name="startStage"/> (one of the definition's <c>start</c>): the stage's
    /// status, checkpoint 1, one entry into the start stage.</summary>
    public static RunState Start(PipelineDefinition definition, string startStage)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.Start.Contains(startStage, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{startStage}' is not a start stage of '{definition.Pipeline}'.", nameof(startStage));
        }

        var stage = StageOf(definition, startStage);
        return new RunState(stage.Id, stage.Status, RunState.InitialCheckpoint, new Dictionary<string, int>(StringComparer.Ordinal) { [stage.Id] = 1 });
    }

    public static Result<RunTransition> Apply(RunState state, Transition transition, RunFacts facts, PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(definition);
        _ = StageOf(definition, state.Stage);

        return transition.Kind switch
        {
            RunTransitionKind.Advance => Advance(state, transition, facts, definition),
            RunTransitionKind.Loop => Loop(state, transition, facts, definition),
            RunTransitionKind.Rollback => Rollback(state, transition, definition),
            RunTransitionKind.Fallback => Fallback(state, transition, definition),
            RunTransitionKind.StatusChange => StatusChange(state, transition, definition),
            RunTransitionKind.Noop => Noop(state, transition, definition),
            _ => throw new ArgumentOutOfRangeException(nameof(transition), transition.Kind, null),
        };
    }

    /// <summary>
    /// FR-288 (0.6.1's <c>RequestAdvance</c>): the successor's <c>requires:</c> are checked first (all unmet reasons, in
    /// declared order, as <c>PHASE_PRECONDITION_FAILED</c>); then, when its entry gate applies in the run's approval mode
    /// and isn't approved, "gate needed" (or <c>GATE_PENDING</c> when that gate is already pending); else the advance is
    /// applied.
    /// </summary>
    public static Result<RunAdvanceOutcome> RequestAdvance(RunState state, RunFacts facts, PipelineDefinition definition, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(definition);
        var current = StageOf(definition, state.Stage);

        if (definition.SuccessorOf(state.Stage) is not { } next)
        {
            return Result.Failure<RunAdvanceOutcome>(WrongPhaseError(TransitionMessages.LastStage(current)));
        }

        var unmet = UnmetEntryConditions(state, next, facts, definition, includeGate: false);
        if (unmet.Count > 0)
        {
            return Result.Failure<RunAdvanceOutcome>(PreconditionFailed(next, unmet));
        }

        if (BuiltInRequirements.RequiredGate(StageOf(definition, next), facts.Mode) is { } gate && !facts.EntryGateApproved)
        {
            return facts.PendingGates.Contains(gate, StringComparer.Ordinal)
                ? Result.Failure<RunAdvanceOutcome>(new DomainError(EngineErrorCodes.GatePending, TransitionMessages.GatePending(gate)))
                : Result.Success(RunAdvanceOutcome.NeedsGate(gate));
        }

        var applied = Advance(state, Transition.Advance(reason), facts, definition);
        return applied.IsSuccess
            ? Result.Success(RunAdvanceOutcome.Applied(applied.Value))
            : Result.Failure<RunAdvanceOutcome>(applied.Error!);
    }

    /// <summary>
    /// §4.52 (0.6.1's <c>UnmetEntryConditions</c>): the unmet reasons for entering <paramref name="entering"/>: its
    /// <c>requires:</c> in declared order (built-ins' fixed messages, guards' own), then, with
    /// <paramref name="includeGate"/>, its entry gate when it applies and isn't approved.
    /// </summary>
    public static IReadOnlyList<string> UnmetEntryConditions(RunState state, string entering, RunFacts facts, PipelineDefinition definition, bool includeGate = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(definition);
        var stage = StageOf(definition, entering);
        var unmet = UnmetRequirements(stage.Requires, facts, definition).ToList();
        if (includeGate && BuiltInRequirements.RequiredGate(stage, facts.Mode) is { } gate && !facts.EntryGateApproved)
        {
            unmet.Add(BuiltInRequirements.GateNotApproved(gate));
        }

        return unmet;
    }

    /// <summary>The unmet reasons of <paramref name="requirements"/>, in declared order. A guard missing from
    /// <see cref="RunFacts.Guards"/> throws <see cref="InvalidOperationException"/>: the caller must evaluate it.</summary>
    public static IReadOnlyList<string> UnmetRequirements(IReadOnlyList<StageRequirement> requirements, RunFacts facts, PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(definition);
        var unmet = new List<string>();
        foreach (var requirement in requirements)
        {
            if (requirement is GuardRequirement guard)
            {
                if (!facts.Guards.TryGetValue(guard.Guard, out var result))
                {
                    throw new InvalidOperationException($"Guard '{guard.Guard}' was not evaluated; put its result in the run facts.");
                }

                if (!result.Ok)
                {
                    unmet.AddRange(result.Reasons.Count > 0 ? result.Reasons : [TransitionMessages.GuardFailed(guard.Guard)]);
                }
            }
            else if (BuiltInRequirements.Unmet(definition, requirement, facts.LockedArtifacts, facts.PendingGates) is { } reason)
            {
                unmet.Add(reason);
            }
        }

        return unmet;
    }

    /// <summary>The value of an iteration cap: its fixed value, or its setting from <see cref="RunFacts.Settings"/>
    /// (throws <see cref="InvalidOperationException"/> when the caller didn't supply it); <c>null</c> for no cap.</summary>
    public static int? ResolveCap(IterationCap? cap, RunFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (cap is null)
        {
            return null;
        }

        if (cap.Value is { } value)
        {
            return value;
        }

        return cap.Setting is { } setting && facts.Settings.TryGetValue(setting, out var resolved)
            ? resolved
            : throw new InvalidOperationException($"Setting '{cap.Setting}' was not resolved; put its value in the run facts.");
    }

    private static Result<RunTransition> Advance(RunState state, Transition transition, RunFacts facts, PipelineDefinition definition)
    {
        var current = StageOf(definition, state.Stage);
        if (definition.SuccessorOf(state.Stage) is not { } next)
        {
            return WrongPhase(TransitionMessages.LastStage(current));
        }

        if (transition.TargetStage is { } target && !string.Equals(target, next, StringComparison.Ordinal))
        {
            return WrongPhase(TransitionMessages.NextOnly(state.Stage, next, target));
        }

        var unmet = UnmetEntryConditions(state, next, facts, definition);
        if (unmet.Count > 0)
        {
            return Result.Failure<RunTransition>(PreconditionFailed(next, unmet));
        }

        var entered = StageOf(definition, next);
        return Result.Success(Moved(state, transition, definition, next, entered.Status, state.Checkpoint + 1, TransitionMessages.Advanced(next)) with
        {
            Iterations = Entered(state.Iterations, next),
            EnqueueOnEnter = entered.EnqueueOnEnter,
        });
    }

    private static Result<RunTransition> Loop(RunState state, Transition transition, RunFacts facts, PipelineDefinition definition)
    {
        var current = StageOf(definition, state.Stage);
        if (current.Loop is not { } loop || (transition.TargetStage is { } target && !string.Equals(target, loop.To, StringComparison.Ordinal)))
        {
            return WrongPhase(TransitionMessages.LoopOnly(definition, current));
        }

        var unmet = new List<string>();
        if (loop.On is { } on && !string.Equals(transition.LoopOutcome, on, StringComparison.Ordinal))
        {
            unmet.Add(TransitionMessages.LoopOutcome(current.Id, transition.LoopOutcome, on));
        }

        unmet.AddRange(UnmetRequirements(loop.Requires, facts, definition));

        // The cap is a backstop (§4.51): it refuses a loop that would otherwise be allowed, and adds no reason to a loop
        // already refused, so a refused SDLC review cycle reports exactly 0.6.1's reasons (FR-291 parity).
        if (unmet.Count == 0 && ResolveCap(loop.MaxIterations, facts) is { } cap && state.IterationsOf(current.Id) >= cap)
        {
            unmet.Add(TransitionMessages.LoopLimit(loop, cap));
        }

        if (unmet.Count > 0)
        {
            return Result.Failure<RunTransition>(PreconditionFailed(loop.To, unmet));
        }

        var entered = StageOf(definition, loop.To);
        return Result.Success(Moved(state, transition, definition, loop.To, entered.Status, state.Checkpoint, TransitionMessages.Looped(loop)) with
        {
            Iterations = Entered(state.Iterations, loop.To),
        });
    }

    private static Result<RunTransition> Rollback(RunState state, Transition transition, PipelineDefinition definition)
    {
        if (transition.TargetCheckpoint is not { } checkpoint || checkpoint < RunState.InitialCheckpoint || checkpoint >= state.Checkpoint)
        {
            return Result.Failure<RunTransition>(new DomainError(EngineErrorCodes.InvalidTargetVersion, TransitionMessages.RollbackCheckpoint(state.Checkpoint)));
        }

        var targetIndex = definition.IndexOfStage(transition.TargetStage);
        if (targetIndex < 0 || targetIndex > definition.IndexOfStage(state.Stage))
        {
            return WrongPhase(TransitionMessages.RollbackLater);
        }

        var target = definition.Stages[targetIndex];
        var status = definition.RollbackStatus ?? target.Status;
        return Result.Success(Moved(state, transition, definition, target.Id, status, checkpoint, TransitionMessages.RolledBack(checkpoint)) with
        {
            Iterations = new Dictionary<string, int>(transition.RestoredIterations ?? new Dictionary<string, int>(), StringComparer.Ordinal),
        });
    }

    private static Result<RunTransition> Fallback(RunState state, Transition transition, PipelineDefinition definition)
    {
        var targetIndex = definition.IndexOfStage(transition.TargetStage);
        if (targetIndex < 0 || targetIndex >= definition.IndexOfStage(state.Stage))
        {
            return WrongPhase(TransitionMessages.FallbackEarlierOnly);
        }

        var target = definition.Stages[targetIndex];
        return Result.Success(Moved(state, transition, definition, target.Id, target.Status, state.Checkpoint, TransitionMessages.FellBack(target.Id)) with
        {
            Iterations = new Dictionary<string, int>(StringComparer.Ordinal),
        });
    }

    private static Result<RunTransition> StatusChange(RunState state, Transition transition, PipelineDefinition definition)
    {
        var change = definition.StatusChanges.FirstOrDefault(c => string.Equals(c.Name, transition.StatusChange, StringComparison.Ordinal))
            ?? throw new ArgumentException($"'{transition.StatusChange}' is not a status change of '{definition.Pipeline}'.", nameof(transition));

        if (!string.Equals(state.Stage, change.Stage, StringComparison.Ordinal) || !change.From.Contains(state.PackStatus, StringComparer.Ordinal))
        {
            return Result.Failure<RunTransition>(new DomainError(change.ErrorCode, TransitionMessages.StatusChangeRefused(change, state)));
        }

        return Result.Success(Moved(state, transition, definition, change.Stage, change.To, state.Checkpoint, change.EffectiveReason) with
        {
            Iterations = state.Iterations,
            StatusChange = change.Name,
            CompletesRun = change.CompletesRun,
        });
    }

    private static Result<RunTransition> Noop(RunState state, Transition transition, PipelineDefinition definition)
    {
        if (definition.SuccessorOf(state.Stage) is not null
            || (transition.TargetStage is { } target && !string.Equals(target, state.Stage, StringComparison.Ordinal)))
        {
            return WrongPhase(TransitionMessages.NoopFinalOnly(definition));
        }

        return Result.Success(Moved(state, transition, definition, state.Stage, state.PackStatus, state.Checkpoint, TransitionMessages.NoopReason) with
        {
            Iterations = state.Iterations,
        });
    }

    private static RunTransition Moved(
        RunState state,
        Transition transition,
        PipelineDefinition definition,
        string to,
        string packStatus,
        int checkpoint,
        string defaultReason) =>
        new(
            state.Stage,
            to,
            packStatus,
            definition.GenericStatusOf(packStatus)
                ?? throw new InvalidOperationException($"Status '{packStatus}' is not a status of '{definition.Pipeline}'."),
            checkpoint,
            transition.Kind,
            string.IsNullOrWhiteSpace(transition.Reason) ? defaultReason : transition.Reason);

    private static Dictionary<string, int> Entered(IReadOnlyDictionary<string, int> iterations, string stage)
    {
        var next = new Dictionary<string, int>(iterations, StringComparer.Ordinal);
        next[stage] = (next.TryGetValue(stage, out var count) ? count : 0) + 1;
        return next;
    }

    private static StageDefinition StageOf(PipelineDefinition definition, string stageId) =>
        definition.FindStage(stageId) ?? throw new ArgumentException($"'{stageId}' is not a stage of '{definition.Pipeline}'.", nameof(stageId));

    private static DomainError WrongPhaseError(string message) => new(EngineErrorCodes.WrongPhase, message);

    private static Result<RunTransition> WrongPhase(string message) => Result.Failure<RunTransition>(WrongPhaseError(message));

    private static DomainError PreconditionFailed(string entering, IReadOnlyList<string> unmet) =>
        new(EngineErrorCodes.PhasePreconditionFailed, TransitionMessages.CantEnter(entering, unmet)) { Details = unmet };
}

/// <summary>
/// FR-287 (an M58 decision): the state machine's messages and default reasons. They are 0.6.1's lifecycle texts with the
/// definition's stage ids, stage titles, loop title and status-change texts filled in, and they keep 0.6.1's word "phase":
/// the SDLC shows them to users and records them in transitions exactly as before, and a stage is what the SDLC called a
/// phase. Pack-specific wording comes from the definition (<c>loop.title</c>, <c>status_changes.*.reason</c> and
/// <c>error_message</c>), never from code.
/// </summary>
public static class TransitionMessages
{
    public const string RollbackLater = "A rollback can't move to a later phase.";

    public const string FallbackEarlierOnly = "An invalidation fallback moves to an earlier phase only.";

    public const string NoopReason = "No-op";

    /// <summary>"Release is the last phase; there is nothing to advance to." (the stage's title).</summary>
    public static string LastStage(StageDefinition stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return $"{(string.IsNullOrWhiteSpace(stage.Title) ? stage.Id : stage.Title)} is the last phase; there is nothing to advance to.";
    }

    public static string NextOnly(string from, string next, string target) =>
        $"Forward transitions go to the next phase only: {from} → {next}, not {target}.";

    public static string CantEnter(string stage, IReadOnlyList<string> unmet) => $"Can't enter {stage}: {string.Join(" ", unmet)}";

    public static string GatePending(string gate) => $"A {gate} gate is already pending.";

    public static string GuardFailed(string guard) => $"The {guard} check failed.";

    public static string Advanced(string stage) => $"Advanced to {stage}";

    /// <summary>"A review cycle moves review → execution only.": the current stage's loop, else the definition's first loop.</summary>
    public static string LoopOnly(PipelineDefinition definition, StageDefinition current)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(current);
        var owner = current.Loop is not null ? current : definition.Stages.FirstOrDefault(s => s.Loop is not null);
        return owner?.Loop is { } loop
            ? $"A {KeyWords.LowerFirst(loop.EffectiveTitle)} moves {owner.Id} → {loop.To} only."
            : $"The {current.Id} phase has no loop.";
    }

    public static string LoopOutcome(string stage, string? outcome, string on) => $"The {stage} outcome is {outcome ?? "none"}, not {on}.";

    public static string LoopLimit(LoopDefinition loop, int cap)
    {
        ArgumentNullException.ThrowIfNull(loop);
        return $"The {KeyWords.LowerFirst(loop.EffectiveTitle)} limit of {cap.ToString(CultureInfo.InvariantCulture)} is reached.";
    }

    /// <summary>"Review cycle: re-entering execution".</summary>
    public static string Looped(LoopDefinition loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        return $"{loop.EffectiveTitle}: re-entering {loop.To}";
    }

    public static string RollbackCheckpoint(int current) =>
        $"The target checkpoint must be an existing checkpoint below the current checkpoint {current.ToString(CultureInfo.InvariantCulture)}.";

    public static string RolledBack(int checkpoint) => $"Rolled back to checkpoint {checkpoint.ToString(CultureInfo.InvariantCulture)}";

    public static string FellBack(string stage) => $"Fell back to {stage} after invalidation";

    /// <summary>"release → release is only valid in the release phase." (the definition's last final stage).</summary>
    public static string NoopFinalOnly(PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var final = definition.Stages.LastOrDefault(s => definition.SuccessorOf(s.Id) is null)?.Id ?? "?";
        return $"{final} → {final} is only valid in the {final} phase.";
    }

    /// <summary>The change's <c>error_message</c> with <c>{stage}</c>, <c>{status}</c>, <c>{change_stage}</c> and <c>{from}</c> filled in.</summary>
    public static string StatusChangeRefused(StatusChangeDefinition change, RunState state)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(state);
        return change.EffectiveErrorMessage
            .Replace("{stage}", state.Stage, StringComparison.Ordinal)
            .Replace("{status}", state.PackStatus, StringComparison.Ordinal)
            .Replace("{change_stage}", change.Stage, StringComparison.Ordinal)
            .Replace("{from}", string.Join(" or ", change.From), StringComparison.Ordinal);
    }
}
