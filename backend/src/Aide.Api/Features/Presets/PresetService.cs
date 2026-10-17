using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Presets;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Presets;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Presets;

/// <summary>The preset a project row shows (§7.2): of its current release version; <c>presetName</c> falls back to the id when the preset is no longer shipped.</summary>
public sealed record ProjectPresetRef(string? PresetId, string? PresetName, string? PresetVersion)
{
    public static ProjectPresetRef None { get; } = new(null, null, null);
}

/// <summary>
/// FR-74: choosing, pinning and describing presets. Pinning writes <c>preset_id</c>, <c>preset_version</c> and
/// <c>preset_hash</c> on a release version; the rules (<c>PRESET_LOCKED</c>) are <see cref="PresetRules"/>.
/// </summary>
public sealed partial class PresetService(AideDbContext db, IPresetCatalog catalog, TimeProvider time, ILogger<PresetService> logger)
{
    /// <summary>
    /// The preset a request names: <c>null</c> for none (blank or omitted), else the catalogue's preset, or
    /// <c>PRESET_NOT_FOUND</c>.
    /// </summary>
    public static Result<Preset?> Resolve(IPresetCatalog catalog, string? presetId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var id = PresetRules.NormaliseId(presetId);
        if (id is null)
        {
            return Result.Success<Preset?>(null);
        }

        return catalog.Find(id) is { } preset
            ? Result.Success<Preset?>(preset)
            : Result.Failure<Preset?>(new DomainError(ErrorCodes.PresetNotFound, $"The preset '{id}' was not found."));
    }

    /// <summary>FR-74: pins <paramref name="preset"/> (or none) and its version and content hash on <paramref name="version"/>.</summary>
    public static void Pin(ReleaseVersion version, Preset? preset)
    {
        ArgumentNullException.ThrowIfNull(version);
        version.PresetId = preset?.Id;
        version.PresetVersion = preset?.Version;
        version.PresetHash = preset?.ContentHash;
    }

    /// <summary>The preset pinned on a version, for DTOs.</summary>
    public static ProjectPresetRef Describe(IPresetCatalog catalog, string? presetId, string? presetVersion)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return presetId is null
            ? ProjectPresetRef.None
            : new ProjectPresetRef(presetId, catalog.Find(presetId)?.Name ?? presetId, presetVersion);
    }

    /// <summary>The preset of the project's current release version (in development, else the latest).</summary>
    public async Task<ProjectPresetRef> ForProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        var pinned = versionId is { } id
            ? await db.ReleaseVersions.AsNoTracking().Where(v => v.Id == id).Select(v => new { v.PresetId, v.PresetVersion }).FirstOrDefaultAsync(cancellationToken)
            : null;
        return Describe(catalog, pinned?.PresetId, pinned?.PresetVersion);
    }

    /// <summary>
    /// FR-74 (<c>PATCH /api/projects/:id/preset</c>): pins <paramref name="presetId"/> (or no preset) on the version in
    /// development. <c>PRESET_NOT_FOUND</c>, then <c>PRESET_LOCKED</c> (nothing in development, or its first implementation
    /// plan was locked). A change records <c>PRESET_CHANGED</c>; picking the pinned preset again changes nothing.
    /// <c>CONFLICT</c> on a concurrent edit of the version.
    /// </summary>
    public async Task<Result<Project>> ChangeAsync(Guid projectId, string? presetId, string actor, CancellationToken cancellationToken)
    {
        var resolved = Resolve(catalog, presetId);
        if (resolved.IsFailure)
        {
            return Result.Failure<Project>(resolved.Error!);
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<Project>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        var version = project.CurrentReleaseVersionId is { } versionId
            ? await db.ReleaseVersions.FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            : null;
        var plans = version is null
            ? []
            : await db.Artifacts.AsNoTracking()
                .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == version.Id && a.Type == ArtifactType.ImplementationPlan)
                .Select(a => new { a.Status, a.ApprovedAt })
                .ToListAsync(cancellationToken);
        var allowed = PresetRules.CanChange(version?.Status, PresetRules.PlanWasLocked(plans.Select(p => (p.Status, p.ApprovedAt))));
        if (allowed.IsFailure)
        {
            return Result.Failure<Project>(allowed.Error!);
        }

        var preset = resolved.Value;
        var target = version!;
        if (target.PresetId == preset?.Id && target.PresetVersion == preset?.Version && target.PresetHash == preset?.ContentHash)
        {
            return Result.Success(project);
        }

        var from = (target.PresetId, target.PresetVersion);
        Pin(target, preset);
        var now = time.GetUtcNow();
        project.UpdatedAt = now;
        db.AddEvent(
            EventTypes.PresetChanged,
            project.Id,
            new
            {
                releaseVersionId = target.Id,
                version = target.Version,
                fromPresetId = from.PresetId,
                fromPresetVersion = from.PresetVersion,
                presetId = target.PresetId,
                presetVersion = target.PresetVersion,
                presetHash = target.PresetHash,
            },
            actor,
            now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<Project>(new DomainError(ErrorCodes.Conflict, "The project was changed by someone else. Reload and try again."));
        }

        LogChanged(logger, project.Id, target.Version, from.PresetId ?? "none", target.PresetId ?? "none");
        return Result.Success(project);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} version {Version}: preset {FromPreset} → {ToPreset}")]
    private static partial void LogChanged(ILogger logger, Guid projectId, string version, string fromPreset, string toPreset);
}

public static class PresetServiceCollectionExtensions
{
    /// <summary>FR-74: the preset service (the catalogue itself is registered by the infrastructure).</summary>
    public static IServiceCollection AddPresets(this IServiceCollection services)
    {
        services.AddScoped<PresetService>();
        services.AddScoped<VersionPresetReader>();
        return services;
    }
}
