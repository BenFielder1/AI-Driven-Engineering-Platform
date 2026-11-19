using Tevrin.Engine.Domain.Gates;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>FR-287, §4.51: the generic transition kinds.</summary>
public enum RunTransitionKind
{
    /// <summary>The stage → its successor (<c>on_success</c>, else the next stage); checkpoint +1.</summary>
    Advance = 0,

    /// <summary>The stage → its <c>loop.to</c>; checkpoint unchanged.</summary>
    Loop = 1,

    /// <summary>Any stage → an earlier or the same stage; checkpoint set to the target.</summary>
    Rollback = 2,

    /// <summary>Any stage → an earlier stage after a cascade (§4.54); checkpoint unchanged.</summary>
    Fallback = 3,

    /// <summary>A named <c>status_changes</c> entry: same stage, new pack status; checkpoint unchanged.</summary>
    StatusChange = 4,

    /// <summary>Same stage, same status; checkpoint unchanged.</summary>
    Noop = 5,
}

/// <summary>
/// FR-287: a run's position. <see cref="Iterations"/> counts the entries into each stage by <c>advance</c> or <c>loop</c>
/// since the run started or its loop state was last cleared (D-0.7.0-27: it follows 0.6.1's <c>projects.review_cycle</c>,
/// so the SDLC's <c>review</c> count is the review cycle; a rollback restores it, a fallback clears it, a new run starts it
/// again). A loop's cap is checked against the count of the stage that owns the loop.
/// </summary>
public sealed record RunState(string Stage, string PackStatus, int Checkpoint, IReadOnlyDictionary<string, int> Iterations)
{
    /// <summary>§4.51: a new run starts at checkpoint 1.</summary>
    public const int InitialCheckpoint = 1;

    /// <summary>The entries into <paramref name="stage"/> (0 when it hasn't been entered).</summary>
    public int IterationsOf(string stage) => Iterations.TryGetValue(stage, out var count) ? count : 0;
}

/// <summary>
/// FR-287: what the machine checks, computed by the caller before <see cref="PipelineStateMachine.Apply"/> (the machine
/// does no I/O). <see cref="Guards"/> holds the result of every guard the move's <c>requires:</c> name, evaluated against
/// the run as it is now; a guard the move needs that isn't here is a programming error.
/// </summary>
public sealed record RunFacts
{
    public static RunFacts Empty { get; } = new();

    /// <summary>The run's approval mode (§4.53: decides whether a stage's entry gate applies).</summary>
    public ApprovalMode Mode { get; init; } = ApprovalMode.Advisory;

    /// <summary>Artifact keys whose latest version is locked (<c>artifact_locked</c>).</summary>
    public IReadOnlyCollection<string> LockedArtifacts { get; init; } = [];

    /// <summary>Types of the run's pending gates (<c>no_pending_gates</c>, and <c>GATE_PENDING</c> in
    /// <see cref="PipelineStateMachine.RequestAdvance"/>).</summary>
    public IReadOnlyCollection<string> PendingGates { get; init; } = [];

    /// <summary>The entry gate of the stage being entered is approved (the approval is the trigger, as in 0.6.1).</summary>
    public bool EntryGateApproved { get; init; }

    /// <summary>Guard name → result.</summary>
    public IReadOnlyDictionary<string, GuardResult> Guards { get; init; } = new Dictionary<string, GuardResult>(StringComparer.Ordinal);

