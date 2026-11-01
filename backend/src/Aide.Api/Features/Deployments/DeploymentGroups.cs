using Aide.Api.Features.Databases;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// FR-190: a group's status from its parts (<see cref="DeploymentGroupRules.StatusOf"/>), written with its events on each
/// transition. Parts change from several jobs (the group's <c>deployment_setup</c>, each database's
/// <c>database_provisioning</c>), so the write that follows a part's own save holds the project lock.
/// </summary>
internal static class DeploymentGroups
{
    /// <summary>Recomputes <paramref name="group"/> (tracked) and adds its event on a transition to ready or error. The caller saves.</summary>
    public static async Task ApplyStatusAsync(AideDbContext db, DeploymentGroup group, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var targets = await db.DeploymentTargets.Where(t => t.GroupId == group.Id).ToListAsync(cancellationToken);
        var databases = await db.ManagedDatabases.Where(d => d.GroupId == group.Id).ToListAsync(cancellationToken);
        var states = targets.Select(t => DeploymentGroupRules.StateOf(t.Status)).Concat(databases.Select(d => DeploymentGroupRules.StateOf(d.Status))).ToList();
        var before = group.Status;
        group.Status = DeploymentGroupRules.StatusOf(states);
        group.UpdatedAt = now;
        var failedTarget = targets.FirstOrDefault(t => t.Status == DeploymentTargetStatus.Error)?.StatusReason;
        var code = group.Status != DeploymentGroupStatus.Error ? null
            : failedTarget ?? (group.StatusReason is { } reason && reason != DeploymentGroupRules.PlacementUnavailable ? reason : ErrorCodes.DatabaseProvisioningFailed);
        group.StatusReason = code ?? (group.StatusReason == DeploymentGroupRules.PlacementUnavailable ? group.StatusReason : null);
        if (before == group.Status)
        {
            return;
        }

        if (group.Status == DeploymentGroupStatus.Ready)
        {
            db.AddEvent(
                EventTypes.DeploymentGroupConfigured,
                group.ProjectId,
                new { groupId = group.Id, provider = group.Provider.ToWire(), status = group.Status.ToWire(), sharedResourceId = group.SharedResourceId },
                actor,
                now);
        }
        else if (group.Status == DeploymentGroupStatus.Error)
        {
            db.AddEvent(
                EventTypes.DeploymentGroupFailed,
                group.ProjectId,
                new { groupId = group.Id, provider = group.Provider.ToWire(), status = group.Status.ToWire(), errorCode = code },
                actor,
                now);
        }
    }

    /// <summary>Recomputes a group after a part was saved, under the project lock (saved). Does nothing without a group.</summary>
    public static async Task RefreshAsync(AideDbContext db, Guid projectId, Guid? groupId, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (groupId is not { } id)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await Projects.ProjectLocks.LockProjectAsync(db, projectId, cancellationToken);
        var group = await db.DeploymentGroups.FirstOrDefaultAsync(g => g.Id == id && g.ProjectId == projectId, cancellationToken);
        if (group is null)
        {
            return;
        }

        await db.Entry(group).ReloadAsync(cancellationToken);
        await ApplyStatusAsync(db, group, actor, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>FR-190: the group's databases whose job was never queued fail with the group's setup. The caller saves.</summary>
    public static async Task FailUnqueuedDatabasesAsync(
        AideDbContext db, ManagedDatabaseSecrets secrets, Guid groupId, string code, string message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var row in await db.ManagedDatabases.Where(d => d.GroupId == groupId && d.Status == ManagedDatabaseStatus.Provisioning).ToListAsync(cancellationToken))
        {
            var connection = secrets.Connection(row);
            if (connection.JobId is null)
            {
                row.Status = ManagedDatabaseStatus.Error;
                row.UpdatedAt = now;
                secrets.SetConnection(row, connection with { Step = code, ErrorMessage = message });
                DatabaseEvents.ProvisioningFailed(db, row, code, now);
            }
        }
    }

    /// <summary>The database provider a group of <paramref name="provider"/> uses; <c>null</c> where a group has no database (Vercel).</summary>
    public static ManagedDatabaseProvider DatabaseProviderOf(DeploymentProvider provider) => provider switch
    {
        DeploymentProvider.Railway => ManagedDatabaseProvider.Railway,
        DeploymentProvider.Render => ManagedDatabaseProvider.Render,
        _ => ManagedDatabaseProvider.Fake,
    };

    /// <summary>The platform of a database provider, as groups see it.</summary>
    public static DeploymentProvider? PlatformOf(ManagedDatabaseProvider provider) => provider switch
    {
        ManagedDatabaseProvider.Railway => DeploymentProvider.Railway,
        ManagedDatabaseProvider.Render => DeploymentProvider.Render,
        ManagedDatabaseProvider.Fake => DeploymentProvider.Fake,
        _ => null,
    };
}
