using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// FR-295: the data an agent stage reads and writes, as a small seam over <see cref="IEngineDbContext"/>, so the runner's
/// rules are tested without a database. Reads and changes are scoped to the run's subject (<see cref="AgentStageScope"/>):
/// artifacts of other subjects (e.g. another release version) are never read or changed. Added entities are written by
/// <see cref="SaveChangesAsync"/>.
/// </summary>
public interface IAgentStageStore
{
    Task<PipelineRun?> FindRunAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>The highest locked version of each of <paramref name="keys"/> in the run's subject (a key without one is
    /// left out). The caller checks integrity.</summary>
    Task<IReadOnlyList<Artifact>> LatestLockedAsync(PipelineRun run, IReadOnlyCollection<string> keys, CancellationToken cancellationToken);

    /// <summary>Every version number and status of artifact <paramref name="key"/> in the project (numbers are project-wide,
    /// FR-58).</summary>
    Task<IReadOnlyList<ArtifactVersion>> VersionsAsync(Guid projectId, string key, CancellationToken cancellationToken);

    /// <summary>The run subject's versions of <paramref name="key"/> among <paramref name="versions"/>, tracked (to supersede).</summary>
    Task<IReadOnlyList<Artifact>> SubjectVersionsAsync(PipelineRun run, string key, IReadOnlyCollection<int> versions, CancellationToken cancellationToken);

    /// <summary>The pending gate of <paramref name="type"/> on <paramref name="run"/>, or <c>null</c>.</summary>
    Task<ApprovalGate?> PendingGateAsync(PipelineRun run, string type, CancellationToken cancellationToken);

    /// <summary>FR-310: the agent run with <paramref name="agentRunId"/>, tracked, or <c>null</c> (the runner refuses one of another pipeline run or stage).</summary>
    Task<AgentRun?> FindAgentRunAsync(Guid agentRunId, CancellationToken cancellationToken);

    /// <summary>FR-310: the run subject's version of <paramref name="key"/> that agent run <paramref name="agentRunId"/> produced, or <c>null</c>.</summary>
    Task<Artifact?> ArtifactOfAgentRunAsync(PipelineRun run, string key, Guid agentRunId, CancellationToken cancellationToken);

