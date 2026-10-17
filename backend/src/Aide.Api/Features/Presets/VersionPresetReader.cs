using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Presets;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Presets;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Presets;

/// <summary>
/// FR-74–FR-77, §4.11: the preset a release version runs with (as shipped now, <see cref="PresetCatalogExtensions.ForVersion"/>)
/// and what its rules need from the repository: whether it is empty (no base commit, or only starter files: scaffold first)
/// and the files on the base commit (immutable <c>existingOnBase</c> paths; <c>null</c> when unknown). Without a preset
/// nothing is read from Git.
/// </summary>
public sealed record VersionPreset(ReleaseVersion? Version, Preset? Preset, bool RepoEmpty, IReadOnlySet<string>? BaseFiles)
{
    public static VersionPreset None { get; } = new(null, null, RepoEmpty: true, BaseFiles: null);

    /// <summary>The agents and rules plan validation uses for a plan of <paramref name="type"/> (FR-76).</summary>
    public PlanAgents PlanAgents(ArtifactType type) =>
        type == ArtifactType.OptimizationPlan
            ? Domain.Execution.PlanAgents.ForOptimization(Preset, BaseFiles)
            : Domain.Execution.PlanAgents.For(Preset, RepoEmpty, BaseFiles);

    /// <summary>Whether <paramref name="path"/> exists on the base commit (unknown: <c>false</c>).</summary>
    public bool ExistsOnBase(string path) => BaseFiles?.Contains(path) ?? false;
}

/// <summary>Reads the <see cref="VersionPreset"/> of a release version, or of a project's current one.</summary>
public sealed partial class VersionPresetReader(
    AideDbContext db,
    IPresetCatalog catalog,
    IWorkingCopies workingCopies,
    Agents.AgentRepoContext repoContext,
    ILogger<VersionPresetReader> logger)
{
    /// <summary>The most base-commit paths read (beyond it the base counts as unknown).</summary>
    public const int MaxBaseFiles = 500_000;

    /// <summary>The project's current release version's preset context.</summary>
    public async Task<VersionPreset> CurrentAsync(Guid projectId, CancellationToken cancellationToken) =>
        await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken) is { } id
            ? await ForVersionAsync(id, cancellationToken)
            : VersionPreset.None;

    /// <summary>
    /// The preset context of release version <paramref name="releaseVersionId"/>. Without <paramref name="readBase"/> the
    /// base commit isn't listed (<see cref="VersionPreset.BaseFiles"/> unknown; empty only without a base commit).
    /// </summary>
    public async Task<VersionPreset> ForVersionAsync(Guid releaseVersionId, CancellationToken cancellationToken, bool readBase = true)
    {
        var version = await db.ReleaseVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == releaseVersionId, cancellationToken);
        return version is null ? VersionPreset.None : await ForVersionAsync(version, cancellationToken, readBase);
    }

    /// <summary>The preset context of <paramref name="version"/> (see <see cref="ForVersionAsync(Guid, CancellationToken, bool)"/>).</summary>
    public async Task<VersionPreset> ForVersionAsync(ReleaseVersion version, CancellationToken cancellationToken, bool readBase = true)
    {
        ArgumentNullException.ThrowIfNull(version);
        var preset = catalog.ForVersion(version);
        if (preset is null)
        {
            return new VersionPreset(version, null, RepoEmpty: string.IsNullOrWhiteSpace(version.BaseCommitSha), BaseFiles: null);
        }

        if (string.IsNullOrWhiteSpace(version.BaseCommitSha))
        {
            return new VersionPreset(version, preset, RepoEmpty: true, BaseFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        if (!readBase)
        {
            return new VersionPreset(version, preset, RepoEmpty: false, BaseFiles: null);
        }

        IReadOnlyList<string>? paths = null;
        try
        {
            paths = await workingCopies.ListPathsAtCommitAsync(version.ProjectId, version.BaseCommitSha, MaxBaseFiles, cancellationToken);

            // A GitHub commit not in the working copy yet (e.g. planning before execution): fetch it, then list again.
            if (paths is null && await repoContext.FetchAsync(version.ProjectId, cancellationToken))
            {
                paths = await workingCopies.ListPathsAtCommitAsync(version.ProjectId, version.BaseCommitSha, MaxBaseFiles, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            LogBaseUnreadable(logger, version.Id, ex.GetType().Name);
        }

        // Unknown base (fetch failed, or too many files): the repository has code; generated plans can't add immutable files.
        return paths is null || paths.Count >= MaxBaseFiles
            ? new VersionPreset(version, preset, RepoEmpty: false, BaseFiles: null)
            : new VersionPreset(version, preset, PresetPathRules.IsStarterOnly(paths), paths.Select(PathRules.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The base commit of release version {ReleaseVersionId} could not be listed ({ErrorType}); preset rules infer existing files from the plan")]
    private static partial void LogBaseUnreadable(ILogger logger, Guid releaseVersionId, string errorType);
}
