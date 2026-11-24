using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Domain.Entities;

/// <summary>
/// FR-277, §5: a stored definition (<c>pipeline_definitions</c>), keyed by its definition hash (§4.50). Written the first
/// time a run uses the hash and never updated, so a run pinned to the hash can always load what it started with.
/// <see cref="CanonicalJson"/> is a <c>jsonb</c> column: PostgreSQL keeps its meaning, not its bytes, so it is read back
/// with a JSON reader and re-canonicalised (D-0.7.0-26), never parsed as YAML.
/// </summary>
public sealed class PipelineDefinitionRecord
{
    /// <summary>SHA-256 of the canonical JSON, lower-case hex (64 characters).</summary>
    public string Hash { get; init; } = string.Empty;

    public string PipelineId { get; init; } = string.Empty;

    public int Version { get; init; }

    public string PackId { get; init; } = string.Empty;

    public string CanonicalJson { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// FR-292, FR-294, §5 (D-0.7.0-9, D-0.7.0-10): a pipeline run (<c>pipeline_runs</c>). It belongs to a project
/// (D-0.7.0-11), is pinned to the definition hash it started with, and holds the stage, the pack status, the generic
/// status, the checkpoint, the approval mode and the loop counters. The SDLC has one run per release version
/// (<see cref="SubjectId"/>); FR-334: an engine run may have none. Stored: the final generic statuses,
/// <see cref="GenericRunStatus.Active"/> and (FR-335) <see cref="GenericRunStatus.WaitingOnInput"/>; <c>waiting_on_gate</c>
/// and <c>running</c> are derived when the run is read (§4.57, D-0.7.0-28).
/// </summary>
public sealed class PipelineRun
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ProjectId { get; init; }

    public string PipelineId { get; init; } = string.Empty;

    /// <summary>The definition the run is pinned to (FR-277).</summary>
    public string DefinitionHash { get; init; } = string.Empty;

    /// <summary>What the run is about; the SDLC's release version id. Unique per pipeline when set; FR-334: <c>null</c> for
    /// a run without a subject, whose artifacts, gates and agent runs belong to the run alone.</summary>
    public Guid? SubjectId { get; init; }

    public string CurrentStage { get; set; } = string.Empty;

    /// <summary>The pack's status (the SDLC's project status wire value).</summary>
    public string PackStatus { get; set; } = string.Empty;

    /// <summary>The stored generic status: <c>active</c>, <c>waiting_on_input</c> (FR-335), <c>completed</c>, <c>failed</c>
    /// or <c>cancelled</c>.</summary>
    public GenericRunStatus Status { get; set; } = GenericRunStatus.Active;

    public int Checkpoint { get; set; } = 1;

    public ApprovalMode ApprovalMode { get; set; }

    /// <summary>Loop counters by stage id (for the SDLC, <c>review</c> = the project's review cycle). Replace, don't
    /// mutate, the dictionary.</summary>
    public IReadOnlyDictionary<string, int> Iterations { get; set; } = PipelineRunRules.NoIterations;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint RowVersion { get; set; }
}

/// <summary>Pure rules for stored run state (§4.57, D-0.7.0-28).</summary>
public static class PipelineRunRules
{
    public static IReadOnlyDictionary<string, int> NoIterations { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>The final generic statuses (§4.57): a run in one of them has ended.</summary>
    public static bool IsFinal(GenericRunStatus status) =>
        status is GenericRunStatus.Completed or GenericRunStatus.Failed or GenericRunStatus.Cancelled;

    /// <summary>
    /// FR-331: whether a new run may start: always with <c>concurrency: { runs: many }</c>, otherwise only when the
    /// project has no open (non-final) run of the pipeline (else <c>RUN_ALREADY_ACTIVE</c>). For subject-less runs this is
    /// the only check (the open-run index covers runs with a subject), so the caller holds an advisory lock on (project,
    /// pipeline) while it checks and inserts.
    /// </summary>
    public static bool MayStart(ConcurrencyDefinition concurrency, bool hasOpenRun)
    {
        ArgumentNullException.ThrowIfNull(concurrency);
        return concurrency.Runs == RunConcurrency.Many || !hasOpenRun;
    }

    /// <summary>
    /// FR-335: the run driver may stop a run at a stage whose required input is missing only from <c>active</c>
    /// (<c>active</c> → <c>waiting_on_input</c>). No other path enters <see cref="GenericRunStatus.WaitingOnInput"/>.
    /// </summary>
    public static bool CanWaitOnInput(GenericRunStatus stored) => stored == GenericRunStatus.Active;

    /// <summary>FR-335: the input arrived, so the driver resumes the run (<c>waiting_on_input</c> → <c>active</c>).</summary>
    public static bool CanResumeFromInput(GenericRunStatus stored) => stored == GenericRunStatus.WaitingOnInput;

    /// <summary>
    /// FR-335: the stored status after the driver's input move, or <c>null</c> when the move isn't allowed from
    /// <paramref name="stored"/>: <paramref name="waiting"/> → <c>waiting_on_input</c>, otherwise → <c>active</c>.
    /// </summary>
    public static GenericRunStatus? InputMove(GenericRunStatus stored, bool waiting) =>
        waiting
            ? CanWaitOnInput(stored) ? GenericRunStatus.WaitingOnInput : null
            : CanResumeFromInput(stored) ? GenericRunStatus.Active : null;

    /// <summary>
    /// The generic status stored for a run whose pack status is <paramref name="packStatus"/>: the final status it maps to
    /// in <paramref name="definition"/>, else <see cref="GenericRunStatus.Active"/> (<c>waiting_on_gate</c> and
    /// <c>running</c> are never stored).
    /// </summary>
    public static GenericRunStatus StoredStatus(PipelineDefinition definition, string packStatus)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.GenericStatusOf(packStatus) is { } generic && IsFinal(generic) ? generic : GenericRunStatus.Active;
    }
}

/// <summary>
/// The generic transition kinds stored in <c>state_transitions.kind</c> (§4.51), plus <see cref="Start"/> for the first
/// transition of a run (a new project or a new version). Null on rows written before 0.7.0.
/// </summary>
public static class PipelineTransitionKinds
{
    public const string Start = "start";
    public const string Advance = "advance";
    public const string Loop = "loop";
    public const string Rollback = "rollback";
    public const string Fallback = "fallback";
    public const string StatusChange = "status_change";
    public const string Noop = "noop";
}
