using Aide.Api.Jobs;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Stack;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Stack;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Stack;

/// <summary>
/// FR-120–FR-128, §4.18: the stack a release version's agents run with, and what its rules need from the repository:
/// whether it is empty (no base commit, or only starter files: scaffold first) and the files on the base commit (immutable
/// <c>existingOnBase</c> paths; <c>null</c> when unknown). One of two modes (the v2 presets were retired by FR-129):
/// <list type="bullet">
/// <item><b>composition</b> (<see cref="Stack"/>): the version's pinned composition resolved against the shipped catalog
/// (<see cref="StackResolver.Resolve"/>, with its <see cref="StackChange"/> and baseline). When it can't be resolved
/// (the catalog no longer has a pinned option, or the catalog failed to load) <see cref="StackError"/> says why and the
/// agent jobs fail with it;</item>
/// <item><b>general</b> (no composition): "No stack (general)", v1 behaviour with the <c>generalist</c>. Nothing is read from Git.</item>
/// </list>
/// FR-128: <see cref="Detection"/> is the stack detector's report at the base commit (asked for with <c>detect</c>).
/// §4.23: <see cref="Legacy"/> is set for a version migrated from a v2 preset (its <c>preset_id</c> is kept as history), so
/// its in-flight plans may still name v2 agent keys.
/// </summary>
public sealed record VersionStackContext(ReleaseVersion? Version, bool RepoEmpty, IReadOnlySet<string>? BaseFiles)
{
    public static VersionStackContext None { get; } = new(null, RepoEmpty: true, BaseFiles: null);

    /// <summary>FR-120–FR-127: the version's resolved composition, or <c>null</c> (no stack, or unresolvable).</summary>
    public StackResolution? Stack { get; init; }

    /// <summary>Why the version's pinned composition couldn't be resolved, or <c>null</c>.</summary>
    public DomainError? StackError { get; init; }

    /// <summary>
    /// Whether <see cref="StackError"/> is a server-side failure (the stack catalog failed to load: the <c>stack-catalog</c>
    /// readiness check is unhealthy) rather than a problem with the version's composition. Jobs then fail transiently
    /// (retried, then dead-lettered) without an error code, never as <c>VALIDATION</c>.
    /// </summary>
    public bool CatalogUnavailable { get; init; }

    /// <summary>FR-128: the detector's report at the base commit (with <c>detect</c>, a composition and a base commit), or <c>null</c>.</summary>
    public StackDetectionReport? Detection { get; init; }

    /// <summary>§4.23: the v2 agent-key map and frozen preset globs of a version migrated from a preset, or <c>null</c>.</summary>
    public LegacyPlanAgents? Legacy { get; init; }

    /// <summary>The agents and rules plan validation uses for a plan of <paramref name="type"/> (FR-124, §4.23).</summary>
    public PlanAgents PlanAgents(ArtifactType type)
    {
        if (Stack is not { } stack)
        {
            return Domain.Execution.PlanAgents.None;
        }

        var agents = type == ArtifactType.OptimizationPlan
            ? Domain.Execution.PlanAgents.ForStackOptimization(stack, BaseFiles)
            : Domain.Execution.PlanAgents.ForStack(stack, RepoEmpty, BaseFiles);
        return agents with { Legacy = Legacy };
    }

    /// <summary>Whether <paramref name="path"/> exists on the base commit (unknown: <c>false</c>).</summary>
    public bool ExistsOnBase(string path) => BaseFiles?.Contains(path) ?? false;

    /// <summary>
    /// An agent job of a version whose composition can't be resolved fails with <see cref="StackError"/>: non-transient with
    /// its code, or (<see cref="CatalogUnavailable"/>) transiently without one, so it is retried and never blamed on the input.
    /// </summary>
    public VersionStackContext ThrowIfUnresolvable() => StackError is { } error
        ? throw (CatalogUnavailable
            ? new JobFailedException(error.Message, transient: true)
            : new JobFailedException(error.Message, transient: false, error.Code))
        : this;
}

