using System.Globalization;
using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>FR-345: one fan-out branch: its variant label (the agent key, or <c>#i</c> for <c>count</c>) and the agent it
/// runs (<c>null</c>: the stage's primary agent).</summary>
public sealed record FanOutBranch(string Label, string? AgentKey);

/// <summary>FR-346: one branch output given to an aggregate stage, labelled with its branch or variant.</summary>
public sealed record AggregateItem(string Label, string Content);

/// <summary>FR-346: the typed list an aggregate stage's handler, and its <c>disagreement</c> guard, receive as a fact.</summary>
public sealed record AggregateListFact(IReadOnlyList<AggregateItem> Items);

/// <summary>
/// FR-345, FR-346, §4.65: the pure rules of fan-out stages and aggregation. A fan-out stage's own execution (no branch)
/// enters one execution per branch (<see cref="StageExecution.Branch"/> = the variant label, the stage id unchanged), and the
/// join, isolation and aggregate list are computed here.
/// </summary>
public static class FanOutRules
{
    /// <summary>The prefix of a <c>count</c> branch's label: <c>#1</c> … <c>#n</c>.</summary>
    public const string CountPrefix = "#";

    /// <summary>§4.65: the shortest trimmed sibling output the isolation check looks for. Shorter outputs ("yes", "{}",
    /// "OK") occur in almost any prompt, so finding one proves nothing.</summary>
    public const int MinIsolationProbeLength = 32;

    /// <summary>The placeholder suffix of an aggregate's list input: <c>{{&lt;KEY&gt;_ALL}}</c>.</summary>
    public const string AllSuffix = "_ALL";

    /// <summary>FR-345: the branches of <paramref name="stage"/>'s fan-out, in declaration order (empty without one); FR-349:
    /// a <c>variants</c> stage's v1…vn.</summary>
    public static IReadOnlyList<FanOutBranch> Branches(StageDefinition stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Variants is { Count: var variants })
        {
            // FR-349: variants are a count fan-out of the stage's own agent, labelled v1…vn.
            return [.. Enumerable.Range(1, variants).Select(i => new FanOutBranch(VariantRules.Label(i), null))];
        }

        return stage.FanOut switch
        {
            null => [],
            { Count: { } count } => [.. Enumerable.Range(1, count).Select(i => new FanOutBranch(CountLabel(i), null))],
            { Agents: var agents } => [.. agents.Select(key => new FanOutBranch(key, key))],
        };
    }

    /// <summary>The label of the <paramref name="index"/>th (1-based) <c>count</c> branch.</summary>
    public static string CountLabel(int index) => CountPrefix + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The branch labelled <paramref name="label"/> of <paramref name="stage"/>'s fan-out, or <c>null</c>.</summary>
    public static FanOutBranch? BranchOf(StageDefinition stage, string? label) =>
        label is null ? null : Branches(stage).FirstOrDefault(b => string.Equals(b.Label, label, StringComparison.Ordinal));

    /// <summary>Whether <paramref name="stageExecution"/> is a branch execution of fan-out stage <paramref name="stage"/>.</summary>
    public static bool InFanOut(StageDefinition stage, StageExecution stageExecution)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(stageExecution);
        return (stage.FanOut is not null || stage.Variants is not null)
            && string.Equals(stageExecution.StageId, stage.Id, StringComparison.Ordinal)
            && BranchOf(stage, stageExecution.Branch) is not null;
    }

    /// <summary>
    /// FR-345: the fan-out's join over the branch executions of its current entry (latest per label). Complete when every
    /// branch has completed; a branch that failed fails the stage (fan-out has no <c>on_branch_failure</c>), so only
    /// completed branches count.
    /// </summary>
    public static bool Joined(StageDefinition stage, IEnumerable<StageExecution> entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var completed = entry
            .Where(e => e.ExitedAt is not null && string.Equals(e.Outcome, BranchOutcomes.Completed, StringComparison.Ordinal))
            .Select(e => e.Branch)
            .ToHashSet(StringComparer.Ordinal);
        var branches = Branches(stage);
        return branches.Count > 0 && branches.All(b => completed.Contains(b.Label));
    }

    /// <summary>
    /// §4.65: the label of the first sibling whose output text appears in <paramref name="renderedInput"/>, or
    /// <c>null</c> when the input is isolated. Outputs shorter than <see cref="MinIsolationProbeLength"/>, and the branch's own label, are ignored. The comparison is
    /// ordinal on the trimmed output text.
    /// </summary>
    public static string? IsolationBreach(string renderedInput, string ownLabel, IEnumerable<AggregateItem> siblingOutputs)
    {
        ArgumentNullException.ThrowIfNull(renderedInput);
        ArgumentNullException.ThrowIfNull(siblingOutputs);
        foreach (var sibling in siblingOutputs)
        {
            if (string.Equals(sibling.Label, ownLabel, StringComparison.Ordinal))
            {
                continue;
            }

            var text = sibling.Content.Trim();
            if (text.Length >= MinIsolationProbeLength && renderedInput.Contains(text, StringComparison.Ordinal))
            {
                return sibling.Label;
            }
        }

        return null;
    }

    /// <summary>The aggregate list placeholder for artifact key <paramref name="artifactKey"/> (e.g. <c>FORECAST_ALL</c>).</summary>
    public static string AllPlaceholder(string artifactKey)
    {
        ArgumentNullException.ThrowIfNull(artifactKey);
        return Agents.AgentPromptPlaceholders.Of(artifactKey) + AllSuffix;
    }

    /// <summary>FR-346: an aggregate list rendered for an agent prompt: each item under a <c>### &lt;label&gt;</c> heading.</summary>
    public static string RenderList(IEnumerable<AggregateItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return string.Join("\n\n", items.Select(i => $"### {i.Label}\n{i.Content.Trim()}"));
    }

    /// <summary>
    /// FR-346: the artifact keys an aggregate reads from <paramref name="source"/>: the fan-out stage's output, or the
    /// outputs of every stage of a parallel group's branches.
    /// </summary>
    public static IReadOnlyList<(string Label, string ArtifactKey)> AggregateSources(StageDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.FanOut is not null)
        {
            return source.Output is { } output ? [(string.Empty, output)] : [];
        }

        if (source.Parallel is { } group)
        {
            return [.. group.Branches.SelectMany(b => b)
                .Where(s => s.Output is not null)
                .Select(s => (s.Id, s.Output!))];
        }

        return [];
    }
}
