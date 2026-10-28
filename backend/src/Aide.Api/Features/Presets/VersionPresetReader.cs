using Aide.Api.Features.Stack;
using Aide.Api.Jobs;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Presets;
using Aide.Domain.Stack;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Presets;
using Aide.Infrastructure.Stack;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Presets;

/// <summary>
/// FR-74–FR-77, §4.11 and FR-120–FR-128, §4.18: the stack a release version's agents run with, and what its rules need from
/// the repository: whether it is empty (no base commit, or only starter files: scaffold first) and the files on the base
/// commit (immutable <c>existingOnBase</c> paths; <c>null</c> when unknown). One of three modes:
/// <list type="bullet">
/// <item><b>composition</b> (<see cref="Stack"/>): the version's pinned composition resolved against the shipped catalog
/// (<see cref="StackResolver.Resolve"/>, with its <see cref="StackChange"/> and baseline). When it can't be resolved
/// (the catalog no longer has a pinned option) <see cref="StackError"/> says why and the agent jobs fail with it;</item>
/// <item><b>preset</b> (<see cref="Preset"/>): the v2 preset as shipped now (<see cref="PresetCatalogExtensions.ForVersion"/>;
/// retired by FR-129);</item>
/// <item><b>general</b> (neither): "No stack (general)", v1 behaviour with the <c>generalist</c>. Nothing is read from Git.</item>
/// </list>
/// FR-128: <see cref="Detection"/> is the stack detector's report at the base commit (asked for with <c>detect</c>).
/// </summary>
public sealed record VersionPreset(ReleaseVersion? Version, Preset? Preset, bool RepoEmpty, IReadOnlySet<string>? BaseFiles)
{
    public static VersionPreset None { get; } = new(null, null, RepoEmpty: true, BaseFiles: null);

    /// <summary>FR-120–FR-127: the version's resolved composition, or <c>null</c> (a preset, no stack, or unresolvable).</summary>
    public StackResolution? Stack { get; init; }

    /// <summary>Why the version's pinned composition couldn't be resolved, or <c>null</c>.</summary>
    public DomainError? StackError { get; init; }

    /// <summary>FR-128: the detector's report at the base commit (with <c>detect</c>, a composition and a base commit), or <c>null</c>.</summary>
    public StackDetectionReport? Detection { get; init; }

    /// <summary>The agents and rules plan validation uses for a plan of <paramref name="type"/> (FR-76, FR-124).</summary>
    public PlanAgents PlanAgents(ArtifactType type) => Stack is { } stack
        ? type == ArtifactType.OptimizationPlan
            ? Domain.Execution.PlanAgents.ForStackOptimization(stack, BaseFiles)
            : Domain.Execution.PlanAgents.ForStack(stack, RepoEmpty, BaseFiles)
        : type == ArtifactType.OptimizationPlan
            ? Domain.Execution.PlanAgents.ForOptimization(Preset, BaseFiles)
            : Domain.Execution.PlanAgents.For(Preset, RepoEmpty, BaseFiles);

    /// <summary>Whether <paramref name="path"/> exists on the base commit (unknown: <c>false</c>).</summary>
    public bool ExistsOnBase(string path) => BaseFiles?.Contains(path) ?? false;

    /// <summary>An agent job of a version whose composition can't be resolved fails (non-transient) with <see cref="StackError"/>.</summary>
    public VersionPreset ThrowIfUnresolvable() => StackError is { } error
        ? throw new JobFailedException(error.Message, transient: false, error.Code)
        : this;
}

