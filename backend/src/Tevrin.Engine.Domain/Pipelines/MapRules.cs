using System.Globalization;
using System.Text.Json;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>FR-355: where one map item execution is.</summary>
public enum MapItemStatus
{
    /// <summary>Not finished (never run, queued, running, or waiting on a retry).</summary>
    Open = 0,

    /// <summary>Its worker finished; its output (if any) is one item of the collection.</summary>
    Completed = 1,

    /// <summary>Its worker failed permanently.</summary>
    Failed = 2,
}

/// <summary>
/// FR-355: one item execution as <see cref="MapRules.Next"/> sees it. <see cref="Busy"/>: it has a queued or running
/// <c>stage.run</c> job; <see cref="Resumable"/>: an open item without one may be queued (it never ran, or its last job
/// neither failed nor died, or this is a retry; <see cref="RunDriverRules.ResumeBranch"/>).
/// </summary>
public sealed record MapItemState(int Index, MapItemStatus Status, bool Busy, bool Resumable);

/// <summary>FR-355, D-0.8.0-24: the map's progress, counted from its item rows.</summary>
public sealed record MapProgress(int Total, int Completed, int Failed)
{
    public int Open => Total - Completed - Failed;
}

/// <summary>FR-355: the source array of a map, read from its artifact: each element's JSON text, or why it can't be read.</summary>
public sealed record MapItems(IReadOnlyList<string> Items, string? Problem)
{
    public static MapItems Invalid(string problem) => new([], problem);
}

/// <summary>FR-355, §4.68: what the driver does next with a map stage.</summary>
public abstract record MapNext
{
    private MapNext()
    {
    }

    /// <summary>Queue these open items (index order; possibly none: the window is full or the rest wait on a retry).</summary>
    public sealed record Start(IReadOnlyList<int> Items) : MapNext;

    /// <summary>Every item has finished within the failure limit: reduce (if declared), then the stage's transition.</summary>
    public sealed record Finish : MapNext;

    /// <summary>§4.68: the failures fail the stage; no further item starts.</summary>
    public sealed record Failed(string Reason) : MapNext;
}

/// <summary>FR-355: the fact a map item handler gets: its 0-based index and its element (JSON text).</summary>
public sealed record MapItemFact(int Index, string Item);

/// <summary>Which construct role an agent request plays on a map stage.</summary>
public enum MapAgentRole
{
    None = 0,
    Item = 1,
    Reduce = 2,
}

/// <summary>
/// FR-355, §4.68: the pure rules of map-reduce stages. A map stage's own execution opens one item execution per element
/// (<see cref="StageExecution.ParentExecutionId"/> = the map's execution, <see cref="StageExecution.ItemIndex"/> 0…n-1) and
/// keeps at most <c>max_parallel</c> of them running, in index order. Failures are counted against <c>max_failed</c>
/// (<c>skip</c>) or fail the stage at once (<c>fail</c>). When every item has finished, the reduce step (if any) gets every
/// item output (the FR-346 list shape, labelled <see cref="ItemLabel"/>).
/// </summary>
public static class MapRules
{
    /// <summary>The placeholder a map item agent's prompt gets its element in (its JSON text).</summary>
    public const string ItemPlaceholder = "MAP_ITEM";

    /// <summary>The placeholder a map item agent's prompt gets its 0-based index in.</summary>
    public const string IndexPlaceholder = "MAP_INDEX";

    /// <summary>The prefix of an item's label in the reduce list: <c>item-0</c> … <c>item-(n-1)</c>.</summary>
    public const string ItemLabelPrefix = "item-";

