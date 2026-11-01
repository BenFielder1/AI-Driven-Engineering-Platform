using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Stack;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Stack;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Stack;

/// <summary>
/// FR-114–FR-119: compositions on projects and release versions. Validation is <see cref="StackCompatibility.Validate"/>
/// after the R5 VCS rule (<see cref="StackPinning.FixVcs(StackSelection, StackVcsPick?)"/>); pinning writes the normalised composition with the catalog
/// version and hash on the version (a migrated version keeps its retired preset columns as history, FR-129) and keeps
/// <c>projects.stack_packs</c> in sync with the current version. Used by project create, <c>POST /versions</c> and
/// <c>GET/PUT /api/projects/:id/stack</c>.
/// </summary>
public sealed partial class ProjectStackService(
    AideDbContext db,
    IStackCatalog stacks,
    VersionStackReader reader,
    Infrastructure.GitLab.IGitLabInstances gitLabInstances,
    TimeProvider time,
    ILogger<ProjectStackService> logger)
{
    /// <summary>
    /// FR-116, R5: the normalised composition of <paramref name="request"/> for a repository whose host is
    /// <paramref name="repoVcs"/> (<see cref="StackPinning.RepoVcs"/>). <c>VCS_MISMATCH</c>, <c>VALIDATION</c> (details
    /// <c>"&lt;fieldPrefix&gt;field: message"</c>) or <c>STACK_INCOMPATIBLE</c> (with the violations).
    /// </summary>
    public Result<StackComposition> Resolve(StackCompositionRequest request, string? repoVcs, string fieldPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(StackMapping.ToSelection(request), repoVcs, fieldPrefix);
    }

    /// <summary>As <see cref="Resolve(StackCompositionRequest, string?, string)"/>, for a selection.</summary>
    public Result<StackComposition> Resolve(StackSelection selection, string? repoVcs, string fieldPrefix) =>
        Resolve(selection, repoVcs is null ? null : new StackVcsPick(repoVcs), fieldPrefix);

    /// <summary>FR-136: as <see cref="Resolve(StackCompositionRequest, string?, string)"/> for the repository host <see cref="RepoHostAsync(Guid, CancellationToken)"/>.</summary>
    public Result<StackComposition> Resolve(StackCompositionRequest request, StackVcsPick? repoHost, string fieldPrefix)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(StackMapping.ToSelection(request), repoHost, fieldPrefix);
    }

    /// <summary>As <see cref="Resolve(StackCompositionRequest, StackVcsPick?, string)"/>, for a selection.</summary>
    public Result<StackComposition> Resolve(StackSelection selection, StackVcsPick? repoHost, string fieldPrefix)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var fixedVcs = StackPinning.FixVcs(selection, repoHost);
        if (fixedVcs.IsFailure)
        {
            var error = fixedVcs.Error!;
            return Result.Failure<StackComposition>(error with { Details = [.. error.Details.Select(d => fieldPrefix + d)] });
        }

        var validation = StackCompatibility.Validate(fixedVcs.Value, stacks.Current);
        if (validation.InputErrors.Count > 0)
        {
            return Result.Failure<StackComposition>(new DomainError(ErrorCodes.Validation, "The stack choices are invalid.")
            {
                Details = [.. validation.InputErrors.Select(e => fieldPrefix + e)],
            });
        }

        return validation.Composition is { } composition
            ? Result.Success(composition)
            : Result.Failure<StackComposition>(StackCompatibility.IncompatibleError(validation.Violations));
    }

    /// <summary>
    /// FR-117 (<c>POST /versions</c> without a stack): <paramref name="composition"/> re-validated against the catalog
    /// shipped now, so it is pinned with the current catalog version and hash. <c>STACK_INCOMPATIBLE</c> when the catalog's
    /// rules no longer allow it.
    /// </summary>
    public Result<StackComposition> Repin(StackComposition composition, string? repoVcs) =>
        Resolve(StackPinning.ToSelection(composition), repoVcs, "stack.");

    /// <summary>
    /// FR-117: pins <paramref name="composition"/> (or none) on <paramref name="version"/>. FR-129: the retired preset columns
    /// (<c>preset_id/version/hash</c>) are history and are never cleared.
    /// </summary>
    public static void Pin(ReleaseVersion version, StackComposition? composition)
    {
        ArgumentNullException.ThrowIfNull(version);
        version.StackComposition = composition is null ? null : StackCompositionJson.Serialize(composition);
        version.CatalogVersion = composition?.CatalogVersion;
        version.CatalogHash = composition?.CatalogHash;
    }

    /// <summary>§5: <c>projects.stack_packs</c> = the pack ids of the current version's composition (<paramref name="composition"/>).</summary>
    public void SyncPacks(Project project, StackComposition? composition)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.StackPacks = composition is null ? [] : StackPinning.PackIds(composition, stacks.Current);
    }

    /// <summary>
    /// FR-136, R5: the VCS pick of a repository host: the provider's VCS option and, for GitLab, the instance and whether it
    /// is self-managed (not gitlab.com).
    /// </summary>
    public async Task<StackVcsPick?> RepoHostAsync(GitProvider provider, Guid? instanceId, CancellationToken cancellationToken)
    {
        var selfManaged = provider == GitProvider.Gitlab && instanceId is { } id
            && await gitLabInstances.GetAsync(id, cancellationToken) is { IsGitLabCom: false };
        return StackPinning.RepoHost(provider, instanceId, selfManaged);
    }

    /// <summary>FR-136, R5: <see cref="RepoHostAsync(GitProvider, Guid?, CancellationToken)"/> of the project's repository.</summary>
    public async Task<StackVcsPick?> RepoHostAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var git = await db.ProjectGitSettings.AsNoTracking()
            .Where(g => g.ProjectId == projectId)
            .Select(g => new { g.Provider, g.InstanceId })
            .FirstOrDefaultAsync(cancellationToken);
        return git is null ? null : await RepoHostAsync(git.Provider, git.InstanceId, cancellationToken);
    }

    /// <summary>R5: the VCS option of the project's repository host (<c>null</c>: Local, any VCS).</summary>
    public async Task<string?> RepoVcsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var provider = await db.ProjectGitSettings.AsNoTracking()
            .Where(g => g.ProjectId == projectId)
            .Select(g => (GitProvider?)g.Provider)
            .FirstOrDefaultAsync(cancellationToken);
        return provider is { } p ? StackPinning.RepoVcs(p) : null;
    }

    /// <summary><c>GET /api/projects/:id/stack</c>: the stack of the project's current release version (<c>NOT_FOUND</c> without one).</summary>
    public async Task<Result<ProjectStackDto>> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var current = await reader.CurrentAsync(projectId, cancellationToken);
        if (current is null)
        {
            return Result.Failure<ProjectStackDto>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        var stackLock = StackPinning.Lock(current.Version.Status, await PlanWasLockedAsync(projectId, current.Version.Id, cancellationToken));
        return Result.Success(ToDto(current, stackLock, await RepoVcsAsync(projectId, cancellationToken)));
    }

    /// <summary>
    /// <c>PUT /api/projects/:id/stack</c> (FR-117): pins <paramref name="request"/> on the version in development. Checks, in
    /// order: <c>STACK_LOCKED</c> (nothing in development, or its first implementation plan was locked), <c>VCS_MISMATCH</c>,
    /// <c>VALIDATION</c>, <c>STACK_INCOMPATIBLE</c>. A change records <c>STACK_CHANGED</c> (with the retired preset id of a migrated version); the
    /// pinned composition again (same catalog) changes nothing. <c>CONFLICT</c> on a concurrent edit.
    /// </summary>
    public async Task<Result<ProjectStackDto>> ChangeAsync(Guid projectId, StackCompositionRequest request, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<ProjectStackDto>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        // The current release version as GET reads it (in development, else the latest), tracked.
        var version = await db.CurrentReleaseVersionAsync(projectId, cancellationToken);
        var allowed = StackPinning.CanChange(version?.Status, version is not null && await PlanWasLockedAsync(projectId, version.Id, cancellationToken));
        if (allowed.IsFailure)
        {
            return Result.Failure<ProjectStackDto>(allowed.Error!);
        }

        var target = version!;
        var resolved = Resolve(request, await RepoHostAsync(projectId, cancellationToken), string.Empty);
        if (resolved.IsFailure)
        {
            return Result.Failure<ProjectStackDto>(resolved.Error!);
        }

        var to = resolved.Value;
        var from = StackCompositionJson.Deserialize(target.StackComposition);
        if (from is not null && StackCompositionJson.Serialize(from) == StackCompositionJson.Serialize(to))
        {
            return await GetAsync(projectId, cancellationToken);
        }

        var fromPresetId = target.PresetId;
        var layersChanged = StackPinning.LayersChanged(from, to);
        Pin(target, to);
        SyncPacks(project, to);
        var now = time.GetUtcNow();
        project.UpdatedAt = now;
        db.AddEvent(
            EventTypes.StackChanged,
            project.Id,
            new
            {
                releaseVersionId = target.Id,
                version = target.Version,
                from,
                to,
                layersChanged,
                catalogVersion = to.CatalogVersion,
                catalogHash = to.CatalogHash,
                fromPresetId,
            },
            actor,
            now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ProjectStackDto>(new DomainError(ErrorCodes.Conflict, "The project was changed by someone else. Reload and try again."));
        }

        var layers = string.Join(",", layersChanged);
        LogChanged(logger, project.Id, target.Version, layers);
        return await GetAsync(projectId, cancellationToken);
    }

    /// <summary>FR-117: whether the version's first implementation plan has been locked.</summary>
    private async Task<bool> PlanWasLockedAsync(Guid projectId, Guid releaseVersionId, CancellationToken cancellationToken)
    {
        var plans = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == releaseVersionId && a.Type == ArtifactType.ImplementationPlan)
            .Select(a => new { a.Status, a.ApprovedAt })
            .ToListAsync(cancellationToken);
        return StackPinning.PlanWasLocked(plans.Select(p => (p.Status, p.ApprovedAt)));
    }

    private ProjectStackDto ToDto(VersionComposition stack, StackLock stackLock, string? repoVcs)
    {
        var version = stack.Version;
        var catalog = stacks.Current;
        return new ProjectStackDto(
            version.Id,
            version.Version,
            version.Status,
            stack.Composition is { } c ? StackMapping.ToDto(c) : null,
            version.CatalogVersion,
            version.CatalogHash,
            catalog.Version,
            catalog.ContentHash,
            stackLock.Locked,
            stackLock.Reason,
            new StackChangeDto(stack.Change.BaseHasCode, stack.Change.LayersChanged, stack.Change.IsStackChange),
            LegacyPreset(version, stacks.Catalog?.Legacy),
            repoVcs);
    }

    /// <summary>FR-129: the retired preset a migrated version was pinned to (kept as history), or <c>null</c>.</summary>
    internal static LegacyPresetDto? LegacyPreset(ReleaseVersion version, LegacyPresets? legacy) =>
        version.PresetId is { } presetId
            ? new LegacyPresetDto(
                presetId,
                legacy?.Find(presetId)?.Name ?? presetId,
                version.PresetVersion,
                Domain.Stack.LegacyPreset.HistoryLabel(presetId, version.PresetVersion))
            : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} version {Version}: stack changed (layers {Layers})")]
    private static partial void LogChanged(ILogger logger, Guid projectId, string version, string layers);
}

public static class ProjectStackServiceCollectionExtensions
{
    /// <summary>FR-114–FR-119: compositions on projects and versions, and import detection.</summary>
    public static IServiceCollection AddProjectStacks(this IServiceCollection services)
    {
        services.AddScoped<VersionStackReader>();
        services.AddScoped<VersionStackLoader>();
        services.AddScoped<RetiredPresets>();
        services.AddScoped<ProjectStackService>();
        services.AddScoped<StackDetectionService>();
        return services;
    }
}
