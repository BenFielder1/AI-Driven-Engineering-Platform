using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>
/// FR-352, §4.76: source-of-truth (<c>scope: project</c>) artifacts. People add and lock versions through the artifact
/// endpoints (one history per project, no run and no release version); a run pins the newest locked version of each at
/// its start, and every stage of the run reads the pinned version (<see cref="PinnedAsync"/>).
/// </summary>
public sealed class ProjectArtifacts(IEngineDbContext db, PipelineRegistry registry, ArtifactSchemas schemas, TimeProvider time)
{
    /// <summary>
    /// The pins of a new run of <paramref name="definition"/> in <paramref name="projectId"/>
    /// (<see cref="ProjectArtifactRules.Pins"/>): the newest locked version of each project-scope artifact, or
    /// <c>SOURCE_ARTIFACT_MISSING</c>.
    /// </summary>
    public static async Task<Result<IReadOnlyDictionary<string, PinnedArtifact>>> PinsAsync(
        IEngineDbContext db, Guid projectId, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var keys = ProjectArtifactRules.ProjectScoped(definition).Select(a => a.Key).ToList();
        if (keys.Count == 0)
        {
            return Result.Success(PipelineRunRules.NoPins);
        }

        var versions = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Scope == ArtifactScope.Project && keys.Contains(a.Type) && a.Status == ArtifactStatus.Locked)
            .Select(a => new ProjectArtifactVersion(a.Type, a.Id, a.Version, a.Status))
            .ToListAsync(cancellationToken);
        return ProjectArtifactRules.Pins(definition, versions);
    }

    /// <summary>The versions <paramref name="run"/> pinned of those of <paramref name="keys"/> it pinned (FR-352).</summary>
    public static async Task<IReadOnlyList<Artifact>> PinnedAsync(
        IEngineDbContext db, PipelineRun run, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(keys);
        var ids = run.PinnedArtifacts.Where(p => keys.Contains(p.Key)).Select(p => p.Value.ArtifactId).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var projectId = run.ProjectId;
        return await db.Artifacts.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.ProjectId == projectId && a.Scope == ArtifactScope.Project)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Every version of project-scope artifact <paramref name="key"/> of <paramref name="pipelineId"/>, newest first.</summary>
    public async Task<Result<(ArtifactDefinition Artifact, IReadOnlyList<Artifact> Versions)>> ListAsync(
        Guid projectId, string pipelineId, string key, CancellationToken cancellationToken)
    {
        var found = Find(pipelineId, key);
        if (found.IsFailure)
        {
            return Result.Failure<(ArtifactDefinition, IReadOnlyList<Artifact>)>(found.Error!);
        }

        var versions = await Versions(projectId, key).AsNoTracking().OrderByDescending(a => a.Version).ToListAsync(cancellationToken);
        return Result.Success<(ArtifactDefinition, IReadOnlyList<Artifact>)>((found.Value.Artifact, versions));
    }

    /// <summary>
    /// Adds the next version of <paramref name="key"/> with <paramref name="value"/> (checked against the artifact's
    /// schema: <c>INPUT_INVALID</c>), parented on the newest. A draft, unless the artifact is <c>approval: auto_lock</c>.
    /// </summary>
    public async Task<Result<Artifact>> CreateAsync(
        Guid projectId, string pipelineId, string key, JsonElement value, Guid? userId, CancellationToken cancellationToken)
    {
        var found = Find(pipelineId, key);
        if (found.IsFailure)
        {
            return Result.Failure<Artifact>(found.Error!);
        }

        var (artifact, packId) = found.Value;
        var content = RunInputs.Content(artifact, value, packId, schemas);
        if (content.IsFailure)
        {
            return Result.Failure<Artifact>(content.Error!);
        }

        // Version numbers are taken under a transaction-scoped advisory lock per (project, artifact key); the unique index
        // on (project_id, type, version) for project scope backs it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = RunStartLock.Key(projectId, "artifact:" + key);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);

        var rows = await Versions(projectId, key).ToListAsync(cancellationToken);
        var stored = rows.Select(a => a.Version).ToList();

        // §4.10: the new version supersedes earlier open drafts of the same (project, key).
        var superseded = ProjectArtifactRules.SupersededBy(rows.Select(a => (a.Version, a.Status))).ToHashSet();
        foreach (var draft in rows.Where(a => superseded.Contains(a.Version)))
        {
            draft.Status = ArtifactStatus.Superseded;
        }

        var now = time.GetUtcNow();
        var autoLock = artifact.Approval == ArtifactApproval.AutoLock;
        var version = new Artifact
        {
            ProjectId = projectId,
            Type = key,
            Version = ProjectArtifactRules.NextVersion(stored),
            ParentVersion = stored.Count == 0 ? null : stored.Max(),
            Content = content.Value,
            ContentHash = ContentHash.Compute(content.Value),
            Status = autoLock ? ArtifactStatus.Locked : ArtifactStatus.Draft,
            CheckpointAtCreation = 0,
            ReleaseVersionId = null,
            PipelineRunId = null,
            Scope = ArtifactScope.Project,
            CreatedByUserId = userId,
            ApprovedBy = autoLock ? userId : null,
            ApprovedAt = autoLock ? now : null,
            CreatedAt = now,
        };
        db.Artifacts.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(version);
    }

    /// <summary>
    /// Locks draft <paramref name="version"/> of <paramref name="key"/> (<see cref="ProjectArtifactRules.Lock"/>: locking a
    /// locked version is a no-op; otherwise <c>ARTIFACT_NOT_DRAFT</c>; a draft older than the newest locked version is
    /// <c>CONFLICT</c>). Runs started from now on pin it; running runs keep their pins (§4.76).
    /// </summary>
    public async Task<Result<Artifact>> LockAsync(
        Guid projectId, string pipelineId, string key, int version, Guid? userId, CancellationToken cancellationToken)
    {
        var found = Find(pipelineId, key);
        if (found.IsFailure)
        {
            return Result.Failure<Artifact>(found.Error!);
        }

        var row = await Versions(projectId, key).FirstOrDefaultAsync(a => a.Version == version, cancellationToken);
        if (row is null)
        {
            return Result.Failure<Artifact>(new DomainError(EngineErrorCodes.NotFound, $"Version {version} of '{key}' was not found."));
        }

        var newestLocked = await Versions(projectId, key).Where(a => a.Status == ArtifactStatus.Locked).MaxAsync(a => (int?)a.Version, cancellationToken);
        var change = ProjectArtifactRules.Lock(row.Status, row.Version, newestLocked);
        if (change.IsFailure)
        {
            return Result.Failure<Artifact>(change.Error!);
        }

        if (change.Value.Changed)
        {
            row.Status = change.Value.NewStatus;
            row.ApprovedBy = userId;
            row.ApprovedAt = time.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(row);
    }

    private IQueryable<Artifact> Versions(Guid projectId, string key) =>
        db.Artifacts.Where(a => a.ProjectId == projectId && a.Scope == ArtifactScope.Project && a.Type == key);

    /// <summary>The project-scope artifact <paramref name="key"/> of a registered pipeline, and its pack; else <c>NOT_FOUND</c>.</summary>
    public Result<(ArtifactDefinition Artifact, string PackId)> Find(string pipelineId, string key)
    {
        if (registry.TryGet(pipelineId, out var loaded) && loaded.Definition is { } definition
            && definition.FindArtifact(key) is { Scope: ArtifactScope.Project } artifact)
        {
            return Result.Success((artifact, loaded.PackId));
        }

        return Result.Failure<(ArtifactDefinition, string)>(
            new DomainError(EngineErrorCodes.NotFound, $"Pipeline '{pipelineId}' has no project-wide artifact '{key}'."));
    }
}