    /// <summary>The label of item <paramref name="index"/> (0-based) in the reduce list and the isolation check.</summary>
    public static string ItemLabel(int index) => ItemLabelPrefix + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The elements of the array at <paramref name="itemPath"/> (dot-separated field names; empty: the document is the
    /// array) in <paramref name="json"/>, each as its JSON text. A missing artifact, invalid JSON, a missing field or a
    /// value that isn't an array is a problem.
    /// </summary>
    public static MapItems ReadItems(string? json, string itemPath)
    {
        ArgumentNullException.ThrowIfNull(itemPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return MapItems.Invalid("the map's source artifact is empty");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var node = doc.RootElement;
            foreach (var field in itemPath.Length == 0 ? [] : itemPath.Split('.'))
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(field, out node))
                {
                    return MapItems.Invalid($"the map's source has no field '{itemPath}'");
                }
            }

            return node.ValueKind == JsonValueKind.Array
                ? new MapItems([.. node.EnumerateArray().Select(e => e.GetRawText())], null)
                : MapItems.Invalid(itemPath.Length == 0 ? "the map's source is not a JSON array" : $"'{itemPath}' in the map's source is not an array");
        }
        catch (JsonException)
        {
            return MapItems.Invalid("the map's source is not valid JSON");
        }
    }

    /// <summary>§4.68: more elements than <c>max_items</c> fail the stage (<c>MAP_TOO_MANY_ITEMS</c>) before any item runs.</summary>
    public static bool TooMany(MapDefinition map, int count)
    {
        ArgumentNullException.ThrowIfNull(map);
        return count > map.MaxItems;
    }

    /// <summary>An item execution's status: open until it exits; then completed or failed by its outcome.</summary>
    public static MapItemStatus StatusOf(StageExecution item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.ExitedAt is null
            ? MapItemStatus.Open
            : string.Equals(item.Outcome, BranchOutcomes.Completed, StringComparison.Ordinal) ? MapItemStatus.Completed : MapItemStatus.Failed;
    }

    /// <summary>D-0.8.0-24: progress grouped from the item rows.</summary>
    public static MapProgress Progress(IReadOnlyCollection<MapItemState> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new MapProgress(
            items.Count,
            items.Count(i => i.Status == MapItemStatus.Completed),
            items.Count(i => i.Status == MapItemStatus.Failed));
    }

    /// <summary>
    /// §4.68: whether the failures fail the stage: with <c>fail</c>, the first one; with <c>skip</c>, more than
    /// <c>max_failed</c> (a count, or a percentage of the total rounded down). Example: 200 items at <c>5%</c> allow 10, so
    /// 3 failures pass and 11 fail the stage.
    /// </summary>
    public static bool StageFailed(MapDefinition map, MapProgress progress)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(progress);
        return map.OnItemFailure == MapItemFailure.Fail
            ? progress.Failed > 0
            : progress.Failed > map.MaxFailed.Allowed(progress.Total);
    }

    /// <summary>
    /// FR-355, §4.68: the map's next step. Failures beyond the limit fail the stage (nothing more is queued); every item
    /// finished → <see cref="MapNext.Finish"/>; otherwise the open, idle, resumable items in index order, as many as fit
    /// beside the busy ones under <c>max_parallel</c>.
    /// </summary>
    public static MapNext Next(MapDefinition map, IReadOnlyCollection<MapItemState> items)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(items);
        var progress = Progress(items);
        if (StageFailed(map, progress))
        {
            return new MapNext.Failed(map.OnItemFailure == MapItemFailure.Fail
                ? $"item {items.Where(i => i.Status == MapItemStatus.Failed).Min(i => i.Index)} failed (on_item_failure: fail)"
                : $"{progress.Failed} of {progress.Total} items failed (max_failed {map.MaxFailed.ToWire()} allows {map.MaxFailed.Allowed(progress.Total)})");
        }

        if (progress.Open == 0)
        {
            return new MapNext.Finish();
        }

        var inFlight = items.Count(i => i.Status == MapItemStatus.Open && i.Busy);
        var slots = Math.Max(0, map.MaxParallel - inFlight);
        return new MapNext.Start([.. items
            .Where(i => i.Status == MapItemStatus.Open && !i.Busy && i.Resumable)
            .OrderBy(i => i.Index)
            .Take(slots)
            .Select(i => i.Index)]);
    }

    /// <summary>
    /// Whether an open, idle item may be queued, from its last job's status (<c>null</c>: it never ran). A never-run item
    /// always may. One that ran before (it escalated, or failed and was left open) is resumed only by the map's own job (a
    /// retry, a redelivery, or a gate decision's re-queue), never when a sibling item ends, and not while the run has a
    /// pending gate (an escalated item waits for its decision); then as <see cref="RunDriverRules.ResumeBranch"/> (a failed
    /// or dead last job only on a retry).
    /// </summary>
    public static bool Resumable(JobStatus? lastJob, bool fromItemEnd, bool gatePending, bool retry) =>
        lastJob is null || (!fromItemEnd && !gatePending && RunDriverRules.ResumeBranch(retry, lastJob));

    /// <summary>
    /// The role of a request for agent <paramref name="agentKey"/> on <paramref name="stage"/>: a map item (an item index is
    /// set and the key is the map's agent), the reduce step (no item index, the key is the reduce's agent), or neither.
    /// </summary>
    public static MapAgentRole RoleOf(StageDefinition stage, string? agentKey, int? itemIndex)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Map is not { } map || agentKey is null)
        {
            return MapAgentRole.None;
        }

        if (itemIndex is not null)
        {
            return string.Equals(map.Agent, agentKey, StringComparison.Ordinal) ? MapAgentRole.Item : MapAgentRole.None;
        }

        return string.Equals(stage.Reduce?.Agent, agentKey, StringComparison.Ordinal) ? MapAgentRole.Reduce : MapAgentRole.None;
    }

    /// <summary>
    /// The placeholders the engine fills for agent <paramref name="agentKey"/> of map stage <paramref name="stage"/>: the
    /// map's agent gets <c>{{MAP_ITEM}}</c> and <c>{{MAP_INDEX}}</c>; the reduce agent gets the item outputs as
    /// <c>{{&lt;OUTPUT&gt;_ALL}}</c> (FR-346). Empty for other agents and stages.
    /// </summary>
    public static IReadOnlyList<string> SuppliedPlaceholders(StageDefinition stage, string? agentKey)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Map is not { } map || agentKey is null)
        {
            return [];
        }

        var supplied = new List<string>();
        if (string.Equals(map.Agent, agentKey, StringComparison.Ordinal))
        {
            supplied.AddRange([ItemPlaceholder, IndexPlaceholder]);
        }

        if (string.Equals(stage.Reduce?.Agent, agentKey, StringComparison.Ordinal) && stage.Output is { Length: > 0 } output)
        {
            supplied.Add(FanOutRules.AllPlaceholder(output));
        }

        return supplied;
    }
}
