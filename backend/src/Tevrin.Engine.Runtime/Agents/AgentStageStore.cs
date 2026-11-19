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
    /// <summary>The release version id the run's artifacts and gates are stored under (<see cref="Guid.Empty"/> without a
    /// subject).</summary>
    public static Guid ReleaseVersionOf(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.SubjectId ?? Guid.Empty;
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

        return await db.Artifacts.AsNoTracking()
            .Where(AgentStageScope.InSubject(run))
            .Where(a => keys.Contains(a.Type) && a.Status == ArtifactStatus.Locked)
            .GroupBy(a => a.Type)
            .Select(g => g.OrderByDescending(a => a.Version).First())
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArtifactVersion>> VersionsAsync(Guid projectId, string key, CancellationToken cancellationToken) =>
        await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == key)
            .Select(a => new ArtifactVersion(a.Version, a.Status))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Artifact>> SubjectVersionsAsync(PipelineRun run, string key, IReadOnlyCollection<int> versions, CancellationToken cancellationToken) =>
        versions.Count == 0
            ? []
            : await db.Artifacts.Where(AgentStageScope.InSubject(run)).Where(a => a.Type == key && versions.Contains(a.Version)).ToListAsync(cancellationToken);

    public Task<ApprovalGate?> PendingGateAsync(PipelineRun run, string type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        return db.ApprovalGates.FirstOrDefaultAsync(g => g.PipelineRunId == run.Id && g.Type == type && g.Status == GateStatus.Pending, cancellationToken);
    }

    public void Add(AgentRun agentRun) => db.AgentRuns.Add(agentRun);

    public void Add(Artifact artifact) => db.Artifacts.Add(artifact);

    public void Add(ApprovalGate gate) => db.ApprovalGates.Add(gate);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