    /// <summary>Setting name → value, from the pack's <see cref="IRunSettingsProvider"/> (a loop cap given as a setting).</summary>
    public IReadOnlyDictionary<string, int> Settings { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>
/// FR-287: a requested move. <see cref="TargetStage"/> is required for <c>rollback</c> and <c>fallback</c>, optional for
/// <c>advance</c>, <c>loop</c> and <c>noop</c> (when given it must be where the move goes), and for
/// <c>status_change</c>, which <see cref="StatusChange"/> names, refused when it isn't the change's stage and the change sets
/// <c>wrong_stage_message</c> (otherwise ignored). <see cref="LoopOutcome"/> is the outcome the caller
/// requests the loop for (D-0.7.0-27: the caller decides it; <c>loop.on</c> must match). <see cref="Reason"/> replaces
/// the default reason when not blank.
/// </summary>
public sealed record Transition(
    RunTransitionKind Kind,
    string? TargetStage = null,
    int? TargetCheckpoint = null,
    string? StatusChange = null,
    string? LoopOutcome = null,
    string? Reason = null)
{
    /// <summary>For <c>rollback</c>: the iteration counts as they were at the target checkpoint (empty when omitted).</summary>
    public IReadOnlyDictionary<string, int>? RestoredIterations { get; init; }

    public static Transition Advance(string? reason = null) => new(RunTransitionKind.Advance, Reason: reason);

    public static Transition AdvanceTo(string targetStage, string? reason = null) => new(RunTransitionKind.Advance, targetStage, Reason: reason);

    public static Transition Loop(string? outcome, string? reason = null) => new(RunTransitionKind.Loop, LoopOutcome: outcome, Reason: reason);

    public static Transition Rollback(string targetStage, int targetCheckpoint, IReadOnlyDictionary<string, int>? restoredIterations = null, string? reason = null) =>
        new(RunTransitionKind.Rollback, targetStage, targetCheckpoint, Reason: reason) { RestoredIterations = restoredIterations };

    public static Transition Fallback(string targetStage, string? reason = null) => new(RunTransitionKind.Fallback, targetStage, Reason: reason);

    public static Transition Change(string statusChange, string? reason = null) => new(RunTransitionKind.StatusChange, StatusChange: statusChange, Reason: reason);

    public static Transition Noop(string? reason = null) => new(RunTransitionKind.Noop, Reason: reason);
}

/// <summary>
/// FR-287: an applied move: what to write as the transition and the run's new position. <see cref="Status"/> is the
/// generic status the pack status maps to (§4.57); <c>waiting_on_gate</c> and <c>running</c> are worked out from gates and
/// jobs when the run is read.
/// </summary>
public sealed record RunTransition(
    string From,
    string To,
    string PackStatus,
    GenericRunStatus Status,
    int Checkpoint,
    RunTransitionKind Kind,
    string Reason)
{
    /// <summary>The run's iteration counts after the move.</summary>
    public IReadOnlyDictionary<string, int> Iterations { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>The <c>status_changes</c> entry applied, for <see cref="RunTransitionKind.StatusChange"/>.</summary>
    public string? StatusChange { get; init; }

    /// <summary>The status change ends the run (<c>completes_run</c>).</summary>
    public bool CompletesRun { get; init; }

    /// <summary>
    /// D-0.7.0-27 (0.6.1's <c>ClearsReviewState</c>): a rollback or fallback clears the loop state, so the caller also clears
    /// pack loop state (the SDLC's extra review cycles). <see cref="Iterations"/> already reflects it.
    /// </summary>
    public bool ClearsLoopState => Kind is RunTransitionKind.Rollback or RunTransitionKind.Fallback;

    /// <summary>The entered stage's <c>enqueue_on_enter</c> job type, set only for an <c>advance</c> (0.6.1's
    /// <c>PhaseJobs</c>, D-0.7.0-27).</summary>
    public string? EnqueueOnEnter { get; init; }

    public RunState NewState => new(To, PackStatus, Checkpoint, Iterations);
}

/// <summary>FR-288: "advance" either applied, or needs <see cref="GateRequired"/> created first.</summary>
public sealed record RunAdvanceOutcome(RunTransition? Transition, string? GateRequired)
{
    public static RunAdvanceOutcome Applied(RunTransition transition) => new(transition, null);

    public static RunAdvanceOutcome NeedsGate(string gate) => new(null, gate);
}
