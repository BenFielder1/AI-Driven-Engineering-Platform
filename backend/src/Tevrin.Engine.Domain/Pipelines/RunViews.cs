using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>FR-300: a stage's state in a run's stage list.</summary>
public enum StageState
{
    Pending = 0,
    Active = 1,
    Completed = 2,
    Skipped = 3,
}

/// <summary>One entry into a stage (a <c>stage_executions</c> row, or one derived from the run's transitions).</summary>
public sealed record StageEntry(string StageId, DateTimeOffset EnteredAt, DateTimeOffset? ExitedAt);

/// <summary>A run transition as the stage and timeline views need it (<c>state_transitions</c>, host-owned).</summary>
public sealed record TransitionEntry(string? FromStage, string ToStage, string? Kind, DateTimeOffset At);

/// <summary>FR-300: one stage of a run: its state, entry count (iterations) and timings.</summary>
public sealed record StageView(
    string Id,
    string Title,
    StageState State,
    int Iterations,
    DateTimeOffset? EnteredAt,
    DateTimeOffset? ExitedAt,
    long? DurationMs);

/// <summary>FR-300, §4.57: pure rules for the generic run views.</summary>
public static class RunViewRules
{
    /// <summary>
    /// §4.57, D-0.7.0-28: the generic status of a run from its stored status (<c>active</c> or a final one):
    /// final stays final; otherwise <c>waiting_on_gate</c> with a pending gate on the run, <c>running</c> with a queued or
    /// running job for it, else <c>active</c>.
    /// </summary>
    public static GenericRunStatus DeriveStatus(GenericRunStatus stored, bool hasPendingGate, bool hasActiveJob)
    {
        if (PipelineRunRules.IsFinal(stored))
        {
            return stored;
        }

        return hasPendingGate ? GenericRunStatus.WaitingOnGate : hasActiveJob ? GenericRunStatus.Running : GenericRunStatus.Active;
    }

    /// <summary>
    /// Stage entries derived from a run's transitions (time order), for runs whose earlier stages predate
    /// <c>stage_executions</c> (D-0.7.0-32: no history backfill). An entry is <see cref="StageExecutionRules.IsEntry"/>;
    /// it is exited at the next entry.
    /// </summary>
    public static IReadOnlyList<StageEntry> EntriesFromTransitions(IEnumerable<TransitionEntry> transitions)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        var entries = new List<StageEntry>();
        foreach (var t in transitions.OrderBy(t => t.At))
        {
            if (!StageExecutionRules.IsEntry(t.FromStage, t.ToStage, t.Kind))
            {
                continue;
            }

            if (entries.Count > 0 && entries[^1].ExitedAt is null)
            {
                entries[^1] = entries[^1] with { ExitedAt = t.At };
            }

            entries.Add(new StageEntry(t.ToStage, t.At, null));
        }

        return entries;
    }

    /// <summary>
    /// FR-300: the run's stages in definition order. The current stage is <c>active</c> while the run isn't final, and
    /// every later stage is <c>pending</c> (D-0.7.0-35: also after a rollback or a loop, though its earlier entries still
    /// count in iterations and timings); an earlier stage that was entered (or the current stage of a final run) is
    /// <c>completed</c>; a stage never entered is
    /// <c>skipped</c> when it lies before the current stage or the run is final, else <c>pending</c>. Iterations count
    /// entries (D-0.7.0-32). Entries come from <paramref name="executions"/>; a stage with none falls back to
    /// <paramref name="fromTransitions"/>. <c>enteredAt</c>/<c>exitedAt</c> are the latest entry's; <c>durationMs</c> is
    /// the time spent over every entry (an open entry of the active stage counts until <paramref name="now"/>).
    /// </summary>
    public static IReadOnlyList<StageView> Stages(
        PipelineDefinition definition,
        string currentStage,
        bool runFinal,
        IReadOnlyList<StageEntry> executions,
        IReadOnlyList<StageEntry> fromTransitions,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(executions);
        ArgumentNullException.ThrowIfNull(fromTransitions);
        var currentIndex = definition.IndexOfStage(currentStage);
        var views = new List<StageView>(definition.Stages.Count);
        for (var i = 0; i < definition.Stages.Count; i++)
        {
            var stage = definition.Stages[i];
            var mine = Of(executions, stage.Id);
            if (mine.Count == 0)
            {
                mine = Of(fromTransitions, stage.Id);
            }

            var isCurrent = i == currentIndex;
            var state = isCurrent && !runFinal ? StageState.Active
                : !runFinal && currentIndex >= 0 && i > currentIndex ? StageState.Pending
                : mine.Count > 0 || isCurrent ? StageState.Completed
                : runFinal || (currentIndex >= 0 && i < currentIndex) ? StageState.Skipped
                : StageState.Pending;
            var last = mine.Count > 0 ? mine[^1] : null;
            long? duration = mine.Count == 0 ? null : mine.Sum(e => Milliseconds(e, isCurrent && !runFinal, now));
            views.Add(new StageView(stage.Id, stage.Title, state, mine.Count, last?.EnteredAt, last?.ExitedAt, duration));
        }

        return views;
    }

    private static List<StageEntry> Of(IReadOnlyList<StageEntry> entries, string stageId) =>
        [.. entries.Where(e => string.Equals(e.StageId, stageId, StringComparison.Ordinal)).OrderBy(e => e.EnteredAt)];

    private static long Milliseconds(StageEntry entry, bool open, DateTimeOffset now)
    {
        var end = entry.ExitedAt ?? (open ? now : entry.EnteredAt);
        return Math.Max(0, (long)(end - entry.EnteredAt).TotalMilliseconds);
    }
}
