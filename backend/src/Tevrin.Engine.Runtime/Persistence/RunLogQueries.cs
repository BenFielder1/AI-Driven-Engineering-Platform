using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Runtime.Persistence;

/// <summary>
/// FR-297, FR-298: read-only queries over the run log. Aggregation runs in the database; the averages come from
/// <see cref="RunLogStats"/>. Which projects a user may see is the caller's filter (pass the visible project ids).
/// </summary>
public sealed class RunLogQueries(IEngineDbContext db)
{
    /// <summary>FR-297: the cost (sum of <c>cost_estimate</c>) and token totals of the agent runs linked to
    /// <paramref name="pipelineRunId"/>; zero when it has none.</summary>
    public async Task<RunTotals> RunTotalsAsync(Guid pipelineRunId, CancellationToken cancellationToken)
    {
        var totals = await db.AgentRuns.AsNoTracking()
            .Where(r => r.PipelineRunId == pipelineRunId)
            .GroupBy(r => 1)
            .Select(g => new
            {
                Runs = g.Count(),
                Cost = g.Sum(r => r.CostEstimate),
                Input = g.Sum(r => (long)r.InputTokens),
                Output = g.Sum(r => (long)r.OutputTokens),
            })
            .FirstOrDefaultAsync(cancellationToken);
        return totals is null ? RunTotals.None : new RunTotals(totals.Runs, totals.Cost, totals.Input, totals.Output);
    }

    /// <summary>
    /// FR-298: the agent runs of <paramref name="pipelineId"/>'s runs in <paramref name="projectIds"/> (the projects the caller
    /// may see; none gives none) grouped by agent key and prompt version, with counts, average tokens, cost, latency and
    /// duration and the failure rate. Sorted by agent key, then prompt version.
    /// </summary>
    public Task<IReadOnlyList<PromptStats>> PromptStatsAsync(
        string pipelineId, IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipelineId);
        ArgumentNullException.ThrowIfNull(projectIds);
        if (projectIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<PromptStats>>([]);
        }

        var ids = projectIds.ToArray();
        return PromptStatsAsync(
            db.PipelineRuns.AsNoTracking().Where(p => p.PipelineId == pipelineId && ids.Contains(p.ProjectId)),
            cancellationToken);
    }

    /// <summary>FR-298: <see cref="PromptStatsAsync(string, IReadOnlyCollection{Guid}, CancellationToken)"/> over every
    /// project. For administrators only: the caller must have checked that.</summary>
    public Task<IReadOnlyList<PromptStats>> PromptStatsForAllProjectsAsync(string pipelineId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipelineId);
        return PromptStatsAsync(db.PipelineRuns.AsNoTracking().Where(p => p.PipelineId == pipelineId), cancellationToken);
    }

    private async Task<IReadOnlyList<PromptStats>> PromptStatsAsync(IQueryable<PipelineRun> runs, CancellationToken cancellationToken)
    {

        var groups = await db.AgentRuns.AsNoTracking()
            .Join(runs, r => r.PipelineRunId, p => (Guid?)p.Id, (r, p) => r)
            .GroupBy(r => new { r.Agent, r.PromptVersion })
            .Select(g => new
            {
                g.Key.Agent,
                g.Key.PromptVersion,
                Runs = g.Count(),
                Finished = g.Count(r => r.Status == AgentRunStatus.Succeeded || r.Status == AgentRunStatus.Failed),
                Failed = g.Count(r => r.Status == AgentRunStatus.Failed),
                Input = g.Sum(r => (long)r.InputTokens),
                Output = g.Sum(r => (long)r.OutputTokens),
                Cost = g.Sum(r => r.CostEstimate),
                LatencyRuns = g.Count(r => r.LatencyMs != null),
                Latency = g.Sum(r => r.LatencyMs ?? 0),
                Duration = g.Sum(r => r.DurationMs),
            })
            .ToListAsync(cancellationToken);
        return RunLogStats.Summarise(groups.Select(g => new PromptRunSums(
            g.Agent, g.PromptVersion, g.Runs, g.Finished, g.Failed, g.Input, g.Output, g.Cost, g.LatencyRuns, g.Latency, g.Duration)));
    }
}
