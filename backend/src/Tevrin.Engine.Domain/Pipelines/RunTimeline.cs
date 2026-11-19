using System.Globalization;
using System.Text;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>FR-300: the kinds of a run's timeline entries.</summary>
public enum PipelineTimelineKind
{
    Transition = 0,
    StageEntered = 1,
    AgentRun = 2,
    GateCreated = 3,
    GateDecided = 4,
    ArtifactVersion = 5,
}

/// <summary>
/// FR-300: the total order of timeline entries: time, then a fixed rank of the kind (for entries written in one save, which
/// share a timestamp: a gate decision, then the transition it caused, the stage it entered, artifacts, agent runs and new
/// gates), then the source row id in PostgreSQL <c>uuid</c> order (the ordinal order of its text form).
/// </summary>
public readonly record struct TimelineKey(DateTimeOffset At, PipelineTimelineKind Kind, Guid Id) : IComparable<TimelineKey>
{
    public int CompareTo(TimelineKey other)
    {
        var byTime = At.UtcTicks.CompareTo(other.At.UtcTicks);
        if (byTime != 0)
        {
            return byTime;
        }

        var byKind = RunTimeline.Rank(Kind).CompareTo(RunTimeline.Rank(other.Kind));
        return byKind != 0 ? byKind : string.CompareOrdinal(Id.ToString("D"), other.Id.ToString("D"));
    }

    public static bool operator <(TimelineKey left, TimelineKey right) => left.CompareTo(right) < 0;

    public static bool operator >(TimelineKey left, TimelineKey right) => left.CompareTo(right) > 0;

    public static bool operator <=(TimelineKey left, TimelineKey right) => left.CompareTo(right) <= 0;

    public static bool operator >=(TimelineKey left, TimelineKey right) => left.CompareTo(right) >= 0;
}

/// <summary>A page of timeline entries and whether more follow.</summary>
public sealed record TimelinePage<T>(IReadOnlyList<T> Items, bool HasMore);

/// <summary>
/// FR-300, FR-303: merging and keyset-paging a run's timeline. Each source is read as "every row at the cursor's exact
/// time, plus the first <c>limit + 1</c> rows after it" (time, id order); <see cref="Page{T}"/> merges the candidates,
/// drops those at or before the cursor and takes <c>limit</c>. Every source's omitted rows sort after its returned
/// ones, so the page is exact, and there are more entries exactly when more than <c>limit</c> candidates remain.
/// </summary>
public static class RunTimeline
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>The tie-break rank of <paramref name="kind"/> (see <see cref="TimelineKey"/>).</summary>
    public static int Rank(PipelineTimelineKind kind) => kind switch
    {
        PipelineTimelineKind.GateDecided => 0,
        PipelineTimelineKind.Transition => 1,
        PipelineTimelineKind.StageEntered => 2,
        PipelineTimelineKind.ArtifactVersion => 3,
        PipelineTimelineKind.AgentRun => 4,
        PipelineTimelineKind.GateCreated => 5,
        _ => 6,
    };

    /// <summary>The page after <paramref name="after"/> (from the start when <c>null</c>).</summary>
    public static TimelinePage<T> Page<T>(IEnumerable<T> candidates, Func<T, TimelineKey> key, TimelineKey? after, int limit)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var ordered = candidates
            .Select(c => (Key: key(c), Item: c))
            .Where(c => after is not { } cursor || c.Key > cursor)
            .DistinctBy(c => c.Key)
            .OrderBy(c => c.Key)
            .ToList();
        return new TimelinePage<T>([.. ordered.Take(limit).Select(c => c.Item)], ordered.Count > limit);
    }

    /// <summary>The opaque cursor for <paramref name="key"/>: base64url of "&lt;UTC ticks&gt;.&lt;kind&gt;.&lt;id&gt;".</summary>
    public static string Encode(TimelineKey key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{key.At.UtcTicks.ToString(CultureInfo.InvariantCulture)}.{((int)key.Kind).ToString(CultureInfo.InvariantCulture)}.{key.Id:N}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The key of a cursor from <see cref="Encode"/>, or <c>null</c> when it isn't one.</summary>
    public static TimelineKey? TryDecode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 128)
        {
            return null;
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split('.');
            return parts.Length == 3
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kind)
                && Enum.IsDefined((PipelineTimelineKind)kind)
                && Guid.TryParseExact(parts[2], "N", out var id)
                    ? new TimelineKey(new DateTimeOffset(ticks, TimeSpan.Zero), (PipelineTimelineKind)kind, id)
                    : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