/// <summary>Reads the <see cref="VersionStackContext"/> of a release version, or of a project's current one.</summary>
public sealed partial class VersionStackLoader(
    AideDbContext db,
    IStackCatalog stacks,
    VersionStackReader compositions,
    IWorkingCopies workingCopies,
    Agents.AgentRepoContext repoContext,
    ILogger<VersionStackLoader> logger)
{
    /// <summary>The most base-commit paths read (beyond it the base counts as unknown).</summary>
    public const int MaxBaseFiles = 500_000;

    /// <summary>The project's current release version's stack context (see <see cref="ForVersionAsync(ReleaseVersion, CancellationToken, bool, bool)"/>).</summary>
    public async Task<VersionStackContext> CurrentAsync(Guid projectId, CancellationToken cancellationToken, bool readBase = true, bool detect = false) =>
        await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken) is { } id
            ? await ForVersionAsync(id, cancellationToken, readBase, detect)
            : VersionStackContext.None;

    /// <summary>
    /// The stack context of release version <paramref name="releaseVersionId"/>. Without <paramref name="readBase"/> the
    /// base commit isn't listed (<see cref="VersionStackContext.BaseFiles"/> unknown; empty only without a base commit).
    /// </summary>
    public async Task<VersionStackContext> ForVersionAsync(Guid releaseVersionId, CancellationToken cancellationToken, bool readBase = true, bool detect = false)
    {
        var version = await db.ReleaseVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == releaseVersionId, cancellationToken);
        return version is null ? VersionStackContext.None : await ForVersionAsync(version, cancellationToken, readBase, detect);
    }

    /// <summary>
    /// The stack context of <paramref name="version"/>. A version without a composition reads nothing from Git (v1 rules
    /// need no base listing); detection (<paramref name="detect"/>) needs <paramref name="readBase"/>.
    /// </summary>
    public async Task<VersionStackContext> ForVersionAsync(ReleaseVersion version, CancellationToken cancellationToken, bool readBase = true, bool detect = false)
    {
        ArgumentNullException.ThrowIfNull(version);
        var (stack, stackError, catalogUnavailable) = await ResolveAsync(version, cancellationToken);
        var legacy = stack is not null && !string.IsNullOrWhiteSpace(version.PresetId) && stacks.Catalog is { } shipped
            ? new LegacyPlanAgents(shipped.Legacy, shipped.Legacy.Find(version.PresetId))
            : null;
        if (stack is null)
        {
            return new VersionStackContext(version, RepoEmpty: string.IsNullOrWhiteSpace(version.BaseCommitSha), BaseFiles: null)
            {
                StackError = stackError,
                CatalogUnavailable = catalogUnavailable,
            };
        }

        if (string.IsNullOrWhiteSpace(version.BaseCommitSha))
        {
            return new VersionStackContext(version, RepoEmpty: true, BaseFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase)) { Stack = stack, Legacy = legacy };
        }

        if (!readBase)
        {
            return new VersionStackContext(version, RepoEmpty: false, BaseFiles: null) { Stack = stack, Legacy = legacy };
        }

        IReadOnlyList<string>? paths = null;
        try
        {
            paths = await workingCopies.ListPathsAtCommitAsync(version.ProjectId, version.BaseCommitSha, MaxBaseFiles, cancellationToken);

            // A hosted commit not in the working copy yet (e.g. planning before execution): fetch it, then list again.
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
            return new VersionStackContext(version, RepoEmpty: false, BaseFiles: null) { Stack = stack, Legacy = legacy };
        }

        var detection = detect ? await DetectAsync(version, paths, stack.Composition, cancellationToken) : null;
        return new VersionStackContext(version, StackPathRules.IsStarterOnly(paths), paths.Select(PathRules.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase))
        {
            Stack = stack,
            Detection = detection,
            Legacy = legacy,
        };
    }

    /// <summary>
    /// FR-120: the version's pinned composition resolved against the shipped catalog. A composition validated against an
    /// older catalog that no longer resolves as stored is re-validated first (its choices against the current rules).
    /// </summary>
    private async Task<(StackResolution? Stack, DomainError? Error, bool CatalogUnavailable)> ResolveAsync(ReleaseVersion version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version.StackComposition))
        {
            return (null, null, false);
        }

        var pinned = await compositions.ForVersionAsync(version, cancellationToken);
        if (pinned.Composition is not { } composition)
        {
            return (null, null, false);
        }

        // A server-side failure (FR-122: the readiness check reports it), not a problem with the version's composition.
        if (stacks.Catalog is not { } shipped)
        {
            LogCatalogUnavailable(logger, version.Id);
            return (null, new DomainError(ErrorCodes.StackIncompatible, "The stack catalog failed to load on this server, so the version's composition can't be resolved; an administrator checks Admin → Stack catalog."), true);
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
            return (null, resolved.Error, false);
        }

        return (resolved.Value, null, false);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The base commit of release version {ReleaseVersionId} could not be listed ({ErrorType}); stack rules infer existing files from the plan")]
    private static partial void LogBaseUnreadable(ILogger logger, Guid releaseVersionId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stack composition of release version {ReleaseVersionId} can't be resolved against the shipped catalog: {Reason}")]
    private static partial void LogUnresolvable(ILogger logger, Guid releaseVersionId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The stack catalog failed to load, so release version {ReleaseVersionId} can't resolve its composition")]
    private static partial void LogCatalogUnavailable(ILogger logger, Guid releaseVersionId);
}
