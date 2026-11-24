using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Gates;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// FR-275, §4.49: a parsed pipeline definition. Immutable; the YAML loader (<c>PipelineDefinitionLoader</c> in the runtime)
/// builds it and <see cref="PipelineDefinitionValidator"/> checks it. Keyed collections (<see cref="Artifacts"/>,
/// <see cref="Statuses"/>, <see cref="StatusChanges"/>) are lists in file order, so a duplicate key survives parsing and
/// the validator can report it (<see cref="DefinitionErrorCodes.DuplicateId"/>). Optional values the loader defaults are
/// filled in (e.g. <see cref="AgentStageDefinition.JobType"/>), so writing a default out doesn't change the hash (§4.50).
/// </summary>
public sealed record PipelineDefinition
{
    /// <summary><c>pipeline</c>: the id, kebab-case, unique across packs.</summary>
    public required string Pipeline { get; init; }

    public int Version { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>FR-284: defaults to <c>{ kind: manual }</c> when omitted.</summary>
    public PipelineTrigger Trigger { get; init; } = PipelineTrigger.Manual;

    public IReadOnlyList<ApprovalMode> ApprovalModes { get; init; } = [];

    public ApprovalMode? DefaultApprovalMode { get; init; }

    /// <summary>Start stages; the first is the default, and a pack's <see cref="IRunStartSelector"/> picks among them.</summary>
    public IReadOnlyList<string> Start { get; init; } = [];

    /// <summary>Setting names whose values come from the pack's <see cref="IRunSettingsProvider"/> (per project).</summary>
    public IReadOnlyList<string> Settings { get; init; } = [];

    public IReadOnlyList<ArtifactDefinition> Artifacts { get; init; } = [];

    /// <summary>FR-285: the pack's custom statuses. The generic status names (<see cref="GenericRunStatus"/>) are always
    /// valid pack statuses that map to themselves.</summary>
    public IReadOnlyList<StatusDefinition> Statuses { get; init; } = [];

    public IReadOnlyList<StatusChangeDefinition> StatusChanges { get; init; } = [];

    /// <summary>
    /// <c>rollback_status</c> (addition to §4.49 for §4.51): the pack status a run takes after a <c>rollback</c>
    /// transition; when <c>null</c> the target stage's status is used.
    /// </summary>
    public string? RollbackStatus { get; init; }

    public ConcurrencyDefinition Concurrency { get; init; } = ConcurrencyDefinition.None;

    public IReadOnlyList<StageDefinition> Stages { get; init; } = [];

    /// <summary>The position of stage <paramref name="stageId"/> in <see cref="Stages"/> (first match), or -1.</summary>
    public int IndexOfStage(string? stageId)
    {
        for (var i = 0; i < Stages.Count; i++)
        {
            if (string.Equals(Stages[i].Id, stageId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Stage <paramref name="stageId"/>, or <c>null</c>.</summary>
    public StageDefinition? FindStage(string? stageId)
    {
        var index = IndexOfStage(stageId);
        return index < 0 ? null : Stages[index];
    }

    /// <summary>Artifact <paramref name="key"/>, or <c>null</c>.</summary>
    public ArtifactDefinition? FindArtifact(string? key) =>
        Artifacts.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// FR-281: where a successful stage goes: its <c>on_success</c>, else the following stage (linear by default), else
    /// <c>null</c> for the last stage.
    /// </summary>
    public string? SuccessorOf(string stageId)
    {
        var index = IndexOfStage(stageId);
        if (index < 0)
        {
            return null;
        }

        var stage = Stages[index];
        return stage.OnSuccess ?? (index + 1 < Stages.Count ? Stages[index + 1].Id : null);
    }

    /// <summary>The generic status a pack status maps to (§4.57): its <see cref="Statuses"/> entry, else the generic
    /// status of the same name, else <c>null</c>.</summary>
    public GenericRunStatus? GenericStatusOf(string? status)
    {
        var declared = Statuses.FirstOrDefault(s => string.Equals(s.Name, status, StringComparison.Ordinal));
        if (declared is not null)
        {
            return declared.Generic;
        }

        return EnumStrings.TryParse(status, out GenericRunStatus generic) ? generic : null;
    }
}

/// <summary>
/// FR-284: <c>trigger</c>. <see cref="Options"/> holds the kind's other scalar keys. FR-336, FR-337 (0.8.0): a
/// <c>schedule</c> or <c>webhook</c> trigger takes <c>input: &lt;artifactKey&gt;</c> (required for a webhook; an artifact
/// declared <c>input: true</c>) and <c>on_active: submit | skip</c> (what a fire or call does while a run is active;
/// <c>skip</c> when omitted). Absent options are left out of the canonical form (§4.49 amended), so the hash is unchanged.
/// </summary>
public sealed record PipelineTrigger(TriggerKind Kind, IReadOnlyDictionary<string, string> Options)
{
    /// <summary>The <c>input</c> option key.</summary>
    public const string InputOption = "input";

    /// <summary>The <c>on_active</c> option key.</summary>
    public const string OnActiveOption = "on_active";

    public static PipelineTrigger Manual { get; } = new(TriggerKind.Manual, new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>FR-337: the artifact a webhook body (or a schedule's inputs) is stored as, or <c>null</c>.</summary>
    public string? Input => Options.TryGetValue(InputOption, out var input) ? input : null;

    /// <summary>FR-336, FR-337: <c>on_active</c>; <see cref="TriggerOnActive.Skip"/> when omitted or not a known value
    /// (the validator reports an unknown value).</summary>
    public TriggerOnActive OnActive =>
        Options.TryGetValue(OnActiveOption, out var value) && EnumStrings.TryParse(value, out TriggerOnActive parsed)
            ? parsed
            : TriggerOnActive.Skip;
}

/// <summary>FR-284: 0.7.0 ran only <see cref="Manual"/> (D-0.7.0-3); 0.8.0 adds schedules and webhooks (FR-336, FR-337).</summary>
public enum TriggerKind
{
    Manual = 0,
    Schedule = 1,
    Webhook = 2,
}

/// <summary>FR-336, FR-337: what a schedule fire or a webhook call does while a run of the pipeline is active.</summary>
public enum TriggerOnActive
{
    /// <summary>The fire is skipped and recorded; a webhook call is refused (<c>RUN_ALREADY_ACTIVE</c>).</summary>
    Skip = 0,

    /// <summary>The payload is submitted to the active run as a new version of the trigger's input (FR-335).</summary>
    Submit = 1,
}

/// <summary>FR-331: how many non-final runs of the pipeline a project may have at once.</summary>
public enum RunConcurrency
{
    /// <summary>At most one (the default; <c>RUN_ALREADY_ACTIVE</c> otherwise).</summary>
    One = 0,

    Many = 1,
}

/// <summary>FR-279: an artifact the definition produces or reads.</summary>
public sealed record ArtifactDefinition(
    string Key,
    string Title,
    ArtifactFormat Format,
    string? Schema,
    ArtifactApproval Approval,
    CascadeMode Cascade)
{
    /// <summary>
    /// FR-335 (0.8.0): <c>input: true</c>, the artifact is supplied from outside the run (at start, by a submit or by a
    /// trigger) rather than produced by a stage. Omitted from the canonical form when <c>false</c> (the hash is unchanged).
    /// </summary>
    public bool Input { get; init; }

    /// <summary>
    /// FR-352 (0.8.0): <c>scope: project</c>, one version history per project shared by every run, edited by people
    /// through the artifact endpoints; a run pins its newest locked version when it starts (§4.76). Omitted from the
    /// canonical form when <see cref="ArtifactScope.Run"/> (the default), so the hash is unchanged.
    /// </summary>
    public ArtifactScope Scope { get; init; }
}

/// <summary>FR-352: whose version history an artifact has.</summary>
public enum ArtifactScope
{
    /// <summary>Each run (or SDLC release version) has its own versions (the default).</summary>
    Run = 0,

    /// <summary>One history per project, pinned by each run at its start (§4.76).</summary>
    Project = 1,
}

public enum ArtifactFormat
{
    Markdown = 0,
    Json = 1,
}

public enum ArtifactApproval
{
    /// <summary>A person approves (locks) the draft.</summary>
    Required = 0,

    /// <summary>The draft locks itself once stored.</summary>
    AutoLock = 1,
}

/// <summary>§4.54: what approving a new version of the artifact invalidates.</summary>
public enum CascadeMode
{
    Downstream = 0,
    SelfAndDownstream = 1,
    None = 2,
}

/// <summary>§4.57: the generic run status. <see cref="Completed"/>, <see cref="Failed"/> and <see cref="Cancelled"/> are final.</summary>
public enum GenericRunStatus
{
    Active = 0,
    Running = 1,
    WaitingOnGate = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,

    /// <summary>FR-335 (0.8.0): an engine-controlled run stopped at a stage that requires an input it doesn't have yet.
    /// Stored (unlike <see cref="WaitingOnGate"/>); only the run driver enters and leaves it (<see cref="Entities.PipelineRunRules"/>).</summary>
    WaitingOnInput = 6,
}

/// <summary>FR-285: a custom pack status and the generic status it maps to.</summary>
public sealed record StatusDefinition(string Name, GenericRunStatus Generic);

/// <summary>
/// FR-285: a named status-only move on <see cref="Stage"/>: allowed from any of <see cref="From"/> to <see cref="To"/>;
/// otherwise the move fails with <see cref="ErrorCode"/>. <see cref="CompletesRun"/> ends the run.
/// </summary>
public sealed record StatusChangeDefinition(
    string Name,
    string Stage,
    IReadOnlyList<string> From,
    string To,
    bool CompletesRun,
    string ErrorCode)
{
    /// <summary>
    /// <c>reason</c> (addition to §4.49, M58): the transition's default reason, recorded when the caller gives none. When
    /// omitted it's <see cref="EffectiveReason"/>, the name in words (<c>release_completed</c> → "Release completed").
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// <c>error_message</c> (addition to §4.49, M58): the message of the <see cref="ErrorCode"/> failure, with the
    /// placeholders <c>{stage}</c> and <c>{status}</c> (the run's), <c>{change_stage}</c> (<see cref="Stage"/>) and
    /// <c>{from}</c> (<see cref="From"/>, joined with " or "). When omitted it's <see cref="DefaultErrorMessage"/>.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// <c>wrong_stage_message</c> (addition to §4.49, M58d): when set, a <c>status_change</c> transition that names a target
    /// stage other than <see cref="Stage"/> is refused with <c>WRONG_PHASE</c> and this message (placeholders
    /// <c>{change_stage}</c> and <c>{target}</c>), before the status is checked. When omitted, a named target is ignored.
    /// </summary>
    public string? WrongStageMessage { get; init; }

    /// <summary>The default <see cref="ErrorMessage"/>: 0.6.1's release status-change message, made generic.</summary>
    public const string DefaultErrorMessage = "The project is in {stage} with status {status}, not {change_stage} with status {from}.";

    public string EffectiveReason => Reason ?? KeyWords.Humanise(Name);

    public string EffectiveErrorMessage => ErrorMessage ?? DefaultErrorMessage;
}

/// <summary>FR-286, §4.59: job types of which at most one may be active per scope.</summary>
public sealed record ConcurrencyDefinition(IReadOnlyList<ExclusiveJob> Exclusive)
{
    public static ConcurrencyDefinition None { get; } = new([]);

    /// <summary>FR-331 (0.8.0): <c>runs: one | many</c>, default <see cref="RunConcurrency.One"/>. Omitted from the
    /// canonical form when it's the default (the hash is unchanged).</summary>
    public RunConcurrency Runs { get; init; } = RunConcurrency.One;
}

public sealed record ExclusiveJob(string JobType, ConcurrencyScope Scope);

public enum ConcurrencyScope
{
    Project = 0,
    Run = 1,
}

/// <summary>FR-280: what does a stage's work.</summary>
public enum StageKind
{
    /// <summary>An agent (<see cref="StageDefinition.Agent"/>).</summary>
    Agent = 0,

    /// <summary>An <see cref="IStageHandler"/> (<see cref="StageDefinition.Handler"/>).</summary>
    Code = 1,

    /// <summary>Neither: work happens through pack endpoints (or the stage is only an approval, D-0.7.0-13).</summary>
    Manual = 2,
}

/// <summary>FR-280–FR-283: one stage.</summary>
public sealed record StageDefinition
{
    public required string Id { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>FR-285: the pack status while the run is in this stage.</summary>
    public string Status { get; init; } = string.Empty;

    public AgentStageDefinition? Agent { get; init; }

    /// <summary>A registered <see cref="IStageHandler"/> name (<c>&lt;pack&gt;.&lt;name&gt;</c>).</summary>
    public string? Handler { get; init; }

    public IReadOnlyList<string> Inputs { get; init; } = [];

    public string? Output { get; init; }

    /// <summary>FR-282: entry requirements, evaluated in declared order (§4.52).</summary>
    public IReadOnlyList<StageRequirement> Requires { get; init; } = [];

    /// <summary>FR-282: guards run on the stage's output.</summary>
    public IReadOnlyList<StageValidation> Validate { get; init; } = [];

    public OnInvalidPolicy? OnInvalid { get; init; }

    /// <summary>FR-283: the entry gate.</summary>
    public StageGate? Gate { get; init; }

    public string? OnSuccess { get; init; }

    public RejectTransition? OnReject { get; init; }

    public LoopDefinition? Loop { get; init; }

    /// <summary>
    /// <c>enqueue_on_enter: &lt;job_type&gt;</c> (D-0.7.0-27, addition to §4.49): the job queued in the same transaction as
    /// an <c>advance</c> into this stage, or <c>null</c> for none. The engine never infers it from the stage kind (an agent
    /// stage doesn't imply a job on entry).
    /// </summary>
    public string? EnqueueOnEnter { get; init; }

    /// <summary>
    /// <c>agents:</c> (D-0.7.0-42, addition to §4.49): secondary agents bound to this stage, run only when a request names
    /// them (<see cref="AgentBindings.ForAgent"/>). They never change the stage's kind, transitions, gates, cascade or state.
    /// </summary>
    public IReadOnlyList<AgentBindingDefinition> Agents { get; init; } = [];

    /// <summary>
    /// <c>handlers:</c> (D-0.7.0-43, addition to §4.49): secondary <see cref="IStageHandler"/> names (<c>&lt;pack&gt;.&lt;name&gt;</c>)
    /// bound to this stage, run only when a request names them (<see cref="StageHandlers.StageOf"/>), as <c>agents:</c> binds
    /// secondary agents. They never change the stage's kind, transitions, gates, cascade or state. Handler names are unique
    /// across the pipeline (<see cref="Handler"/> and every <c>handlers:</c> entry).
    /// </summary>
    public IReadOnlyList<string> Handlers { get; init; } = [];

    /// <summary>The stage's kind; a stage with both an agent and a handler is invalid (<see cref="DefinitionErrorCodes.StageKind"/>)
    /// and reads as an agent stage.</summary>
    public StageKind Kind => Agent is not null ? StageKind.Agent : Handler is not null ? StageKind.Code : StageKind.Manual;
}

/// <summary>
/// FR-280: <c>agent: { key, prompt, model, tools, job_type }</c>. <see cref="Prompt"/> is optional (deviation from §4.49,
/// which the §4.51 sketch already omits): <c>null</c> means the pack's prompt for <see cref="Key"/>.
/// <see cref="JobType"/> defaults to <c>agent.run</c>.
/// </summary>
public sealed record AgentStageDefinition(
    string Key,
    string? Prompt,
    string? Model,
    IReadOnlyList<string> Tools,
    string JobType);

/// <summary>FR-282: one <c>requires:</c> entry.</summary>
public abstract record StageRequirement;

/// <summary><c>{ artifact_locked: &lt;key&gt; }</c>: the artifact's latest version is locked.</summary>
public sealed record ArtifactLockedRequirement(string ArtifactKey) : StageRequirement;

/// <summary><c>{ no_pending_gates: { except: [...] } }</c>: no gate is pending on the run, apart from the listed types.</summary>
public sealed record NoPendingGatesRequirement(IReadOnlyList<string> Except) : StageRequirement;

/// <summary><c>{ guard: &lt;pack&gt;.&lt;name&gt; }</c>: a registered <see cref="IGuard"/> passes.</summary>
public sealed record GuardRequirement(string Guard) : StageRequirement;

/// <summary>
/// FR-335 (0.8.0): <c>{ input: &lt;key&gt; }</c>: the run has a version of the input artifact <paramref name="ArtifactKey"/>
/// (declared <c>input: true</c>). Unmet, the run driver waits (<see cref="GenericRunStatus.WaitingOnInput"/>).
/// </summary>
public sealed record InputRequirement(string ArtifactKey) : StageRequirement;

/// <summary>FR-282: one <c>validate:</c> entry, <c>{ guard: &lt;pack&gt;.&lt;name&gt; }</c>.</summary>
public sealed record StageValidation(string Guard);

/// <summary>FR-282, §4.55: what happens when validation fails.</summary>
public sealed record OnInvalidPolicy(bool RetryWithFeedback, int MaxAttempts, InvalidOutcome Then);

public enum InvalidOutcome
{
    /// <summary>Open an <c>escalation</c> gate on the run.</summary>
    Escalate = 0,

    /// <summary>The agent run and its job end <c>failed</c>.</summary>
    Fail = 1,
}

/// <summary>FR-283, §4.53: the gate that must be approved to enter the stage when the run's approval mode is in
/// <see cref="Modes"/>. <see cref="Approves"/> is the artifact the approval locks, or <c>null</c> (<c>none</c>).</summary>
public sealed record StageGate(string Type, IReadOnlyList<ApprovalMode> Modes, string? Approves, bool ApprovalLinks);

/// <summary>A cap on iterations: a fixed <see cref="Value"/> (1–100) or a <see cref="Setting"/> name (per project).</summary>
public sealed record IterationCap(int? Value, string? Setting)
{
    public static IterationCap Fixed(int value) => new(value, null);

    public static IterationCap FromSetting(string setting) => new(null, setting);
}

/// <summary>
/// FR-281: <c>loop: { to, on, max_iterations }</c>, back to an earlier stage when the stage's outcome is <see cref="On"/>
/// (<c>null</c>: the pack decides). <see cref="Requires"/> (addition to §4.49, for §4.51's <c>sdlc.review-cycle-ready</c>)
/// is the loop's own entry check, in the same shape as a stage's <c>requires:</c>.
/// </summary>
public sealed record LoopDefinition(string To, string? On, IterationCap? MaxIterations, IReadOnlyList<StageRequirement> Requires)
{
    /// <summary>The default <see cref="Title"/>.</summary>
    public const string DefaultTitle = "Loop";

    /// <summary>
    /// <c>title</c> (addition to §4.49, M58): what the loop is called in transition reasons and errors (the SDLC's
    /// "Review cycle"); <see cref="DefaultTitle"/> when omitted.
    /// </summary>
    public string? Title { get; init; }

    public string EffectiveTitle => string.IsNullOrWhiteSpace(Title) ? DefaultTitle : Title;
}

/// <summary>Turns keys into words for default texts.</summary>
public static class KeyWords
{
    /// <summary><c>release_completed</c> → "Release completed": underscores and hyphens become spaces, the first letter
    /// upper-case.</summary>
    public static string Humanise(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var words = key.Replace('_', ' ').Replace('-', ' ');
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>"Review cycle" → "review cycle": the first letter lower-cased (for use mid-sentence).</summary>
    public static string LowerFirst(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length > 0 && char.IsUpper(text[0]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;
    }
}

/// <summary>
/// FR-281: <c>on_reject: &lt;stage&gt;</c>, or <c>on_reject: { to, max_iterations }</c> (addition to §4.49): an
/// <c>on_reject</c> pointing backwards is a loop and needs the cap (<see cref="DefinitionErrorCodes.UncappedLoop"/>).
/// </summary>
public sealed record RejectTransition(string To, IterationCap? MaxIterations);
