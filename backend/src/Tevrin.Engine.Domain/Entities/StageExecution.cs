namespace Tevrin.Engine.Domain.Entities;

/// <summary>
/// FR-297, §5: one entry of a pipeline run into a stage (<c>stage_executions</c>). Opened when the run enters the stage and
/// closed (<see cref="ExitedAt"/>, <see cref="Outcome"/>) when it leaves, in the same save as the transition (D-0.7.0-32).
/// </summary>
public sealed class StageExecution
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid PipelineRunId { get; init; }

    public string StageId { get; init; } = string.Empty;

    /// <summary>1-based count of the run's entries into <see cref="StageId"/> (for the SDLC's review stage, the review cycle
    /// on the normal path).</summary>
    public int Iteration { get; init; }

    /// <summary>The run's checkpoint on entry.</summary>
    public int Checkpoint { get; init; }

    public DateTimeOffset EnteredAt { get; init; }

    public DateTimeOffset? ExitedAt { get; set; }

    /// <summary>How the run left the stage: the transition kind (<see cref="PipelineTransitionKinds"/>), or the run's final
    /// status (<c>completed</c>, <c>failed</c>, <c>cancelled</c>) when it ended in the stage. Null while open.</summary>
    public string? Outcome { get; set; }

    /// <summary>FR-343–FR-349, §5: the parallel branch this execution ran in; null outside a branch.</summary>
    public string? Branch { get; init; }

    /// <summary>§5: the for-each item index (M70); null outside a for-each.</summary>
    public int? ItemIndex { get; init; }

    /// <summary>FR-343–FR-349, §5: the variant label this execution produced; null otherwise.</summary>
    public string? Variant { get; init; }

    /// <summary>FR-343–FR-349, §5: the veto findings (JSON) when a veto stage stopped the run; null otherwise.</summary>
    public string? VetoFindings { get; set; }
}

/// <summary>FR-297: pure rules for stage executions.</summary>
public static class StageExecutionRules
{
    /// <summary>
    /// True when a transition from <paramref name="fromStage"/> to <paramref name="toStage"/> of kind <paramref name="kind"/>
    /// is an entry into <paramref name="toStage"/>: a change of stage, or a start, loop, rollback or fallback (which re-enter
    /// even the same stage). A status change or no-op within a stage isn't an entry.
    /// </summary>
    public static bool IsEntry(string? fromStage, string toStage, string? kind) =>
        fromStage is null
        || !string.Equals(fromStage, toStage, StringComparison.Ordinal)
        || kind is PipelineTransitionKinds.Start or PipelineTransitionKinds.Loop or PipelineTransitionKinds.Rollback or PipelineTransitionKinds.Fallback;

    /// <summary>The outcome stored on the execution a transition leaves: its kind, or (rows without one) <c>start</c> for a
    /// first transition and <c>advance</c> otherwise.</summary>
    public static string LeavingOutcome(string? fromStage, string? kind) =>
        kind ?? (fromStage is null ? PipelineTransitionKinds.Start : PipelineTransitionKinds.Advance);
}
