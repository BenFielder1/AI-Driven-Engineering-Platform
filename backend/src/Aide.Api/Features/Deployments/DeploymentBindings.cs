using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// FR-88 (as FR-79 for LLM connections): a project's Vercel binding goes with the Owner who bound it — when they stop being
/// an Owner, leave, or delete the connection. Cleared in the caller's unit of work with <c>SETTINGS_UPDATED</c>
/// (<c>section: deployment</c>, <c>bindingCleared</c>); deployment settings, env vars and history stay. The Owners'
/// "binding cleared" notification arrives with FR-92.
/// </summary>
internal static class DeploymentBindings
{
    public const string OwnerRoleLost = "owner_role_lost";
    public const string MemberRemoved = "member_removed";
    public const string ConnectionDeleted = "connection_deleted";

    private static readonly string[] ConnectionChange = ["connection"];

    public static void Clear(AideDbContext db, ProjectDeploymentSettings settings, string reason, string actor, DateTimeOffset now)
    {
        var connectionId = settings.ConnectionId;
        var boundByUserId = settings.BoundByUserId;
        settings.ConnectionId = null;
        settings.BoundByUserId = null;
        settings.UpdatedAt = now;
        db.AddEvent(
            EventTypes.SettingsUpdated,
            settings.ProjectId,
            new { projectId = settings.ProjectId, section = "deployment", changes = ConnectionChange, bindingCleared = true, connectionId, boundByUserId, reason },
            actor,
            now);
    }

    /// <summary>Clears the binding when <paramref name="memberUserId"/> bound it and is no longer an Owner (or left).</summary>
    public static async Task<bool> ClearForMemberAsync(
        AideDbContext db, Guid projectId, Guid memberUserId, ProjectRole? newRole, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = await db.ProjectDeploymentSettings.FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        if (settings is null)
        {
            return false;
        }

        var clears = newRole is { } role
            ? DeploymentSettingsRules.RoleChangeClearsBinding(settings.BoundByUserId, memberUserId, role)
            : DeploymentSettingsRules.RemovalClearsBinding(settings.BoundByUserId, memberUserId);
        if (!clears)
        {
            return false;
        }

        Clear(db, settings, newRole is null ? MemberRemoved : OwnerRoleLost, actor, now);
        return true;
    }
}
