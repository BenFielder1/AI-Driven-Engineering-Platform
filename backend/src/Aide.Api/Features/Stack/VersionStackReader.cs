using System.Text.Json;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Stack;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Stack;

/// <summary>
/// FR-117: the stack a release version runs with — its pinned composition (<c>null</c>: no stack, or a legacy preset) —
/// and its <see cref="StackChange"/> against the stack its base code was built with. The spec and plan generators read
/// <see cref="StackChange.IsStackChange"/> (FR-123, FR-124, M26).
/// </summary>
public sealed record VersionComposition(ReleaseVersion Version, StackComposition? Composition, StackComposition? Baseline, StackChange Change);

/// <summary>
/// Reads the <see cref="VersionComposition"/> of a release version. The <b>baseline</b> (the stack of the version's base code) is
/// the composition of the released or imported version it builds on (same commit); without one (a first version on a
/// repository with code: an import or FR-86), the composition the version started with — the <c>from</c> of its first
/// <c>STACK_CHANGED</c> event, or its current composition when it never changed.
/// </summary>
public sealed class VersionStackReader(AideDbContext db)
{
    /// <summary>The project's current release version's stack (<c>null</c>: no such project or version).</summary>
    public async Task<VersionComposition?> CurrentAsync(Guid projectId, CancellationToken cancellationToken) =>
        await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken) is { } id
            ? await ForVersionAsync(id, cancellationToken)
            : null;

    /// <summary>The stack of release version <paramref name="releaseVersionId"/> (<c>null</c>: no such version).</summary>
    public async Task<VersionComposition?> ForVersionAsync(Guid releaseVersionId, CancellationToken cancellationToken)
    {
        var version = await db.ReleaseVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == releaseVersionId, cancellationToken);
        return version is null ? null : await ForVersionAsync(version, cancellationToken);
    }

    /// <summary>The stack of <paramref name="version"/>.</summary>
    public async Task<VersionComposition> ForVersionAsync(ReleaseVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        var composition = StackCompositionJson.Deserialize(version.StackComposition);
        var baseHasCode = !string.IsNullOrWhiteSpace(version.BaseCommitSha);
        if (!baseHasCode || composition is null)
        {
            return new VersionComposition(version, composition, null, new StackChange(baseHasCode, []));
        }

        var baseVersion = await db.ReleaseVersions.AsNoTracking()
            .Where(v => v.ProjectId == version.ProjectId && v.Id != version.Id && v.MergeCommitSha == version.BaseCommitSha)
            .Where(v => v.Status == ReleaseVersionStatus.Released || v.Status == ReleaseVersionStatus.Imported)
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => v.StackComposition)
            .FirstOrDefaultAsync(cancellationToken);
        var baseline = StackCompositionJson.Deserialize(baseVersion) ?? await StartedWithAsync(version, composition, cancellationToken);
        return new VersionComposition(version, composition, baseline, StackPinning.Change(baseHasCode, baseline, composition));
    }

    /// <summary>The composition <paramref name="version"/> started with: the <c>from</c> of its first <c>STACK_CHANGED</c>, else <paramref name="current"/>.</summary>
    private async Task<StackComposition?> StartedWithAsync(ReleaseVersion version, StackComposition current, CancellationToken cancellationToken)
    {
        var payloads = await db.Events.AsNoTracking()
            .Where(e => e.ProjectId == version.ProjectId && e.Type == EventTypes.StackChanged)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Select(e => e.Payload)
            .ToListAsync(cancellationToken);
        var versionId = version.Id.ToString();
        foreach (var payload in payloads)
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.TryGetProperty("releaseVersionId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == versionId)
            {
                return root.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object
                    ? StackCompositionJson.Deserialize(from.GetRawText())
                    : null;
            }
        }

        return current;
    }
}
