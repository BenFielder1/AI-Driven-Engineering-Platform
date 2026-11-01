using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Stack;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Stack;

/// <summary>
/// FR-129: the retired v2 preset a project row or release version shows as history (§7.2): its id, name (from the frozen
/// legacy data, else the id) and version. All <c>null</c> when the version was never pinned to a preset.
/// </summary>
public sealed record ProjectPresetRef(string? PresetId, string? PresetName, string? PresetVersion)
{
    public static ProjectPresetRef None { get; } = new(null, null, null);
}

/// <summary>
/// FR-129: reading the retired preset columns (<c>preset_id/version</c>), kept on release versions and agent runs as history
/// after the migration to compositions. Nothing writes them any more.
/// </summary>
public sealed class RetiredPresets(AideDbContext db, IStackCatalog catalog)
{
    /// <summary>The retired preset <paramref name="presetId"/> at <paramref name="presetVersion"/>, for DTOs.</summary>
    public ProjectPresetRef Describe(string? presetId, string? presetVersion) => Describe(catalog, presetId, presetVersion);

    /// <summary>As <see cref="Describe(string?, string?)"/>, with the catalog given.</summary>
    public static ProjectPresetRef Describe(IStackCatalog catalog, string? presetId, string? presetVersion)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return presetId is null
            ? ProjectPresetRef.None
            : new ProjectPresetRef(presetId, catalog.Catalog?.Legacy.Find(presetId)?.Name ?? presetId, presetVersion);
    }

    /// <summary>The retired preset of the project's current release version (in development, else the latest).</summary>
    public async Task<ProjectPresetRef> ForProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        var pinned = versionId is { } id
            ? await db.ReleaseVersions.AsNoTracking().Where(v => v.Id == id).Select(v => new { v.PresetId, v.PresetVersion }).FirstOrDefaultAsync(cancellationToken)
            : null;
        return Describe(pinned?.PresetId, pinned?.PresetVersion);
    }
}
