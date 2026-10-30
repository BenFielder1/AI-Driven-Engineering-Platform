using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>FR-90, FR-91: deployments as members see them (version text and "triggered by &lt;name&gt;").</summary>
public static class DeploymentReader
{
    /// <summary>
    /// FR-151: the project's deployments, newest first, optionally of one environment, component and/or release version.
    /// </summary>
    public static async Task<IReadOnlyList<DeploymentDto>> ListAsync(
        AideDbContext db,
        Guid projectId,
        DeploymentEnvironment? target,
        Guid? releaseVersionId,
        CancellationToken cancellationToken,
        int? take = null,
        DeploymentComponent? component = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.Deployments.AsNoTracking()
            .Where(d => d.ProjectId == projectId
                && (target == null || d.Environment == target)
                && (component == null || d.Component == component)
                && (releaseVersionId == null || d.ReleaseVersionId == releaseVersionId))
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id);
        var rows = await (take is { } n ? query.Take(n) : query).ToListAsync(cancellationToken);
        return await ToDtosAsync(db, rows, cancellationToken);
    }

    /// <summary>
    /// FR-151: the deployment production serves now for each component of the project: the newest ready production row
    /// (deploy, redeploy, promote or rollback). Untracked.
    /// </summary>
    public static async Task<IReadOnlyDictionary<DeploymentComponent, Deployment>> CurrentProductionAsync(
        AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.Deployments.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.Environment == DeploymentEnvironment.Production && d.Status == DeploymentStatus.Ready)
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .ToListAsync(cancellationToken);
        return rows.GroupBy(d => d.Component).ToDictionary(g => g.Key, g => g.First());
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

    /// <summary>FR-151: the production URL of each component that has a target, in component order.</summary>
    public static async Task<IReadOnlyList<ComponentProductionUrlDto>> ProductionUrlsAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var targets = await db.DeploymentTargets.AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Select(t => new { t.Component, t.Provider, t.ProductionUrl })
            .ToListAsync(cancellationToken);
        if (targets.Count == 0)
        {
            return [];
        }

        // FR-151: frontend first, then backend (explicit, not the stored string's sort order).
        targets = [.. targets.OrderBy(t => t.Component == DeploymentComponent.Frontend ? 0 : 1).ThenBy(t => t.Component.ToString(), StringComparer.Ordinal)];

        var current = await CurrentProductionAsync(db, projectId, cancellationToken);
        return
        [
            .. targets.Select(t => current.TryGetValue(t.Component, out var d)
                ? new ComponentProductionUrlDto(t.Component, t.Provider, t.ProductionUrl ?? d.Url, d.Id)
                : new ComponentProductionUrlDto(t.Component, t.Provider, t.ProductionUrl, null)),
        ];
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
        var projectIds = rows.Select(r => r.ProjectId).Distinct().ToList();
        var targets = await db.DeploymentTargets.AsNoTracking()
            .Where(t => projectIds.Contains(t.ProjectId))
            .Select(t => new { t.Id, t.ProjectId, t.Component, t.Provider })
            .ToListAsync(cancellationToken);
        var current = new Dictionary<Guid, IReadOnlyDictionary<DeploymentComponent, Deployment>>();
        foreach (var projectId in projectIds)
        {
            current[projectId] = await CurrentProductionAsync(db, projectId, cancellationToken);
        }

        return
        [
            .. rows.Select(r =>
            {
                // FR-151: the platform is the target's host (rows made in Fake mode record provider fake).
                var platform = targets.FirstOrDefault(t => t.Id == r.TargetId)?.Provider
                    ?? targets.FirstOrDefault(t => t.ProjectId == r.ProjectId && t.Component == r.Component)?.Provider
                    ?? r.Provider;
                var serving = current[r.ProjectId].GetValueOrDefault(r.Component);
                return ToDto(
                    r,
                    versions.GetValueOrDefault(r.ReleaseVersionId) ?? string.Empty,
                    r.TriggeredBy is { } by && users.TryGetValue(by, out var name) ? new DeploymentUserDto(by, name) : null,
                    platform,
                    serving?.Id == r.Id,
                    ServesProduction(r, serving));
            }),
        ];
    }

    /// <summary>
    /// FR-150, FR-151: whether production serves <paramref name="row"/>'s platform deployment now: it is the current row, or
    /// another production row of the component pointing at the same platform deployment (the original of a rollback row).
    /// </summary>
    public static bool ServesProduction(Deployment row, Deployment? serving)
    {
        ArgumentNullException.ThrowIfNull(row);
        return serving is not null
            && (serving.Id == row.Id
                || (row.Environment == DeploymentEnvironment.Production
                    && row.Component == serving.Component
                    && !string.IsNullOrEmpty(row.ProviderDeploymentId)
                    && row.ProviderDeploymentId == serving.ProviderDeploymentId));
    }

    /// <summary>
    /// The DTO of <paramref name="d"/>. <paramref name="current"/>: it is the component's current production row (one per
    /// component). <paramref name="servesProduction"/>: production serves its platform deployment (<see cref="ServesProduction"/>;
    /// defaults to <paramref name="current"/>), so it is blocked from rollback with reason <c>current</c>.
    /// </summary>
    public static DeploymentDto ToDto(Deployment d, string releaseVersion, DeploymentUserDto? triggeredBy, DeploymentProvider? platform = null, bool current = false, bool? servesProduction = null)
    {
        ArgumentNullException.ThrowIfNull(d);
        var host = platform ?? d.Provider;
        var blocked = DeploymentRollbackRules.BlockedReason(host, d.Environment, d.Status, d.ProviderDeploymentId, servesProduction ?? current);
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
            d.ReadyAt,
            d.Component,
            d.Kind,
            host,
            current,
            blocked is null,
            blocked,
            DeploymentRollbackRules.OlderThanLatestMigration(d.CreatedAt, null));
    }
}