    /// <summary>
    /// FR-345, FR-346: the newest version of each variant of <paramref name="key"/> this run stored (superseded ones left
    /// out), by variant label. Fan-out branches write these; isolation (§4.65) and aggregation read them.
    /// </summary>
    Task<IReadOnlyList<Artifact>> RunVariantsAsync(PipelineRun run, string key, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Artifact>>([]);

    /// <summary>FR-346: the newest version (no variant, not superseded) of each of <paramref name="keys"/> this run stored.</summary>
    Task<IReadOnlyList<Artifact>> RunLatestAsync(PipelineRun run, IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Artifact>>([]);

    void Add(AgentRun agentRun);

    void Add(Artifact artifact);

    void Add(ApprovalGate gate);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// FR-295: what "the run's subject" means for artifacts. A run with a subject (the SDLC's release version) owns the
/// project's artifacts of that release version; a run without one owns the artifacts it created.
/// </summary>
public static class AgentStageScope
{
    /// <summary>The release version id the run's artifacts and gates are stored under (FR-334: <c>null</c> without a
    /// subject, so the rows belong to the run alone).</summary>
    public static Guid? ReleaseVersionOf(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.SubjectId;
    }

    /// <summary>Whether an artifact belongs to <paramref name="run"/>'s subject.</summary>
    public static Expression<Func<Artifact, bool>> InSubject(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var projectId = run.ProjectId;
        var runId = run.Id;
        if (run.SubjectId is { } subject)
        {
            return a => a.ProjectId == projectId && a.ReleaseVersionId == subject;
        }

        return a => a.ProjectId == projectId && a.PipelineRunId == runId;
    }
}

/// <summary>The <see cref="IAgentStageStore"/> on the host's <see cref="IEngineDbContext"/> (scoped).</summary>
public sealed class EngineAgentStageStore(IEngineDbContext db) : IAgentStageStore
{
    public Task<PipelineRun?> FindRunAsync(Guid runId, CancellationToken cancellationToken) =>
        db.PipelineRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

    public async Task<IReadOnlyList<Artifact>> LatestLockedAsync(PipelineRun run, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return [];
        }

        // FR-352, §4.76: a project-scope artifact is read at the version the run pinned at its start, never a newer one.
        var pinned = await Runs.ProjectArtifacts.PinnedAsync(db, run, keys, cancellationToken);
        var own = keys.Where(k => !run.PinnedArtifacts.ContainsKey(k)).ToList();
        if (own.Count == 0)
        {
            return pinned;
        }

        var latest = await db.Artifacts.AsNoTracking()
            .Where(AgentStageScope.InSubject(run))
            .Where(a => own.Contains(a.Type) && a.Status == ArtifactStatus.Locked && a.Variant == null)
            .GroupBy(a => a.Type)
            .Select(g => g.OrderByDescending(a => a.Version).First())
            .ToListAsync(cancellationToken);
        return [.. latest, .. pinned];
    }

    public async Task<IReadOnlyList<ArtifactVersion>> VersionsAsync(Guid projectId, string key, CancellationToken cancellationToken) =>
        await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == key)
            .Select(a => new ArtifactVersion(a.Version, a.Status))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Artifact>> SubjectVersionsAsync(PipelineRun run, string key, IReadOnlyCollection<int> versions, CancellationToken cancellationToken) =>
        versions.Count == 0
            ? []
            : await db.Artifacts.Where(AgentStageScope.InSubject(run)).Where(a => a.Type == key && a.Variant == null && versions.Contains(a.Version)).ToListAsync(cancellationToken);

    public Task<ApprovalGate?> PendingGateAsync(PipelineRun run, string type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        return db.ApprovalGates.FirstOrDefaultAsync(g => g.PipelineRunId == run.Id && g.Type == type && g.Status == GateStatus.Pending, cancellationToken);
    }

    public Task<AgentRun?> FindAgentRunAsync(Guid agentRunId, CancellationToken cancellationToken) =>
        db.AgentRuns.FirstOrDefaultAsync(r => r.Id == agentRunId, cancellationToken);

    public Task<Artifact?> ArtifactOfAgentRunAsync(PipelineRun run, string key, Guid agentRunId, CancellationToken cancellationToken) =>
        db.Artifacts.Where(AgentStageScope.InSubject(run)).FirstOrDefaultAsync(a => a.Type == key && a.AgentRunId == agentRunId, cancellationToken);

    public async Task<IReadOnlyList<Artifact>> RunVariantsAsync(PipelineRun run, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var runId = run.Id;
        var rows = await db.Artifacts.AsNoTracking()
            .Where(a => a.PipelineRunId == runId && a.Type == key && a.Variant != null && a.Status != ArtifactStatus.Superseded)
            .ToListAsync(cancellationToken);
        return [.. rows.GroupBy(a => a.Variant!, StringComparer.Ordinal).Select(g => g.MaxBy(a => a.Version)!).OrderBy(a => a.CreatedAt).ThenBy(a => a.Variant, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<Artifact>> RunLatestAsync(PipelineRun run, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(keys);
        var runId = run.Id;
        var rows = await db.Artifacts.AsNoTracking()
            .Where(a => a.PipelineRunId == runId && keys.Contains(a.Type) && a.Variant == null && a.Status != ArtifactStatus.Superseded)
            .ToListAsync(cancellationToken);
        return [.. rows.GroupBy(a => a.Type, StringComparer.Ordinal).Select(g => g.MaxBy(a => a.Version)!)];
    }

    public void Add(AgentRun agentRun) => db.AgentRuns.Add(agentRun);

    public void Add(Artifact artifact) => db.Artifacts.Add(artifact);

    public void Add(ApprovalGate gate) => db.ApprovalGates.Add(gate);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
