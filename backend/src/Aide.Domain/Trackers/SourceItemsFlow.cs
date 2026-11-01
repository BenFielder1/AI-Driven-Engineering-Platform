using System.Text.RegularExpressions;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;

namespace Aide.Domain.Trackers;

/// <summary>
/// FR-172: a refinement run with a queued or running import waits under the FR-70 rule: re-queued, never a blocking sleep, up to
/// 10 minutes after the oldest such import was queued, then it goes on with <c>source_items_unavailable</c>. A version's failed
/// import that hasn't been retried also gives the warning. Pure.
/// </summary>
public static class SourceItemsWait
{
    public static SummaryWaitDecision Decide(DateTimeOffset? oldestPendingCreatedAt, bool hasFailedImport, DateTimeOffset now)
    {
        if (oldestPendingCreatedAt is not { } oldest)
        {
            return SummaryWaitDecision.Proceed(hasFailedImport ? AgentRunWarnings.SourceItemsUnavailable : null);
        }

        var deadline = oldest + CodebaseSummaryWait.MaxWait;
        if (now >= deadline)
        {
            return SummaryWaitDecision.Proceed(AgentRunWarnings.SourceItemsUnavailable);
        }

        var next = now + CodebaseSummaryWait.PollInterval;
        return new SummaryWaitDecision(true, next < deadline ? next : deadline, null);
    }
}

