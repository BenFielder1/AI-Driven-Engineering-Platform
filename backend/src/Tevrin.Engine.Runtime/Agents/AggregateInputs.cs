using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>FR-346: the list inputs of an aggregate stage (<c>aggregate: { from }</c>), read from the run's artifacts.</summary>
public static class AggregateInputs
{
    /// <summary>
    /// Each list placeholder (<c>&lt;KEY&gt;_ALL</c>) of stage <paramref name="stageId"/>'s aggregate, with its items: a
    /// fan-out source's newest version of every variant (labelled by variant, in branch order), or each parallel branch
    /// stage's newest output (labelled by the branch stage id). Empty when the stage has no aggregate. An output that fails
    /// its integrity check (FR-20) is left out, never fed to an agent or handler.
    /// </summary>
    public static async Task<IReadOnlyList<(string Placeholder, IReadOnlyList<AggregateItem> Items)>> LoadAsync(
        IAgentStageStore store, PipelineRun run, PipelineDefinition definition, string stageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.FindAnyStage(stageId)?.Aggregate is not { } aggregate || definition.FindStage(aggregate.From) is not { } source)
        {
            return [];
        }

        var lists = new List<(string, IReadOnlyList<AggregateItem>)>();
        if (source.FanOut is not null && source.Output is { } output)
        {
            var order = FanOutRules.Branches(source).Select(b => b.Label).ToList();
            var items = (await store.RunVariantsAsync(run, output, cancellationToken))
                .Where(Intact)
                .OrderBy(a => order.IndexOf(a.Variant!) is var i and >= 0 ? i : int.MaxValue)
                .Select(a => new AggregateItem(a.Variant!, a.Content))
                .ToList();
            lists.Add((FanOutRules.AllPlaceholder(output), items));
            return lists;
        }

        var sources = FanOutRules.AggregateSources(source);
        var stored = (await store.RunLatestAsync(run, [.. sources.Select(s => s.ArtifactKey).Distinct(StringComparer.Ordinal)], cancellationToken))
            .Where(Intact)
            .ToList();
        foreach (var group in sources.GroupBy(s => s.ArtifactKey, StringComparer.Ordinal))
        {
            var items = group
                .SelectMany(s => stored.Where(a => string.Equals(a.Type, s.ArtifactKey, StringComparison.Ordinal)).Select(a => new AggregateItem(s.Label, a.Content)))
                .ToList();
            lists.Add((FanOutRules.AllPlaceholder(group.Key), items));
        }

        return lists;
    }

    private static bool Intact(Artifact a) => ArtifactRules.IntegrityOf(a.Status, a.Content, a.ContentHash) == ArtifactIntegrity.Ok;

    /// <summary>All the items of every list, for a <c>disagreement</c> guard or a handler (FR-346's typed list).</summary>
    public static async Task<IReadOnlyList<AggregateItem>> ItemsAsync(
        IAgentStageStore store, PipelineRun run, PipelineDefinition definition, string stageId, CancellationToken cancellationToken) =>
        [.. (await LoadAsync(store, run, definition, stageId, cancellationToken)).SelectMany(l => l.Items)];
}
