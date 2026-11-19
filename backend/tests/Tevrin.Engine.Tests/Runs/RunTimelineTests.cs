using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Tests.Runs;

/// <summary>FR-300, FR-303: timeline merge order, keyset paging over windowed sources, and the cursor.</summary>
public class RunTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Reads a source as the reader does: rows at the cursor's exact time plus the first take after it.</summary>
    private static IEnumerable<TimelineKey> Window(IReadOnlyList<TimelineKey> source, TimelineKey? cursor, int take) =>
        cursor is { } c
            ? source.Where(k => k.At == c.At)
                .Concat(source.Where(k => k.At > c.At).OrderBy(k => k.At).ThenBy(k => k.Id.ToString("D"), StringComparer.Ordinal).Take(take))
            : source.OrderBy(k => k.At).ThenBy(k => k.Id.ToString("D"), StringComparer.Ordinal).Take(take);

    /// <summary>120 entries over five kinds, with shared timestamps inside and across kinds (one save writes several).</summary>
    private static List<List<TimelineKey>> Sources()
    {
        PipelineTimelineKind[] kinds =
        [
            PipelineTimelineKind.Transition, PipelineTimelineKind.StageEntered, PipelineTimelineKind.AgentRun,
            PipelineTimelineKind.GateCreated, PipelineTimelineKind.ArtifactVersion,
        ];
        var sources = kinds.Select(_ => new List<TimelineKey>()).ToList();
        for (var i = 0; i < 120; i++)
        {
            var kind = i % kinds.Length;
            sources[kind].Add(new TimelineKey(T0.AddSeconds(i / 7), kinds[kind], Guid.CreateVersion7()));
        }

        return sources;
    }

    [Fact]
    public void FR300_following_the_cursor_returns_all_120_entries_once_in_order()
    {
        var sources = Sources();
        var all = sources.SelectMany(s => s).Order().ToList();
        var seen = new List<TimelineKey>();
        TimelineKey? cursor = null;
        var pages = 0;
        do
        {
            var candidates = sources.SelectMany(s => Window(s, cursor, 51));
            var page = RunTimeline.Page(candidates, k => k, cursor, 50);
            seen.AddRange(page.Items);
            pages++;
            cursor = page.HasMore ? RunTimeline.TryDecode(RunTimeline.Encode(page.Items[^1])) : null;
            Assert.True(pages < 10);
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(all, seen);
    }

    [Fact]
    public void FR300_entries_at_the_same_time_order_by_kind_rank_then_id()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var lower = Guid.Parse("00000000-0000-0000-0000-000000000001");
        TimelineKey[] keys =
        [
            new(T0, PipelineTimelineKind.GateCreated, lower),
            new(T0, PipelineTimelineKind.AgentRun, lower),
            new(T0, PipelineTimelineKind.Transition, id),
            new(T0, PipelineTimelineKind.Transition, lower),
            new(T0, PipelineTimelineKind.GateDecided, id),
            new(T0.AddTicks(-10), PipelineTimelineKind.ArtifactVersion, id),
        ];

        var page = RunTimeline.Page(keys, k => k, null, 10);

        Assert.Equal(
            [PipelineTimelineKind.ArtifactVersion, PipelineTimelineKind.GateDecided, PipelineTimelineKind.Transition,
                PipelineTimelineKind.Transition, PipelineTimelineKind.AgentRun, PipelineTimelineKind.GateCreated],
            page.Items.Select(k => k.Kind));
        Assert.Equal(lower, page.Items[2].Id);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void FR300_ids_order_as_postgresql_uuids()
    {
        // .NET's Guid.CompareTo orders the first fields little-endian; PostgreSQL compares the bytes as written.
        var a = Guid.Parse("01000000-0000-0000-0000-000000000000");
        var b = Guid.Parse("00ffffff-0000-0000-0000-000000000001");
        Assert.True(new TimelineKey(T0, PipelineTimelineKind.AgentRun, b) < new TimelineKey(T0, PipelineTimelineKind.AgentRun, a));
    }

    [Fact]
    public void FR300_the_cursor_round_trips_and_garbage_is_refused()
    {
        var key = new TimelineKey(T0.AddTicks(12_340), PipelineTimelineKind.GateDecided, Guid.CreateVersion7());
        Assert.Equal(key, RunTimeline.TryDecode(RunTimeline.Encode(key)));
        Assert.Null(RunTimeline.TryDecode("not a cursor"));
        Assert.Null(RunTimeline.TryDecode("MTIzLjk5LjAw"));
        Assert.Null(RunTimeline.TryDecode(new string('a', 200)));
    }
}
