using System.Globalization;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// FR-357: one earlier run that may give a <c>from_runs</c> input an item: the run, how and when it ended, and the final
/// version of the input's artifact in it: its newest version that is no map item and not superseded, a plain version
/// (no variant) first, else the newest follow-up output (<c>followup:&lt;duration&gt;</c>, FR-354). The version's status
/// and stored hash let the reader drop content that fails its integrity check (FR-20).
/// </summary>
public sealed record RunsInputCandidate(
    Guid RunId, string PipelineId, GenericRunStatus Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string Content)
{
    public Artifacts.ArtifactStatus ArtifactStatus { get; init; } = Artifacts.ArtifactStatus.Locked;

    public string ContentHash { get; init; } = string.Empty;
}

/// <summary>FR-357: the list one <c>from_runs</c> input collected (FR-346 shape), with the placeholder an agent reads it as.</summary>
public sealed record RunsInputList(RunsInputDefinition Input, string Placeholder, IReadOnlyList<AggregateItem> Items);

/// <summary>FR-357: what a handler stage with <c>from_runs</c> inputs receives as a fact: one list per input, in
/// <c>inputs:</c> order.</summary>
public sealed record RunsInputFact(IReadOnlyList<RunsInputList> Lists)
{
    /// <summary>The list of the input reading <paramref name="artifact"/> from <paramref name="pipeline"/>, or <c>null</c>.</summary>
    public RunsInputList? For(string pipeline, string artifact) =>
        Lists.FirstOrDefault(l => string.Equals(l.Input.Pipeline, pipeline, StringComparison.Ordinal)
            && string.Equals(l.Input.Artifact, artifact, StringComparison.Ordinal));
}

/// <summary>
/// FR-357 (§3.6, D-0.8.0-23): the pure rules of a <c>from_runs</c> stage input. The list holds the final version of the
/// artifact from each of the project's runs of the named pipeline that ended with the input's status within the window
/// (measured back from now, inclusive), the current run left out, newest completion first, at most
/// <see cref="RunsInputDefinition.MaxRuns"/> runs. Each item is labelled with its run's start time (UTC, to the second).
/// An agent reads the list as <c>{{&lt;ARTIFACT&gt;_RUNS}}</c>.
/// </summary>
public static class RunsInputRules
{
    /// <summary>The placeholder suffix of a <c>from_runs</c> list: <c>{{&lt;ARTIFACT&gt;_RUNS}}</c>.</summary>
    public const string RunsSuffix = "_RUNS";

    /// <summary>The item label format: the run's start time, e.g. <c>2030-12-02T06:00:00Z</c>.</summary>
    public const string LabelFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>The placeholder an agent reads <paramref name="input"/>'s list as (e.g. <c>SCORES_RUNS</c>).</summary>
    public static string Placeholder(RunsInputDefinition input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Agents.AgentPromptPlaceholders.Of(input.Artifact) + RunsSuffix;
    }

    /// <summary>The earliest completion time in the window, or <c>null</c> when the window isn't a valid duration.</summary>
    public static DateTimeOffset? Since(RunsInputDefinition input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        return FollowUpRules.ParseDuration(input.Window) is { } window ? now - window : null;
    }

    /// <summary>An item's label: its run's start time in UTC.</summary>
    public static string Label(DateTimeOffset startedAt) => startedAt.UtcDateTime.ToString(LabelFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// The list <paramref name="input"/> collects from <paramref name="candidates"/> for run <paramref name="currentRunId"/>
    /// at <paramref name="now"/>: the input's pipeline and status, completed within the window, not the current run,
    /// newest completion first (then newest start), capped at <see cref="RunsInputDefinition.MaxRuns"/>.
    /// </summary>
    public static IReadOnlyList<AggregateItem> Select(
        RunsInputDefinition input, IEnumerable<RunsInputCandidate> candidates, Guid currentRunId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(candidates);
        if (Since(input, now) is not { } since)
        {
            return [];
        }

        return [.. candidates
            .Where(c => c.RunId != currentRunId
                && string.Equals(c.PipelineId, input.Pipeline, StringComparison.Ordinal)
                && c.Status == input.Status
                && c.CompletedAt is { } at && at >= since && at <= now)
            .OrderByDescending(c => c.CompletedAt)
            .ThenByDescending(c => c.StartedAt)
            .Take(RunsInputDefinition.MaxRuns)
            .Select(c => new AggregateItem(Label(c.StartedAt), c.Content))];
    }

    /// <summary>The input position of a stage's plain input <paramref name="plainIndex"/> in its YAML <c>inputs:</c> list,
    /// counting the <c>from_runs</c> entries before it (for error paths).</summary>
    public static int InputPosition(StageDefinition stage, int plainIndex)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var position = plainIndex;
        foreach (var runs in stage.FromRuns.OrderBy(r => r.Position))
        {
            if (runs.Position <= position)
            {
                position++;
            }
        }

        return position;
    }
}
