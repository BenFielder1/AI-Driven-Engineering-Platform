using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Domain.Artifacts;

/// <summary>FR-352: a stored version of a project-scope artifact, as the pin rule sees it.</summary>
public sealed record ProjectArtifactVersion(string Key, Guid ArtifactId, int Version, ArtifactStatus Status);

/// <summary>FR-352, §4.76: pure rules for source-of-truth (project-scope) artifacts.</summary>
public static class ProjectArtifactRules
{
    /// <summary>The project-scope artifacts <paramref name="definition"/> declares, in declaration order.</summary>
    public static IReadOnlyList<ArtifactDefinition> ProjectScoped(PipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return [.. definition.Artifacts.Where(a => a.Scope == ArtifactScope.Project)];
    }

    /// <summary>
    /// §4.76: the pins of a new run: for each project-scope artifact the definition declares, its newest <b>locked</b>
    /// version (drafts and rejected versions are ignored, even when newer). The first declared artifact without a locked
    /// version gives 409 <c>SOURCE_ARTIFACT_MISSING</c>, naming it: the title as the ProblemDetails <c>reason</c> (the
    /// <c>{artifact}</c> in the copy) and the key in <c>details</c>.
    /// </summary>
    public static Result<IReadOnlyDictionary<string, PinnedArtifact>> Pins(PipelineDefinition definition, IEnumerable<ProjectArtifactVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var newestLocked = versions
            .Where(v => v.Status == ArtifactStatus.Locked)
            .GroupBy(v => v.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.MaxBy(v => v.Version)!, StringComparer.Ordinal);

        var pins = new Dictionary<string, PinnedArtifact>(StringComparer.Ordinal);
        foreach (var artifact in ProjectScoped(definition))
        {
            if (!newestLocked.TryGetValue(artifact.Key, out var version))
            {
                return Result.Failure<IReadOnlyDictionary<string, PinnedArtifact>>(Missing(artifact));
            }

            pins[artifact.Key] = new PinnedArtifact(version.ArtifactId, version.Version);
        }

        return Result.Success<IReadOnlyDictionary<string, PinnedArtifact>>(pins);
    }

    /// <summary>The next version number of a project-scope artifact: one more than the newest stored, or 1.</summary>
    public static int NextVersion(IEnumerable<int> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return stored.DefaultIfEmpty(0).Max() + 1;
    }

    /// <summary>
    /// Locking version <paramref name="version"/> (status <paramref name="status"/>) of a project-scope artifact whose newest
    /// locked version is <paramref name="newestLocked"/>: a draft locks (FR-17), a locked version is a no-op, any other
    /// status is <c>ARTIFACT_NOT_DRAFT</c>, and a draft older than the newest locked version is 409 <c>CONFLICT</c> (locking
    /// it would make runs pin an older source of truth).
    /// </summary>
    public static Result<ArtifactStatusChange> Lock(ArtifactStatus status, int version, int? newestLocked)
    {
        var change = ArtifactRules.Approve(status);
        if (change.IsSuccess && change.Value.Changed && newestLocked is { } newest && version < newest)
        {
            return Result.Failure<ArtifactStatusChange>(new DomainError(
                EngineErrorCodes.Conflict, $"Version {newest} is already locked; add a new version instead of locking version {version}."));
        }

        return change;
    }

    /// <summary>
    /// §4.10 for project scope: the open drafts a new version supersedes (every earlier draft of the same project and key).
    /// </summary>
    public static IReadOnlyList<int> SupersededBy(IEnumerable<(int Version, ArtifactStatus Status)> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return [.. stored.Where(v => v.Status == ArtifactStatus.Draft).Select(v => v.Version).Order()];
    }

    /// <summary>409 <c>SOURCE_ARTIFACT_MISSING</c> for <paramref name="artifact"/>.</summary>
    public static DomainError Missing(ArtifactDefinition artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new DomainError(EngineErrorCodes.SourceArtifactMissing, $"Lock a version of {artifact.Title} before starting a run.")
        {
            Reason = artifact.Title,
            Details = [artifact.Key],
        };
    }
}
