using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>FR-90, FR-91: deployments as members see them (version text and "triggered by &lt;name&gt;").</summary>
public static class DeploymentReader
{
    /// <summary>The project's deployments, newest first, optionally of one target and/or release version.</summary>
    public static async Task<IReadOnlyList<DeploymentDto>> ListAsync(
        AideDbContext db, Guid projectId, DeploymentEnvironment? target, Guid? releaseVersionId, CancellationToken cancellationToken, int? take = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.Deployments.AsNoTracking()
            .Where(d => d.ProjectId == projectId && (target == null || d.Environment == target) && (releaseVersionId == null || d.ReleaseVersionId == releaseVersionId))
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id);
        var rows = await (take is { } n ? query.Take(n) : query).ToListAsync(cancellationToken);
        return await ToDtosAsync(db, rows, cancellationToken);
    }

    /// <summary>One deployment of the project, or <c>null</c>.</summary>
    public static async Task<DeploymentDto?> FindAsync(AideDbContext db, Guid projectId, Guid deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deploymentId && d.ProjectId == projectId, cancellationToken);
        return row is null ? null : (await ToDtosAsync(db, [row], cancellationToken))[0];
    }

    /// <summary>FR-90, FR-91: the project's newest deployment of any target (project state), or <c>null</c>.</summary>
    public static async Task<DeploymentDto?> LatestAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        var latest = await ListAsync(db, projectId, null, null, cancellationToken, take: 1);
        return latest.Count == 0 ? null : latest[0];
    }

    /// <summary>FR-91: the newest production deployment of each release attempt (redeploys included).</summary>
    public static async Task<IReadOnlyDictionary<Guid, DeploymentDto>> ProductionByReleaseAsync(
        AideDbContext db, IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(releaseIds);
        if (releaseIds.Count == 0)
        {
            return new Dictionary<Guid, DeploymentDto>();
        }

        var rows = await db.Deployments.AsNoTracking()
            .Where(d => d.ReleaseId != null && releaseIds.Contains(d.ReleaseId.Value) && d.Environment == DeploymentEnvironment.Production)
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .ToListAsync(cancellationToken);
        var newest = rows.GroupBy(d => d.ReleaseId!.Value).Select(g => g.First()).ToList();
        var dtos = await ToDtosAsync(db, newest, cancellationToken);
        return dtos.ToDictionary(d => d.ReleaseId!.Value);
    }

    public static async Task<IReadOnlyList<DeploymentDto>> ToDtosAsync(AideDbContext db, IReadOnlyList<Deployment> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return [];
        }

        var versionIds = rows.Select(r => r.ReleaseVersionId).Distinct().ToList();
        var versions = await db.ReleaseVersions.AsNoTracking()
            .Where(v => versionIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Version })
            .ToDictionaryAsync(v => v.Id, v => v.Version, cancellationToken);
        var userIds = rows.Where(r => r.TriggeredBy != null).Select(r => r.TriggeredBy!.Value).Distinct().ToList();
        var users = userIds.Count == 0
            ? []
            : await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.Name }).ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken);
        return
        [
            .. rows.Select(r => ToDto(
                r,
                versions.GetValueOrDefault(r.ReleaseVersionId) ?? string.Empty,
                r.TriggeredBy is { } by && users.TryGetValue(by, out var name) ? new DeploymentUserDto(by, name) : null)),
        ];
    }

    public static DeploymentDto ToDto(Deployment d, string releaseVersion, DeploymentUserDto? triggeredBy)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new DeploymentDto(
            d.Id,
            d.Environment,
            d.Provider,
            d.Status,
            d.Url,
            d.InspectorUrl,
            d.GitRef,
            d.GitSha,
            d.ReleaseVersionId,
            releaseVersion,
            d.ReleaseId,
            d.ErrorCode,
            d.ErrorMessage,
            d.BuildLogTail,
            d.Trigger,
            triggeredBy,
            d.PromotedFromDeploymentId,
            d.CreatedAt,
            d.ReadyAt);
    }
}
