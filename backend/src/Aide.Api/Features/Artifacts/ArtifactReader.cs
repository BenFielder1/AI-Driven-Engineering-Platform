using Aide.Api.Features.Activity;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Artifacts;

/// <summary>
/// FR-16, FR-20, FR-21: artifact reads. Every read goes through <see cref="ArtifactIntegrityGuard"/>, so each returned
/// version carries the integrity found by re-hashing it now.
/// </summary>
public sealed class ArtifactReader(AideDbContext db, ArtifactIntegrityGuard integrity)
{
    /// <summary>The highest version of each type that exists, in type order.</summary>
    public async Task<IReadOnlyList<ArtifactSummaryDto>> ListLatestAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var latest = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId
                && a.Version == db.Artifacts.Where(b => b.ProjectId == projectId && b.Type == a.Type).Max(b => b.Version))
            .ToListAsync(cancellationToken);
        return await SummariesAsync([.. latest.OrderBy(a => a.Type)], cancellationToken);
    }

    /// <summary>Every version of a type, newest first.</summary>
    public async Task<IReadOnlyList<ArtifactSummaryDto>> HistoryAsync(Guid projectId, ArtifactType type, CancellationToken cancellationToken)
    {
        var versions = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == type)
            .OrderByDescending(a => a.Version)
            .ToListAsync(cancellationToken);
        return await SummariesAsync(versions, cancellationToken);
    }

    /// <summary>The highest version of a type without its content, or <c>null</c>.</summary>
    public async Task<ArtifactSummaryDto?> LatestSummaryAsync(Guid projectId, ArtifactType type, CancellationToken cancellationToken)
    {
        var artifact = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == type)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return artifact is null ? null : (await SummariesAsync([artifact], cancellationToken))[0];
    }

    /// <summary>The highest version of a type, or <c>null</c>.</summary>
    public async Task<ArtifactDto?> LatestAsync(Guid projectId, ArtifactType type, CancellationToken cancellationToken)
    {
        var artifact = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == type)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return artifact is null ? null : await ToDtoAsync(artifact, cancellationToken);
    }

    /// <summary>One version of a type, or <c>null</c>.</summary>
    public async Task<ArtifactDto?> VersionAsync(Guid projectId, ArtifactType type, int version, CancellationToken cancellationToken)
    {
        var artifact = await FindAsync(projectId, type, version, cancellationToken);
        return artifact is null ? null : await ToDtoAsync(artifact, cancellationToken);
    }

    /// <summary>
    /// FR-21: the line diff between two versions; <c>NOT_FOUND</c> if either doesn't exist, <c>VALIDATION</c> if they are
    /// too large to diff (<see cref="LineDiff.MaxTotalLines"/>).
    /// </summary>
    public async Task<Result<ArtifactDiffDto>> DiffAsync(Guid projectId, ArtifactType type, int from, int to, CancellationToken cancellationToken)
    {
        var older = await FindAsync(projectId, type, from, cancellationToken);
        var newer = from == to ? older : await FindAsync(projectId, type, to, cancellationToken);
        if (older is null || newer is null)
        {
            var missing = older is null ? from : to;
            return Result.Failure<ArtifactDiffDto>(new DomainError(ErrorCodes.NotFound, $"Version {missing} of the {type.ToWire()} was not found."));
        }

        await integrity.CheckAsync(from == to ? [older] : [older, newer], cancellationToken);
        var diff = LineDiff.Diff(older.Content, newer.Content, $"{type.ToWire()} v{from}", $"{type.ToWire()} v{to}");
        if (diff.IsFailure)
        {
            return Result.Failure<ArtifactDiffDto>(diff.Error!);
        }

        return Result.Success(new ArtifactDiffDto(
            from,
            to,
            diff.Value.Unified,
            [.. diff.Value.Lines.Select(l => new DiffLineDto(
                l.Kind, l.OldLine, l.NewLine, l.Text, l.Kind == DiffLineKind.Skipped ? l.Skipped : null))]));
    }

    /// <summary>Maps a version (re-checking its integrity) with author and approver names.</summary>
    public async Task<ArtifactDto> ToDtoAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var checkedIntegrity = await integrity.CheckAsync(artifact, cancellationToken);
        var names = await ActorResolver.NamesAsync(db, new[] { artifact.CreatedByUserId, artifact.ApprovedBy }.OfType<Guid>(), cancellationToken);
        return new ArtifactDto(
            artifact.Id,
            artifact.Type,
            artifact.Version,
            artifact.ParentVersion,
            artifact.Content,
            artifact.ContentHash,
            artifact.Status,
            artifact.ProjectVersionAtCreation,
            Author(artifact, names),
            artifact.ApprovedBy is { } approver ? User(approver, names) : null,
            artifact.ApprovedAt,
            artifact.RejectionComment,
            artifact.CreatedAt,
            checkedIntegrity);
    }

    private Task<Artifact?> FindAsync(Guid projectId, ArtifactType type, int version, CancellationToken cancellationToken) =>
        db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ProjectId == projectId && a.Type == type && a.Version == version, cancellationToken);

    private async Task<IReadOnlyList<ArtifactSummaryDto>> SummariesAsync(IReadOnlyList<Artifact> artifacts, CancellationToken cancellationToken)
    {
        var checkedIntegrity = await integrity.CheckAsync(artifacts, cancellationToken);
        var names = await ActorResolver.NamesAsync(db, artifacts.Select(a => a.CreatedByUserId).OfType<Guid>(), cancellationToken);
        return
        [
            .. artifacts.Select(a => new ArtifactSummaryDto(
                a.Id,
                a.Type,
                a.Version,
                a.Status,
                checkedIntegrity[a.Id],
                a.CreatedAt,
                Author(a, names))),
        ];
    }

    private static ArtifactAuthorDto Author(Artifact artifact, Dictionary<Guid, string> names) =>
        new(artifact.CreatedByUserId is { } userId ? User(userId, names) : null, artifact.AgentRunId);

    private static ActorDto User(Guid userId, Dictionary<Guid, string> names) =>
        new(userId, names.GetValueOrDefault(userId, ActorResolver.DeletedUserName), false);
}
