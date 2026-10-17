using Aide.Api.Features.Activity;
using Aide.Api.Features.Presets;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Presets;
using Aide.Domain.Specs;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Artifacts;

/// <summary>
/// FR-16, FR-20, FR-21: artifact reads. Every read goes through <see cref="ArtifactIntegrityGuard"/>, so each returned
/// version carries the integrity found by re-hashing it now.
/// </summary>
public sealed class ArtifactReader(AideDbContext db, ArtifactIntegrityGuard integrity, VersionPresetReader presets)
{
    /// <summary>The highest version of each type that exists in release version <paramref name="releaseVersionId"/>, in type order.</summary>
    public async Task<IReadOnlyList<ArtifactSummaryDto>> ListLatestAsync(Guid projectId, Guid? releaseVersionId, CancellationToken cancellationToken)
    {
        var latest = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == releaseVersionId
                && a.Version == db.Artifacts.Where(b => b.ProjectId == projectId && b.ReleaseVersionId == releaseVersionId && b.Type == a.Type).Max(b => b.Version))
            .ToListAsync(cancellationToken);
        return await SummariesAsync([.. latest.OrderBy(a => a.Type)], cancellationToken);
    }

    /// <summary>Every version of a type in release version <paramref name="releaseVersionId"/>, newest first.</summary>
    public async Task<IReadOnlyList<ArtifactSummaryDto>> HistoryAsync(Guid projectId, ArtifactType type, Guid? releaseVersionId, CancellationToken cancellationToken)
    {
        var versions = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == releaseVersionId && a.Type == type)
            .OrderByDescending(a => a.Version)
            .ToListAsync(cancellationToken);
        return await SummariesAsync(versions, cancellationToken);
    }

    /// <summary>FR-59: every version of a type across all the project's release versions, newest first.</summary>
    public async Task<IReadOnlyList<ArtifactSummaryDto>> HistoryAcrossReleaseVersionsAsync(Guid projectId, ArtifactType type, CancellationToken cancellationToken)
    {
        var versions = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == type)
            .OrderByDescending(a => a.Version)
            .ToListAsync(cancellationToken);
        return await SummariesAsync(versions, cancellationToken);
    }

    /// <summary>The highest version of a type in release version <paramref name="releaseVersionId"/>, without its content, or <c>null</c>.</summary>
    public async Task<ArtifactSummaryDto?> LatestSummaryAsync(Guid projectId, ArtifactType type, Guid? releaseVersionId, CancellationToken cancellationToken)
    {
        var versionId = releaseVersionId;
        var artifact = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == versionId && a.Type == type)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return artifact is null ? null : (await SummariesAsync([artifact], cancellationToken))[0];
    }

    /// <summary>The highest version of a type in release version <paramref name="releaseVersionId"/>, or <c>null</c>.</summary>
    public async Task<ArtifactDto?> LatestAsync(Guid projectId, ArtifactType type, Guid? releaseVersionId, CancellationToken cancellationToken)
    {
        var artifact = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == releaseVersionId && a.Type == type)
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
        var releaseVersions = await ReleaseVersionNamesAsync([artifact.ReleaseVersionId], cancellationToken);
        return new ArtifactDto(
            artifact.Id,
            artifact.Type,
            artifact.Version,
            artifact.ParentVersion,
            artifact.Content,
            artifact.ContentHash,
            artifact.Status,
            artifact.ReleaseVersionId,
            artifact.CheckpointAtCreation,
            Author(artifact, names),
            artifact.ApprovedBy is { } approver ? User(approver, names) : null,
            artifact.ApprovedAt,
            artifact.RejectionComment,
            artifact.CreatedAt,
            checkedIntegrity,
            releaseVersions.GetValueOrDefault(artifact.ReleaseVersionId, string.Empty),
            artifact.RestoredFromVersion,
            await WarningsAsync(artifact, cancellationToken));
    }

    /// <summary>
    /// FR-76, §4.13: an implementation plan's warnings, computed on read (<see cref="PresetPlanValidator.AllWarnings"/>):
    /// coverage (<c>UNCOVERED_REQUIREMENTS</c>, against the latest locked specification of its release version, so it follows
    /// the spec it is checked against) and, with the version's preset, <c>AGENT_SCOPE</c> and <c>PLAN_ORDER</c>.
    /// </summary>
    private async Task<IReadOnlyList<ArtifactWarningDto>> WarningsAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        if (artifact.Type != ArtifactType.ImplementationPlan)
        {
            return [];
        }

        var preset = await presets.ForVersionAsync(artifact.ReleaseVersionId, cancellationToken, readBase: false);
        var plan = PlanDocument.ParseMarkdown(artifact.Content, maxFilesPerStep: null, preset.PlanAgents(artifact.Type));
        if (plan.IsFailure)
        {
            return [];
        }

        var spec = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == artifact.ProjectId && a.ReleaseVersionId == artifact.ReleaseVersionId
                && a.Type == ArtifactType.TechnicalSpecification && a.Status == ArtifactStatus.Locked)
            .OrderByDescending(a => a.Version)
            .Select(a => a.Content)
            .FirstOrDefaultAsync(cancellationToken);
        var requirements = spec is null ? null : RequirementParser.Parse(spec).Requirements;
        return
        [
            .. PresetPlanValidator.AllWarnings(plan.Value, requirements, preset.Preset)
                .Select(w => new ArtifactWarningDto(w.Code, w.Message, w.Details)),
        ];
    }

    private Task<Artifact?> FindAsync(Guid projectId, ArtifactType type, int version, CancellationToken cancellationToken) =>
        db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ProjectId == projectId && a.Type == type && a.Version == version, cancellationToken);

    private async Task<IReadOnlyList<ArtifactSummaryDto>> SummariesAsync(IReadOnlyList<Artifact> artifacts, CancellationToken cancellationToken)
    {
        var checkedIntegrity = await integrity.CheckAsync(artifacts, cancellationToken);
        var names = await ActorResolver.NamesAsync(db, artifacts.Select(a => a.CreatedByUserId).OfType<Guid>(), cancellationToken);
        var releaseVersions = await ReleaseVersionNamesAsync([.. artifacts.Select(a => a.ReleaseVersionId).Distinct()], cancellationToken);
        return
        [
            .. artifacts.Select(a => new ArtifactSummaryDto(
                a.Id,
                a.Type,
                a.Version,
                a.Status,
                a.ReleaseVersionId,
                checkedIntegrity[a.Id],
                a.CreatedAt,
                Author(a, names),
                releaseVersions.GetValueOrDefault(a.ReleaseVersionId, string.Empty),
                a.RestoredFromVersion)),
        ];
    }

    /// <summary>FR-59: the semver of each release version, by id.</summary>
    private async Task<Dictionary<Guid, string>> ReleaseVersionNamesAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? []
            : await db.ReleaseVersions.AsNoTracking()
                .Where(v => ids.Contains(v.Id))
                .ToDictionaryAsync(v => v.Id, v => v.Version, cancellationToken);

    private static ArtifactAuthorDto Author(Artifact artifact, Dictionary<Guid, string> names) =>
        new(artifact.CreatedByUserId is { } userId ? User(userId, names) : null, artifact.AgentRunId);

    private static ActorDto User(Guid userId, Dictionary<Guid, string> names) =>
        new(userId, names.GetValueOrDefault(userId, ActorResolver.DeletedUserName), false);
}
