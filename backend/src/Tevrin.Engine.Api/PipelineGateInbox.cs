using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Api;

/// <summary>
/// FR-302: gates across every project the caller can see (filtered in SQL through
/// <see cref="IRunAccessPolicy.VisibleProjectIdsAsync"/>, §8), newest first, keyset-paged on (created, id), filterable by
/// status, pipeline and gate type.
/// </summary>
public sealed class PipelineGateInbox(
    IEngineDbContext db,
    IRunAccessPolicy access,
    PipelineRunReader runs,
    PipelineDefinitionResolver definitions)
{
    public async Task<PipelineGateInboxPageDto> PageAsync(
        GateStatus status, string? pipelineId, string? gateType, TimelineKey? cursor, int limit, CancellationToken cancellationToken)
    {
        var visible = await access.VisibleProjectIdsAsync(cancellationToken);
        var query = db.ApprovalGates.AsNoTracking()
            .Where(g => g.PipelineRunId != null && g.Status == status && visible.Contains(g.ProjectId));
        if (pipelineId is not null)
        {
            query = query.Where(g => db.PipelineRuns.Any(r => r.Id == g.PipelineRunId && r.PipelineId == pipelineId));
        }

        if (gateType is not null)
        {
            query = query.Where(g => g.Type == gateType);
        }

        var candidates = cursor is { } c
            ? [.. await query.Where(g => g.CreatedAt == c.At).ToListAsync(cancellationToken),
                .. await query.Where(g => g.CreatedAt < c.At).OrderByDescending(g => g.CreatedAt).ThenByDescending(g => g.Id).Take(limit + 1).ToListAsync(cancellationToken)]
            : await query.OrderByDescending(g => g.CreatedAt).ThenByDescending(g => g.Id).Take(limit + 1).ToListAsync(cancellationToken);

        // Newest first is the reverse of the timeline order; the cursor is the last item's (created, id).
        var ordered = candidates
            .Select(g => (Key: new TimelineKey(g.CreatedAt, PipelineTimelineKind.GateCreated, g.Id), Gate: g))
            .Where(x => cursor is not { } after || x.Key < after)
            .OrderByDescending(x => x.Key)
            .ToList();
        var page = ordered.Take(limit).ToList();
        var gates = await runs.ToGateDtosAsync([.. page.Select(x => x.Gate)], cancellationToken);

        var runIds = gates.Select(g => g.RunId).Distinct().ToList();
        var pipelines = await db.PipelineRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.PipelineId, r.DefinitionHash })
            .ToListAsync(cancellationToken);
        var titles = new Dictionary<Guid, (string Id, string Title)>();
        foreach (var run in pipelines)
        {
            var title = (await definitions.ResolveAsync(run.DefinitionHash, cancellationToken))?.Definition.Title ?? run.PipelineId;
            titles[run.Id] = (run.PipelineId, title);
        }

        var names = await access.ProjectNamesAsync([.. gates.Select(g => g.ProjectId).Distinct()], cancellationToken);
        var items = gates.Select(g =>
        {
            var pipeline = titles.TryGetValue(g.RunId, out var p) ? p : (string.Empty, string.Empty);
            return new PipelineGateInboxItemDto(g, names.GetValueOrDefault(g.ProjectId) ?? string.Empty, pipeline.Item1, pipeline.Item2);
        }).ToList();
        var next = ordered.Count > limit ? RunTimeline.Encode(page[^1].Key) : null;
        return new PipelineGateInboxPageDto(items, next);
    }
}
