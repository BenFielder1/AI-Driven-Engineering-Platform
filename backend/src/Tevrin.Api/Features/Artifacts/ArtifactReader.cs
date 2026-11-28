using Microsoft.EntityFrameworkCore;
using Tevrin.Api.Features.Activity;
using Tevrin.Api.Features.Stack;
using Tevrin.Api.Pipelines;
using Tevrin.Domain.Common;
using Tevrin.Domain.Deployments;
using Tevrin.Domain.Entities;
using Tevrin.Domain.Execution;
using Tevrin.Domain.Specs;
using Tevrin.Domain.Stack;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Infrastructure.Persistence;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Api.Features.Artifacts;

/// <summary>
/// FR-16, FR-20, FR-21: artifact reads. Every read goes through <see cref="ArtifactIntegrityGuard"/>, so each returned
/// version carries the integrity found by re-hashing it now.
/// </summary>
public sealed class ArtifactReader(TevrinDbContext db, ArtifactIntegrityGuard integrity, VersionStackLoader stacks)
{
    /// <summary>The highest version of each type that exists in release version <paramref name="releaseVersionId"/>, in type order.</summary>
    public async Task<IReadOnlyList<ArtifactSummaryDto>> ListLatestAsync(Guid projectId, Guid? releaseVersionId, CancellationToken cancellationToken)
    {
        // FR-334: no release version, no SDLC artifacts (a null scope never matches a subject-less run's rows).
        if (releaseVersionId is null)
        {
            return [];
        }

        var latest = await db.Artifacts.Sdlc().AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == releaseVersionId
                && a.Version == db.Artifacts.Where(b => b.ReleaseVersionId != null && b.ProjectId == projectId && b.ReleaseVersionId == releaseVersionId && b.Type == a.Type).Max(b => b.Version))
            .ToListAsync(cancellationToken);
        // Type order is the 0.6.1 enum order (SdlcArtifacts.All), not alphabetical.
        return await SummariesAsync([.. latest.OrderBy(a => SdlcArtifacts.Order(a.Type))], cancellationToken);
    }

    /// <summary>Every version of a type in release version <paramref name="releaseVersionId"/>, newest first.</summary>
    public async Task<IReadOnlyList<ArtifactSummaryDto>> HistoryAsync(Guid projectId, string type, Guid? releaseVersionId, CancellationToken cancellationToken)
    {
        if (releaseVersionId is null)
        {
            return [];
        }

        var versions = await db.Artifacts.Sdlc().AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == releaseVersionId && a.Type == type)
            .OrderByDescending(a => a.Version)
            .ToListAsync(cancellationToken);
        return await SummariesAsync(versions, cancellationToken);
    }

    /// <summary>FR-59: every version of a type across all the project's release versions, newest first.</summary>
    public async Task<IReadOnlyList<ArtifactSummaryDto>> HistoryAcrossReleaseVersionsAsync(Guid projectId, string type, CancellationToken cancellationToken)
    {
        var versions = await db.Artifacts.Sdlc().AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == type)
            .OrderByDescending(a => a.Version)
            .ToListAsync(cancellationToken);
        return await SummariesAsync(versions, cancellationToken);
    }

    /// <summary>The highest version of a type in release version <paramref name="releaseVersionId"/>, without its content, or <c>null</c>.</summary>
    public async Task<ArtifactSummaryDto?> LatestSummaryAsync(Guid projectId, string type, Guid? releaseVersionId, CancellationToken cancellationToken)
    {
        if (releaseVersionId is null)
        {
            return null;
        }

        var versionId = releaseVersionId;
        var artifact = await db.Artifacts.Sdlc().AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == versionId && a.Type == type)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return artifact is null ? null : (await SummariesAsync([artifact], cancellationToken))[0];
    }

    /// <summary>The highest version of a type in release version <paramref name="releaseVersionId"/>, or <c>null</c>.</summary>
    public async Task<ArtifactDto?> LatestAsync(Guid projectId, string type, Guid? releaseVersionId, CancellationToken cancellationToken)
    {
        if (releaseVersionId is null)
        {
            return null;
        }

        var artifact = await db.Artifacts.Sdlc().AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == releaseVersionId && a.Type == type)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return artifact is null ? null : await ToDtoAsync(artifact, cancellationToken);
    }

    /// <summary>One version of a type, or <c>null</c>.</summary>
    public async Task<ArtifactDto?> VersionAsync(Guid projectId, string type, int version, CancellationToken cancellationToken)
    {
        var artifact = await FindAsync(projectId, type, version, cancellationToken);
        return artifact is null ? null : await ToDtoAsync(artifact, cancellationToken);
    }

    /// <summary>
    /// FR-21: the line diff between two versions; <c>NOT_FOUND</c> if either doesn't exist, <c>VALIDATION</c> if they are
    /// too large to diff (<see cref="LineDiff.MaxTotalLines"/>).
    /// </summary>
    public async Task<Result<ArtifactDiffDto>> DiffAsync(Guid projectId, string type, int from, int to, CancellationToken cancellationToken)
    {
        var older = await FindAsync(projectId, type, from, cancellationToken);
        var newer = from == to ? older : await FindAsync(projectId, type, to, cancellationToken);
        if (older is null || newer is null)
        {
            var missing = older is null ? from : to;
            return Result.Failure<ArtifactDiffDto>(new DomainError(ErrorCodes.NotFound, $"Version {missing} of the {type} was not found."));
        }

        await integrity.CheckAsync(from == to ? [older] : [older, newer], cancellationToken);
        var diff = LineDiff.Diff(older.Content, newer.Content, $"{type} v{from}", $"{type} v{to}");
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
        var releaseVersions = await ReleaseVersionNamesAsync([SdlcRows.ReleaseVersion(artifact.ReleaseVersionId)], cancellationToken);
        return new ArtifactDto(
            artifact.Id,
            artifact.Type,
            artifact.Version,
            artifact.ParentVersion,
            artifact.Content,
            artifact.ContentHash,
            artifact.Status,
            SdlcRows.ReleaseVersion(artifact.ReleaseVersionId),
            artifact.CheckpointAtCreation,
            Author(artifact, names),
            artifact.ApprovedBy is { } approver ? User(approver, names) : null,
            artifact.ApprovedAt,
            artifact.RejectionComment,
            artifact.CreatedAt,
            checkedIntegrity,
            releaseVersions.GetValueOrDefault(SdlcRows.ReleaseVersion(artifact.ReleaseVersionId), string.Empty),
            artifact.RestoredFromVersion,
            await WarningsAsync(artifact, cancellationToken));
    }

    /// <summary>
    /// FR-124, §4.13: an implementation plan's warnings, computed on read (<see cref="CompositionPlanValidator.AllWarnings"/>):
    /// coverage (<c>UNCOVERED_REQUIREMENTS</c>, against the latest locked specification of its release version, so it follows
    /// the spec it is checked against) and, with the version's composition, <c>AGENT_SCOPE</c>, <c>PLAN_ORDER</c> and
    /// <c>UNDECLARED_GENERATED_FILE</c>. §4.23: steps of a migrated 0.2.0 plan are checked against the frozen 0.2.0 globs.
    /// Without a composition only coverage applies.
    /// </summary>
    private async Task<IReadOnlyList<ArtifactWarningDto>> WarningsAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        if (artifact.Type == SdlcArtifacts.TechnicalSpecification)
        {
            return await SpecWarningsAsync(artifact, cancellationToken);
        }

        if (artifact.Type != SdlcArtifacts.ImplementationPlan)
        {
            return [];
        }

        var version = await stacks.ForVersionAsync(SdlcRows.ReleaseVersion(artifact.ReleaseVersionId), cancellationToken, readBase: false);
        var plan = PlanDocument.ParseMarkdown(artifact.Content, maxFilesPerStep: null, version.PlanAgents(artifact.Type));
        if (plan.IsFailure)
        {
            return [];
        }

        var spec = await db.Artifacts.Sdlc().AsNoTracking()
            .Where(a => a.ProjectId == artifact.ProjectId && a.ReleaseVersionId == artifact.ReleaseVersionId
                && a.Type == SdlcArtifacts.TechnicalSpecification && a.Status == ArtifactStatus.Locked)
            .OrderByDescending(a => a.Version)
            .Select(a => a.Content)
            .FirstOrDefaultAsync(cancellationToken);
        var requirements = spec is null ? null : RequirementParser.Parse(spec).Requirements;
        // FR-124, FR-127: a composed plan's warnings come from the composition (AGENT_SCOPE, PLAN_ORDER, UNDECLARED_GENERATED_FILE).
        IReadOnlyList<ValidationWarning> warnings = version.Stack is { } stack
            ? CompositionPlanValidator.AllWarnings(plan.Value, requirements, stack, version.Legacy?.Preset)
            : requirements is not null && PlanScope.Check(plan.Value, requirements) is { } uncovered ? [uncovered] : [];

        // FR-89: a stack deploying the frontend to Vercel on a new repository needs vercel.json in the Vercel root directory
        // (the base isn't listed on read, so with existing code its files are unknown and the warning is skipped). Before a
        // target is set up, its root directory is the frontend layer path (as DeploymentBuildRules derives it), the folder
        // FR-243's added step creates vercel.json in.
        if (version.Stack?.Composition.Deploy.FrontendHost == "deploy-vercel")
        {
            var rootDirectory = await db.DeploymentTargets.AsNoTracking()
                .Where(t => t.ProjectId == artifact.ProjectId && t.Component == DeploymentComponent.Frontend)
                .Select(s => s.RootDirectory)
                .FirstOrDefaultAsync(cancellationToken) ?? version.Stack.Composition.Frontend?.Path;
            if (VercelConfigRules.PlanWarning(plan.Value, deploysToVercel: true, version.BaseFiles, rootDirectory) is { } vercel)
            {
                warnings = [.. warnings, vercel];
            }
        }
        return [.. warnings.Select(w => new ArtifactWarningDto(w.Code, w.Message, w.Details))];
    }

    /// <summary>FR-172: <c>UNCOVERED_SOURCE_ITEMS</c> for the version's imported, not-done items no requirement cites; advisory (D-V5-6).</summary>
    private async Task<IReadOnlyList<ArtifactWarningDto>> SpecWarningsAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        var imports = await db.TrackerImports.AsNoTracking()
            .Where(i => i.ReleaseVersionId == artifact.ReleaseVersionId && i.Status == Domain.Trackers.TrackerImportStatus.Completed)
            .ToListAsync(cancellationToken);
        if (imports.Count == 0)
        {
            return [];
        }

        var kept = Domain.Trackers.SourceItemsRenderer.Render(imports.Select(Trackers.SourceSnapshotWriter.ToSnapshot)).Items;
        return Domain.Trackers.SourceTraceability.Check(artifact.Content, kept) is { } warning
            ? [new ArtifactWarningDto(warning.Code, warning.Message, warning.Details)]
            : [];
    }

    private Task<Artifact?> FindAsync(Guid projectId, string type, int version, CancellationToken cancellationToken) =>
        db.Artifacts.Sdlc().AsNoTracking().FirstOrDefaultAsync(a => a.ProjectId == projectId && a.Type == type && a.Version == version, cancellationToken);

    private async Task<IReadOnlyList<ArtifactSummaryDto>> SummariesAsync(IReadOnlyList<Artifact> artifacts, CancellationToken cancellationToken)
    {
        var checkedIntegrity = await integrity.CheckAsync(artifacts, cancellationToken);
        var names = await ActorResolver.NamesAsync(db, artifacts.Select(a => a.CreatedByUserId).OfType<Guid>(), cancellationToken);
        var releaseVersions = await ReleaseVersionNamesAsync([.. artifacts.Select(a => SdlcRows.ReleaseVersion(a.ReleaseVersionId)).Distinct()], cancellationToken);
        return
        [
            .. artifacts.Select(a => new ArtifactSummaryDto(
                a.Id,
                a.Type,
                a.Version,
                a.Status,
                SdlcRows.ReleaseVersion(a.ReleaseVersionId),
                checkedIntegrity[a.Id],
                a.CreatedAt,
                Author(a, names),
                releaseVersions.GetValueOrDefault(SdlcRows.ReleaseVersion(a.ReleaseVersionId), string.Empty),
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
