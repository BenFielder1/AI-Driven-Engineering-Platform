using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Api;

/// <summary>
/// FR-300, FR-303: a run's timeline, merged from transitions, stage executions, agent runs, gate events and artifact
/// versions and keyset-paged (<see cref="RunTimeline"/>). Each source reads by run (indexed on <c>pipeline_run_id</c>;
/// §8) at most the cursor's exact time plus <c>limit + 1</c> rows. Only the replay loads agent input and output.
/// </summary>
public sealed class PipelineTimelineReader(IEngineDbContext db, IPipelineTransitionSource transitions)
{
    private static readonly Expression<Func<AgentRun, AgentRun>> Summary = r => new AgentRun
    {
        Id = r.Id,
        CreatedAt = r.CreatedAt,
        Stage = r.Stage,
        Agent = r.Agent,
        AgentKey = r.AgentKey,
        Status = r.Status,
        Model = r.Model,
        PromptVersion = r.PromptVersion,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        CostEstimate = r.CostEstimate,
        DurationMs = r.DurationMs,
        LatencyMs = r.LatencyMs,
        Error = r.Error,
        ItemIndex = r.ItemIndex,
        TurnIndex = r.TurnIndex,
        TurnRole = r.TurnRole,
    };

    private static readonly Expression<Func<AgentRun, AgentRun>> WithInputAndOutput = r => new AgentRun
    {
        Id = r.Id,
        CreatedAt = r.CreatedAt,
        Stage = r.Stage,
        Agent = r.Agent,
        AgentKey = r.AgentKey,
        Status = r.Status,
        Model = r.Model,
        PromptVersion = r.PromptVersion,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        CostEstimate = r.CostEstimate,
        DurationMs = r.DurationMs,
        LatencyMs = r.LatencyMs,
        Error = r.Error,
        Input = r.Input,
        InputTruncated = r.InputTruncated,
        Output = r.Output,
        ItemIndex = r.ItemIndex,
        TurnIndex = r.TurnIndex,
        TurnRole = r.TurnRole,
    };

    /// <summary>The page after <paramref name="cursor"/>; <paramref name="replay"/> adds agent input and output.</summary>
    public async Task<PipelineTimelinePageDto> PageAsync(Guid runId, TimelineKey? cursor, int limit, bool replay, CancellationToken cancellationToken)
    {
        var take = limit + 1;
        var items = new List<PipelineTimelineItemDto>();

        var moves = cursor is { } c
            ? [.. await transitions.ListAsync(runId, new TimelineWindow(c.At, null, null), cancellationToken),
                .. await transitions.ListAsync(runId, new TimelineWindow(null, c.At, take), cancellationToken)]
            : await transitions.ListAsync(runId, new TimelineWindow(null, null, take), cancellationToken);
        items.AddRange(moves.Select(TimelineItems.Transition));

        var stages = await WindowAsync(db.StageExecutions.AsNoTracking().Where(e => e.PipelineRunId == runId), e => e.EnteredAt, e => e.Id, cursor, take, cancellationToken);
        items.AddRange(stages.Select(TimelineItems.Stage));

        var runs = await WindowAsync(
            db.AgentRuns.AsNoTracking().Where(r => r.PipelineRunId == runId).Select(replay ? WithInputAndOutput : Summary),
            r => r.CreatedAt, r => r.Id, cursor, take, cancellationToken);
        items.AddRange(runs.Select(r => TimelineItems.AgentRun(r, replay)));

        var gates = db.ApprovalGates.AsNoTracking().Where(g => g.PipelineRunId == runId);
        var created = await WindowAsync(gates, g => g.CreatedAt, g => g.Id, cursor, take, cancellationToken);
        items.AddRange(created.Select(g => TimelineItems.Gate(g, decided: false)));
        var decided = await WindowAsync(
            gates.Where(g => g.DecidedAt != null && g.Status != GateStatus.Pending), g => g.DecidedAt!.Value, g => g.Id, cursor, take, cancellationToken);
        items.AddRange(decided.Select(g => TimelineItems.Gate(g, decided: true)));

        var artifacts = await WindowAsync(
            db.Artifacts.AsNoTracking().Where(a => a.PipelineRunId == runId)
                .Select(a => new ArtifactRow { Id = a.Id, Type = a.Type, Version = a.Version, Status = a.Status, ContentHash = a.ContentHash, AgentRunId = a.AgentRunId, CreatedAt = a.CreatedAt, Variant = a.Variant, ItemIndex = a.ItemIndex }),
            a => a.CreatedAt, a => a.Id, cursor, take, cancellationToken);
        items.AddRange(artifacts.Select(TimelineItems.Artifact));

        var page = RunTimeline.Page(items, TimelineItems.KeyOf, cursor, limit);
        return new PipelineTimelinePageDto(page.Items, page.HasMore ? RunTimeline.Encode(TimelineItems.KeyOf(page.Items[^1])) : null);
    }

    private static async Task<List<T>> WindowAsync<T>(
        IQueryable<T> source,
        Expression<Func<T, DateTimeOffset>> at,
        Expression<Func<T, Guid>> id,
        TimelineKey? cursor,
        int take,
        CancellationToken cancellationToken)
    {
        if (cursor is not { } c)
        {
            return await source.OrderBy(at).ThenBy(id).Take(take).ToListAsync(cancellationToken);
        }

        // A captured value (not a constant), so EF sends it as a parameter.
        var value = Expression.Property(Expression.Constant(new TimeBox(c.At)), nameof(TimeBox.Value));
        var equal = Expression.Lambda<Func<T, bool>>(Expression.Equal(at.Body, value), at.Parameters);
        var after = Expression.Lambda<Func<T, bool>>(Expression.GreaterThan(at.Body, value), at.Parameters);
        var ties = await source.Where(equal).ToListAsync(cancellationToken);
        var next = await source.Where(after).OrderBy(at).ThenBy(id).Take(take).ToListAsync(cancellationToken);
        return [.. ties, .. next];
    }

    private sealed record TimeBox(DateTimeOffset Value);
}

/// <summary>The artifact columns the timeline reads (never the content).</summary>
internal sealed class ArtifactRow
{
    public Guid Id { get; init; }

    public string Type { get; init; } = string.Empty;

    public int Version { get; init; }

    public Domain.Artifacts.ArtifactStatus Status { get; init; }

    public string ContentHash { get; init; } = string.Empty;

    public Guid? AgentRunId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string? Variant { get; init; }

    public int? ItemIndex { get; init; }
}
