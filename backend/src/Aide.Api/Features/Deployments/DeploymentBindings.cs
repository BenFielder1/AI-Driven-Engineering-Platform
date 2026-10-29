using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// FR-88, FR-144 (as FR-79 for LLM connections): a deployment target's binding goes with the Owner who bound it — when
/// they stop being an Owner, leave, or delete the connection. Cleared in the caller's unit of work with
/// <c>SETTINGS_UPDATED</c> (<c>section: deployment</c>, <c>bindingCleared</c>, the <c>component</c>); the target (now
/// <c>attention</c> when its resource stays linked), env vars and history stay.
/// </summary>
internal static class DeploymentBindings
{
    public const string OwnerRoleLost = "owner_role_lost";
    public const string MemberRemoved = "member_removed";
    public const string ConnectionDeleted = "connection_deleted";

    private static readonly string[] ConnectionChange = ["connection"];

    public static void Clear(AideDbContext db, DeploymentTarget target, string reason, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        var connectionId = target.ConnectionId;
        var boundByUserId = target.BoundByUserId;
        target.ConnectionId = null;
        target.BoundByUserId = null;
        DeploymentTargetRules.Refresh(target);
        target.UpdatedAt = now;
        db.AddEvent(
            EventTypes.SettingsUpdated,
            target.ProjectId,
            new
            {
                projectId = target.ProjectId,
                section = "deployment",
                component = target.Component.ToWire(),
                changes = ConnectionChange,
                bindingCleared = true,
                connectionId,
                boundByUserId,
                reason,
            },
            actor,
            now);
    }

    /// <summary>Clears every target binding <paramref name="memberUserId"/> made when they are no longer an Owner (or left).</summary>
    public static async Task<bool> ClearForMemberAsync(
        AideDbContext db, Guid projectId, Guid memberUserId, ProjectRole? newRole, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var targets = await db.DeploymentTargets.Where(t => t.ProjectId == projectId && t.BoundByUserId == memberUserId).ToListAsync(cancellationToken);
        var cleared = false;
        foreach (var target in targets)
        {
            var clears = newRole is { } role
                ? DeploymentSettingsRules.RoleChangeClearsBinding(target.BoundByUserId, memberUserId, role)
                : DeploymentSettingsRules.RemovalClearsBinding(target.BoundByUserId, memberUserId);
            if (clears)
            {
                Clear(db, target, newRole is null ? MemberRemoved : OwnerRoleLost, actor, now);
                cleared = true;
            }
        }

        return cleared;
    }
}
