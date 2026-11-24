using Microsoft.EntityFrameworkCore;
using Tevrin.Api.Jobs;
using Tevrin.Api.Logging;
using Tevrin.Api.Pipelines;
using Tevrin.Domain.Access;
using Tevrin.Domain.Artifacts;
using Tevrin.Domain.Common;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Infrastructure.Persistence;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Api.Features.Artifacts;

/// <summary>
/// FR-20: re-checks the content hash of every artifact read. The first read that finds a mismatch marks the row
/// <c>corrupted</c> and, in the same transaction, writes <c>ARTIFACT_CORRUPTED</c>, the <c>INTEGRITY_ALERT</c> admin
/// alert and — for the current version of its type (open draft or highest locked) when agent-produced — the
/// regeneration job of the producing agent; older versions are only marked and alerted. The status change is a
/// conditional update (<c>status &lt;&gt; 'corrupted'</c>), so concurrent reads raise the alert and the job exactly once.
/// Writes go through their own scope and unit of work, never the caller's.
/// </summary>
public sealed partial class ArtifactIntegrityGuard(IServiceScopeFactory scopes, TimeProvider time, ILogger<ArtifactIntegrityGuard> logger)
{
    /// <summary>
    /// The integrity of each artifact (by id). Corrupted rows are marked (once) and their <see cref="Artifact.Status"/>
    /// is updated in place, so the caller returns <c>corrupted</c>. Pass untracked entities.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, ArtifactIntegrity>> CheckAsync(IEnumerable<Artifact> artifacts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var results = new Dictionary<Guid, ArtifactIntegrity>();
        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var integrity = ArtifactRules.IntegrityOf(artifact.Status, artifact.Content, artifact.ContentHash);
            if (integrity == ArtifactIntegrity.Corrupted && artifact.Status != ArtifactStatus.Corrupted)
            {
                await MarkCorruptedAsync(artifact);
                artifact.Status = ArtifactStatus.Corrupted;
            }

            results[artifact.Id] = integrity;
        }

        return results;
    }

    public async Task<ArtifactIntegrity> CheckAsync(Artifact artifact, CancellationToken cancellationToken) =>
        (await CheckAsync([artifact], cancellationToken))[artifact.Id];

    private async Task MarkCorruptedAsync(Artifact artifact)
    {
        // Not cancellable: once started, the mark, alert and job are written even if the request is aborted.
        var token = CancellationToken.None;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TevrinDbContext>();
        var jobs = scope.ServiceProvider.GetRequiredService<JobEnqueuer>();
        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var marked = await db.Artifacts.Sdlc()
            .Where(a => a.Id == artifact.Id && a.Status != ArtifactStatus.Corrupted)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, ArtifactStatus.Corrupted), token);
        if (marked == 0)
        {
            // Another read marked it first; its alert stands.
            await transaction.RollbackAsync(token);
            return;
        }

        var authoredByAgent = ArtifactRules.IsAuthoredByAgent(artifact.CreatedByUserId);
        var others = await db.Artifacts.Sdlc().AsNoTracking()
            .Where(a => a.ProjectId == artifact.ProjectId && a.Type == artifact.Type && a.Id != artifact.Id)
            .Select(a => new ArtifactVersion(a.Version, a.Status))
            .ToListAsync(token);
        var isCurrent = ArtifactRules.IsCurrentVersion(new ArtifactVersion(artifact.Version, artifact.Status), others);
        var change = SdlcArtifactRules.OnIntegrityCheck(artifact.Type, artifact.Status, artifact.Content, artifact.ContentHash, authoredByAgent, isCurrent);
        var projectVersion = await db.Projects.AsNoTracking()
            .Where(p => p.Id == artifact.ProjectId)
            .Select(p => p.Checkpoint)
            .FirstAsync(token);

        Guid? jobId = null;
        if (change.RerunAgent is { } agent)
        {
            var job = await jobs.EnqueueAsync(
                SdlcJobTypes.ForAgent(agent),
                new LifecycleJobPayload(artifact.ProjectId, projectVersion, LifecycleJobPayload.ArtifactCorrupted, ArtifactId: artifact.Id),
                artifact.ProjectId,
                token,
                now);
            jobId = job.Id;
        }

        var details = new
        {
            artifactId = artifact.Id,
            type = artifact.Type,
            version = artifact.Version,
            previousStatus = artifact.Status,
            expectedHash = artifact.ContentHash,
            actualHash = ContentHash.Compute(artifact.Content),
            rerunAgent = change.RerunAgent,
            jobId,
        };
        db.AddEvent(EventTypes.ArtifactCorrupted, artifact.ProjectId, details, SystemActor.Name, now);
        db.AddEvent(EventTypes.IntegrityAlert, artifact.ProjectId, details, SystemActor.Name, now);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);

        LogCorrupted(logger, artifact.ProjectId, artifact.Id, artifact.Type, artifact.Version, change.RerunAgent is not null);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Project {ProjectId}: artifact {ArtifactId} ({ArtifactType} v{Version}) failed its integrity check and was marked corrupted (regenerate: {Regenerate})")]
    private static partial void LogCorrupted(ILogger logger, Guid projectId, Guid artifactId, LoggedKey artifactType, int version, bool regenerate);
}