/// <summary>
/// FR-172 <b>Traceability:</b> spec requirements may carry <c>Source: PROJ-123</c>. <c>UNCOVERED_SOURCE_ITEMS</c> lists the imported,
/// not-done items no line cites. Advisory, never blocking (D-V5-6). Pure.
/// </summary>
public static partial class SourceTraceability
{
    /// <summary>Every key cited on a <c>Source:</c> line (also bold, and with several keys separated by commas).</summary>
    public static IReadOnlySet<string> CitedKeys(string specMarkdown)
    {
        ArgumentNullException.ThrowIfNull(specMarkdown);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SourceLine().Matches(specMarkdown))
        {
            foreach (var part in match.Groups[1].Value.Split([',', ';', ' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                keys.Add(part.Trim('*', '_', '`', '[', ']', '(', ')', '.'));
            }
        }

        return keys;
    }

    /// <summary>The warning, or <c>null</c> when every not-done item is cited (or nothing was imported).</summary>
    public static ValidationWarning? Check(string specMarkdown, IEnumerable<SourceItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var cited = CitedKeys(specMarkdown);
        var uncovered = items
            .Where(i => i.StatusCategory != SourceItemStatusCategory.Done && !cited.Contains(i.Key))
            .Select(i => i.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return uncovered.Count == 0
            ? null
            : new ValidationWarning(
                ErrorCodes.UncoveredSourceItems,
                $"No requirement cites {uncovered.Count} imported item(s): {string.Join(", ", uncovered)}. Add \"Source: KEY\" to the requirement that covers each, or leave it out on purpose.",
                uncovered);
    }

    [GeneratedRegex(@"\bSource:\**[ \t]*(.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SourceLine();
}

/// <summary>An import as the retry chain sees it: the import its completion replaces, and where it is.</summary>
public sealed record TrackerImportLink(Guid Id, Guid? SupersedesId, TrackerImportStatus Status);

/// <summary>What a re-import does: nothing (<see cref="NotFound"/>), hands back the import already on its way (<see cref="Pending"/>), or adds one replacing <see cref="Supersedes"/>.</summary>
public sealed record TrackerReimportPlan(bool NotFound, Guid? Pending, Guid? Supersedes);

/// <summary>
/// FR-173: re-imports form a chain: each replaces one import, and each import has at most one replacement. Retrying any row of a chain acts on its
/// tail, so a duplicate retry adds nothing, and completing the tail supersedes every earlier row. Pure.
/// </summary>
public static class TrackerImportChain
{
    private static bool IsPending(TrackerImportStatus status) => status is TrackerImportStatus.Queued or TrackerImportStatus.Running;

    public static TrackerReimportPlan PlanReimport(IReadOnlyList<TrackerImportLink> rows, Guid importId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var target = rows.FirstOrDefault(r => r.Id == importId);
        if (target is null or { Status: TrackerImportStatus.Superseded or TrackerImportStatus.Removed })
        {
            return new TrackerReimportPlan(true, null, null);
        }

        var descendants = Descendants(rows, importId);
        var tail = descendants.Count > 0 ? descendants[^1] : target;
        return IsPending(tail.Status)
            ? new TrackerReimportPlan(false, tail.Id, null)
            : new TrackerReimportPlan(false, null, tail.Id);
    }

    /// <summary>The rows this one replaces, nearest first.</summary>
    public static IReadOnlyList<TrackerImportLink> Ancestors(IReadOnlyList<TrackerImportLink> rows, Guid importId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var byId = rows.ToDictionary(r => r.Id);
        var seen = new HashSet<Guid> { importId };
        var result = new List<TrackerImportLink>();
        var next = byId.GetValueOrDefault(importId)?.SupersedesId;
        while (next is { } id && seen.Add(id) && byId.TryGetValue(id, out var row))
        {
            result.Add(row);
            next = row.SupersedesId;
        }

        return result;
    }

    /// <summary>The rows replacing this one, nearest first.</summary>
    public static IReadOnlyList<TrackerImportLink> Descendants(IReadOnlyList<TrackerImportLink> rows, Guid importId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var seen = new HashSet<Guid> { importId };
        var result = new List<TrackerImportLink>();
        var current = importId;
        while (rows.FirstOrDefault(r => r.SupersedesId == current) is { } child && seen.Add(child.Id))
        {
            result.Add(child);
            current = child.Id;
        }

        return result;
    }

    /// <summary>
    /// FR-173: removing an import also drops what only exists because of it: replacements not completed, and failed rows it was retrying. A completed row it
    /// was replacing stays active.
    /// </summary>
    public static IReadOnlyList<Guid> AlsoRemoved(IReadOnlyList<TrackerImportLink> rows, Guid importId) =>
    [
        .. Descendants(rows, importId).Where(r => IsPending(r.Status) || r.Status == TrackerImportStatus.Failed).Select(r => r.Id),
        .. Ancestors(rows, importId).Where(r => r.Status == TrackerImportStatus.Failed).Select(r => r.Id),
    ];
}

/// <summary>FR-173 <b>Drift:</b> comparing a snapshot with the tracker. A check never changes a snapshot. Pure.</summary>
public static class SourceItemDriftRules
{
    /// <summary>FR-173: results are cached for 5 minutes.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>§8: a check is capped at 10 seconds, with <c>unknown</c> for what remains.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    public static bool IsCacheFresh(DateTimeOffset? checkedAt, DateTimeOffset now) => checkedAt is { } at && now - at < CacheLifetime;

    /// <summary>
    /// Per item: <paramref name="current"/> <c>null</c> (no connection, or the tracker couldn't answer) is <c>unknown</c>; an item the tracker no
    /// longer returns is <c>removed</c>; another <c>updatedAt</c> is <c>changed</c>; otherwise <c>unchanged</c>.
    /// </summary>
    public static IReadOnlyList<SourceItemDriftResult> Compare(IReadOnlyList<SourceItem> snapshot, IReadOnlyList<SourceItem>? current)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (current is null)
        {
            return [.. snapshot.Select(i => new SourceItemDriftResult(i.Key, SourceItemDrift.Unknown))];
        }

        var byKey = current.GroupBy(i => i.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return
        [
            .. snapshot.Select(i => new SourceItemDriftResult(
                i.Key,
                !byKey.TryGetValue(i.Key, out var now) ? SourceItemDrift.Removed
                : now.UpdatedAt != i.UpdatedAt ? SourceItemDrift.Changed
                : SourceItemDrift.Unchanged)),
        ];
    }

    /// <summary>
    /// FR-173: whether the latest draft was built from an older snapshot, for "Sources changed since this draft" and the
    /// approval confirmation. An agent's draft compares the snapshot version its run listed; a person's edit (no run), when it was made.
    /// </summary>
    public static bool IsDraftBehind(int? currentSnapshotVersion, int? builtFromSnapshotVersion, bool authoredByAgent, DateTimeOffset draftCreatedAt, DateTimeOffset? snapshotCreatedAt)
    {
        if (currentSnapshotVersion is not { } current)
        {
            return false;
        }

        return authoredByAgent ? builtFromSnapshotVersion != current : snapshotCreatedAt > draftCreatedAt;
    }
}
