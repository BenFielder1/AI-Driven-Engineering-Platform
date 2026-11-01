using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Infrastructure.Git;

/// <summary>
/// FR-136: clearing a GitLab or Bitbucket project's binding (the binding Owner's connection). Clearing happens in the
/// caller's unit of work (the same transaction as the role change, removal, connection deletion or invalidation that
/// causes it) and records <c>GIT_BINDING_CLEARED</c>, which notifies the project's Owners (<c>git_binding_cleared</c>).
/// Git jobs then fail with <c>GIT_NOT_CONNECTED</c> until an Owner re-binds their own connection in Settings → Git.
/// </summary>
public static class GitBindings
{
    /// <summary>Clears the binding of <paramref name="settings"/> (tracked) and records why; the caller saves.</summary>
    public static void Clear(AideDbContext db, ProjectGitSettings settings, GitBindingClearedReason reason, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(settings);
        var gitConnectionId = settings.GitConnectionId;
        var boundByUserId = settings.BoundByUserId;
        settings.GitConnectionId = null;
        settings.BoundByUserId = null;
        db.AddEvent(
            EventTypes.GitBindingCleared,
            settings.ProjectId,
            new { projectId = settings.ProjectId, gitConnectionId, boundByUserId, provider = settings.Provider.ToWire(), reason = reason.ToWire() },
            actor,
            now);
    }

    /// <summary>
    /// FR-136: clears every project binding to <paramref name="connectionId"/> (deleted or invalid). Returns the projects
    /// whose binding was cleared.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ClearForConnectionAsync(
        AideDbContext db, Guid connectionId, GitBindingClearedReason reason, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var bound = await db.ProjectGitSettings.Where(s => s.GitConnectionId == connectionId).ToListAsync(cancellationToken);
        var cleared = new List<Guid>();
        foreach (var settings in bound.Where(s => GitBindingRules.ConnectionChangeClearsBinding(s.GitConnectionId, connectionId)))
        {
            Clear(db, settings, reason, actor, now);
            cleared.Add(settings.ProjectId);
        }

        return cleared;
    }

    /// <summary>
    /// FR-136: clears the project's binding when <paramref name="memberUserId"/> bound it and stops being an Owner
    /// (<paramref name="newRole"/>) or leaves (<paramref name="newRole"/> <c>null</c>). Returns whether it was cleared.
    /// </summary>
    public static async Task<bool> ClearForMemberAsync(
        AideDbContext db, Guid projectId, Guid memberUserId, ProjectRole? newRole, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var settings = await db.ProjectGitSettings.FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        if (settings?.GitConnectionId is null)
        {
            return false;
        }

        var clears = newRole is { } role
            ? GitBindingRules.RoleChangeClearsBinding(settings.BoundByUserId, memberUserId, role)
            : GitBindingRules.RemovalClearsBinding(settings.BoundByUserId, memberUserId);
        if (!clears)
        {
            return false;
        }

        Clear(db, settings, newRole is null ? GitBindingClearedReason.MemberRemoved : GitBindingClearedReason.OwnerRoleLost, actor, now);
        return true;
    }
}
