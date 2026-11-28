using System.Globalization;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// FR-354 (0.8.0): one <c>followups:</c> entry: stage <see cref="Stage"/> runs once per duration in <see cref="After"/>
/// (kept as written, e.g. <c>1d</c>), measured from the run's <c>completed_at</c> (§4.70).
/// </summary>
public sealed record FollowUpDefinition(string Stage, IReadOnlyList<string> After);

/// <summary>FR-354, §4.70: follow-up durations, due times and output labels (pure).</summary>
public static class FollowUpRules
{
    /// <summary>The placeholder a follow-up's prompt reads the run's outcome scores from (FR-353): <c>{{OUTCOME_SCORES}}</c>.</summary>
    public const string OutcomeScoresPlaceholder = "OUTCOME_SCORES";

    /// <summary>The outcome scores as one <c>metric: score</c> line each (invariant culture), or <c>(none)</c>.</summary>
    public static string RenderScores(IEnumerable<(string Metric, double Score)> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        var lines = scores.Select(s => $"{s.Metric}: {s.Score.ToString("0.####", CultureInfo.InvariantCulture)}").ToList();
        return lines.Count == 0 ? "(none)" : string.Join('\n', lines);
    }

    /// <summary>
    /// FR-354: a follow-up job does nothing when its run isn't <c>completed</c> (cancelled before it was due, or not done) or
    /// its execution belongs to another run or has already finished (a redelivery).
    /// </summary>
    public static bool IsStale(Entities.PipelineRun run, Entities.StageExecution? execution)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Status != GenericRunStatus.Completed
            || (execution is not null && (execution.PipelineRunId != run.Id || execution.ExitedAt is not null || execution.FollowUp is null));
    }

    /// <summary>The prefix of a follow-up's label: <c>followup:&lt;duration&gt;</c>.</summary>
    public const string LabelPrefix = "followup:";

    /// <summary>
    /// A duration <c>&lt;n&gt;d</c>, <c>&lt;n&gt;h</c> or <c>&lt;n&gt;m</c> (days of 24 h, hours, minutes; no timezone applies),
    /// with <c>n</c> a positive integer; <c>null</c> when it isn't one or isn't greater than zero.
    /// </summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 2)
        {
            return null;
        }

        var digits = text[..^1];
        if (!digits.All(char.IsAsciiDigit)
            || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            || n <= 0
            || n > 36_500L * 24 * 60)
        {
            return null;
        }

        return text[^1] switch
        {
            'd' when n <= 36_500 => TimeSpan.FromDays(n),
            'h' when n <= 36_500L * 24 => TimeSpan.FromHours(n),
            'm' => TimeSpan.FromMinutes(n),
            _ => null,
        };
    }

    /// <summary>§4.70: a follow-up's due time, <c>completed_at + after</c>.</summary>
    public static DateTimeOffset DueAt(DateTimeOffset completedAt, TimeSpan after) => completedAt + after;

    /// <summary>The label a follow-up's outputs and execution carry: <c>followup:&lt;duration&gt;</c>.</summary>
    public static string Label(string duration) => LabelPrefix + duration;

    /// <summary>Whether <paramref name="label"/> is a follow-up label.</summary>
    public static bool IsLabel(string? label) => label is not null && label.StartsWith(LabelPrefix, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="stageId"/> is a follow-up stage of <paramref name="definition"/>.</summary>
    public static bool IsFollowUpStage(PipelineDefinition definition, string? stageId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.FollowUps.Any(f => string.Equals(f.Stage, stageId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether <paramref name="label"/> is a declared follow-up of <paramref name="stageId"/> (the stage is a follow-up
    /// stage and the duration is in its <c>after</c>).
    /// </summary>
    public static bool IsDeclared(PipelineDefinition definition, string? stageId, string? label)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return IsLabel(label)
            && definition.FollowUps.Any(f => string.Equals(f.Stage, stageId, StringComparison.Ordinal)
                && f.After.Contains(label![LabelPrefix.Length..], StringComparer.Ordinal));
    }

    /// <summary>
    /// §4.70: every follow-up a run completed at <paramref name="completedAt"/> schedules: the stage, its label and its due
    /// time, in declared order (durations that don't parse are skipped; the validator rejects them).
    /// </summary>
    public static IReadOnlyList<ScheduledFollowUp> Schedule(PipelineDefinition definition, DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return
        [
            .. from f in definition.FollowUps
               from after in f.After
               let span = ParseDuration(after)
               where span is not null
               select new ScheduledFollowUp(f.Stage, Label(after), DueAt(completedAt, span.Value)),
        ];
    }
}

/// <summary>FR-354: one follow-up to queue: stage <see cref="Stage"/> with label <see cref="Label"/> at <see cref="DueAt"/>.</summary>
public sealed record ScheduledFollowUp(string Stage, string Label, DateTimeOffset DueAt);
