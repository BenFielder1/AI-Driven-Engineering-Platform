using Aide.Api.Features.Databases;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Stack;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>FR-191: the group cards of Settings → Deployment: what the composition groups, joined with the stored groups and their parts.</summary>
public sealed class DeploymentGroupReader(AideDbContext db, DeploymentAccess access)
{
    private const string NotConfigured = "not_configured";

    public async Task<IReadOnlyList<DeploymentGroupDto>> ListAsync(Guid projectId, StackComposition? composition, bool canBind, CancellationToken cancellationToken)
    {
        var groups = await db.DeploymentGroups.AsNoTracking().Where(g => g.ProjectId == projectId).ToListAsync(cancellationToken);
        var composed = DeploymentGrouping.Groups(composition);
        var providers = composed.Select(g => g.Provider)
            .Concat(groups.Select(g => g.Provider))
            .Distinct()
            .Where(p => p != DeploymentProvider.Fake || groups.Any(g => g.Provider == p))
            .ToList();
        if (providers.Count == 0)
        {
            return [];
        }

        var targets = await db.DeploymentTargets.AsNoTracking().Where(t => t.ProjectId == projectId).ToListAsync(cancellationToken);
        var databases = await db.ManagedDatabases.AsNoTracking().Where(d => d.ProjectId == projectId).ToListAsync(cancellationToken);
        var cards = new List<DeploymentGroupDto>();
        foreach (var provider in providers)
        {
            var row = groups.FirstOrDefault(g => g.Provider == provider);
            var spec = DeploymentGroupRules.Spec(composition, provider, access.UsesFake);
            var parts = spec?.Parts ?? PartsOf(row!, targets, databases);
            var states = PartStatuses(parts, row, targets, databases);
            cards.Add(new DeploymentGroupDto(
                row?.Id,
                provider,
                spec?.Capability ?? DeploymentGrouping.Capability(provider),
                parts,
                row?.Name,
                row is null ? null : await BindingAsync(row, cancellationToken),
                canBind,
                row?.SharedResourceId,
                DeploymentGroupRules.SharedProjectUrl(provider, row?.SharedResourceId),
                row?.Region,
                row?.Plan,
                row?.Status.ToWire() ?? NotConfigured,
                row?.StatusReason,
                states,
                canBind && row is not null && row.Status != DeploymentGroupStatus.Configuring && CanRetry(row.Id, targets, databases),
                row?.UpdatedAt));
        }

        return cards;
    }

    private static List<DeploymentPart> PartsOf(DeploymentGroup group, List<DeploymentTarget> targets, List<ManagedDatabase> databases)
    {
        var parts = targets.Where(t => t.GroupId == group.Id).Select(t => t.Component == DeploymentComponent.Frontend ? DeploymentPart.Frontend : DeploymentPart.Backend).ToList();
        if (databases.Any(d => d.GroupId == group.Id))
        {
            parts.Add(DeploymentPart.Database);
        }

        return [.. parts.Order()];
    }

    /// <summary>FR-190 Retry: a current member failed. A part that left the group (FR-191) isn't one, and is never set up again by a retry.</summary>
    internal static bool CanRetry(Guid groupId, IReadOnlyList<DeploymentTarget> targets, IReadOnlyList<ManagedDatabase> databases) =>
        targets.Any(t => t.GroupId == groupId && DeploymentGroupRules.Retries(t.Status))
        || databases.Any(d => d.GroupId == groupId && DeploymentGroupRules.Retries(d.Status));

    internal static DeploymentComponent ComponentOf(DeploymentPart part) => part == DeploymentPart.Frontend ? DeploymentComponent.Frontend : DeploymentComponent.Backend;

    private static List<DeploymentGroupPartDto> PartStatuses(IReadOnlyList<DeploymentPart> parts, DeploymentGroup? group, List<DeploymentTarget> targets, List<ManagedDatabase> databases)
    {
        var statuses = new List<DeploymentGroupPartDto>();
        foreach (var part in parts)
        {
            if (part != DeploymentPart.Database)
            {
                var target = group is null ? null : targets.FirstOrDefault(t => t.Component == ComponentOf(part) && t.GroupId == group.Id);
                statuses.Add(new DeploymentGroupPartDto(part, null, target?.Status.ToWire() ?? NotConfigured, target?.StatusReason, target?.ResourceId));
                continue;
            }

            foreach (var environment in new[] { DeploymentEnvironment.Production, DeploymentEnvironment.Preview })
            {
                var row = group is null ? null : databases.FirstOrDefault(d => d.Environment == environment && d.GroupId == group.Id && !ManagedDatabaseService.Unlinked(d));
                statuses.Add(new DeploymentGroupPartDto(part, environment, row?.Status.ToWire() ?? NotConfigured, null, row?.ResourceId));
            }
        }

        return statuses;
    }

    private async Task<DeploymentTargetBindingDto?> BindingAsync(DeploymentGroup group, CancellationToken cancellationToken)
    {
        if (group.ConnectionId is not { } id)
        {
            return null;
        }

        var connection = await db.PlatformConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        var user = connection is null ? null
            : await db.Users.AsNoTracking().Where(u => u.Id == connection.UserId).Select(u => new { u.Id, u.Name }).FirstOrDefaultAsync(cancellationToken);
        return connection is null || user is null ? null
            : new DeploymentTargetBindingDto(connection.Id, connection.Label, connection.Last4, connection.Metadata.TeamSlug ?? connection.AccountName, new DeploymentUserDto(user.Id, user.Name), connection.Status);
    }
}