/// <summary>Reads the <see cref="VersionPreset"/> of a release version, or of a project's current one.</summary>
public sealed partial class VersionPresetReader(
    AideDbContext db,
    IPresetCatalog catalog,
    IStackCatalog stacks,
    VersionStackReader compositions,
    IWorkingCopies workingCopies,
    Agents.AgentRepoContext repoContext,
    ILogger<VersionPresetReader> logger)
{
    /// <summary>The most base-commit paths read (beyond it the base counts as unknown).</summary>
    public const int MaxBaseFiles = 500_000;

    /// <summary>The project's current release version's stack context.</summary>
    public async Task<VersionPreset> CurrentAsync(Guid projectId, CancellationToken cancellationToken, bool detect = false) =>
        await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken) is { } id
            ? await ForVersionAsync(id, cancellationToken, detect: detect)
            : VersionPreset.None;

    /// <summary>
    /// The stack context of release version <paramref name="releaseVersionId"/>. Without <paramref name="readBase"/> the
    /// base commit isn't listed (<see cref="VersionPreset.BaseFiles"/> unknown; empty only without a base commit).
    /// </summary>
    public async Task<VersionPreset> ForVersionAsync(Guid releaseVersionId, CancellationToken cancellationToken, bool readBase = true, bool detect = false)
    {
        var version = await db.ReleaseVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == releaseVersionId, cancellationToken);
        return version is null ? VersionPreset.None : await ForVersionAsync(version, cancellationToken, readBase, detect);
    }

    /// <summary>The stack context of <paramref name="version"/> (see <see cref="ForVersionAsync(Guid, CancellationToken, bool, bool)"/>).</summary>
    public async Task<VersionPreset> ForVersionAsync(ReleaseVersion version, CancellationToken cancellationToken, bool readBase = true, bool detect = false)
    {
        ArgumentNullException.ThrowIfNull(version);
        var (stack, stackError) = await ResolveAsync(version, cancellationToken);
        var preset = stack is null && stackError is null ? catalog.ForVersion(version) : null;
        var hasRules = stack is not null || preset is not null;
        if (!hasRules)
        {
            return new VersionPreset(version, null, RepoEmpty: string.IsNullOrWhiteSpace(version.BaseCommitSha), BaseFiles: null) { StackError = stackError };
        }

        if (string.IsNullOrWhiteSpace(version.BaseCommitSha))
        {
            return new VersionPreset(version, preset, RepoEmpty: true, BaseFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase)) { Stack = stack };
        }

        if (!readBase)
        {
            return new VersionPreset(version, preset, RepoEmpty: false, BaseFiles: null) { Stack = stack };
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
        if (paths is null || paths.Count >= MaxBaseFiles)
        {
            return new VersionPreset(version, preset, RepoEmpty: false, BaseFiles: null) { Stack = stack };
        }

        var detection = detect && stack is not null ? await DetectAsync(version, paths, stack.Composition, cancellationToken) : null;
        return new VersionPreset(version, preset, PresetPathRules.IsStarterOnly(paths), paths.Select(PathRules.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase))
        {
            Stack = stack,
            Detection = detection,
        };
    }

    /// <summary>
    /// FR-120: the version's pinned composition resolved against the shipped catalog. A composition validated against an
    /// older catalog that no longer resolves as stored is re-validated first (its choices against the current rules).
    /// </summary>
    private async Task<(StackResolution? Stack, DomainError? Error)> ResolveAsync(ReleaseVersion version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version.StackComposition))
        {
            return (null, null);
        }

        var pinned = await compositions.ForVersionAsync(version, cancellationToken);
        if (pinned.Composition is not { } composition)
        {
            return (null, null);
        }

        if (stacks.Catalog is not { } shipped)
        {
            return (null, new DomainError(ErrorCodes.Validation, "The stack catalog failed to load, so the version's composition can't be resolved."));
        }

        var resolved = StackResolver.Resolve(composition, shipped, pinned.Change, pinned.Baseline);
        if (resolved.IsFailure)
        {
            var revalidated = StackCompatibility.Validate(StackPinning.ToSelection(composition), shipped);
            if (revalidated.Composition is { } current)
            {
                resolved = StackResolver.Resolve(current, shipped, pinned.Change, pinned.Baseline);
            }
        }

        if (resolved.IsFailure)
        {
            LogUnresolvable(logger, version.Id, resolved.Error!.Message);
            return (null, resolved.Error);
        }

        return (resolved.Value, null);
    }

    /// <summary>FR-118, FR-128: the stack detector over the base commit's files and manifests (detection never blocks).</summary>
    private async Task<StackDetectionReport?> DetectAsync(ReleaseVersion version, IReadOnlyList<string> paths, StackComposition composition, CancellationToken cancellationToken)
    {
        try
        {
            var (files, _) = RepoFiles.ByNormalisedPath(paths.Select(p => (p, p)), StackDetectionService.MaxFiles);
            var wanted = StackDetector.ManifestsToRead(files.Keys).Where(files.ContainsKey).ToList();
            var read = await workingCopies.ReadTextAtCommitAsync(version.ProjectId, version.BaseCommitSha!, [.. wanted.Select(w => files[w])], StackDetector.MaxManifestLength, cancellationToken);
            var manifests = wanted.Where(w => read.ContainsKey(files[w])).ToDictionary(w => w, w => read[files[w]], StringComparer.Ordinal);
            var detection = StackDetector.Detect(files.Keys, manifests, StackDetectionService.VcsHost);
            return StackAnalysis.Report(detection, composition);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            LogBaseUnreadable(logger, version.Id, ex.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The base commit of release version {ReleaseVersionId} could not be listed ({ErrorType}); preset rules infer existing files from the plan")]
    private static partial void LogBaseUnreadable(ILogger logger, Guid releaseVersionId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stack composition of release version {ReleaseVersionId} can't be resolved against the shipped catalog: {Reason}")]
    private static partial void LogUnresolvable(ILogger logger, Guid releaseVersionId, string reason);
}
