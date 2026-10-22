using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Projects;

/// <summary>
/// FR-79: reading and clearing a project's LLM binding. Clearing happens in the caller's unit of work (the same
/// transaction as the role change, removal or connection deletion that causes it) and records
/// <c>LLM_BINDING_CLEARED</c>; the "LLM binding cleared" notification type for Owners arrives with FR-92.
/// </summary>
internal static class LlmBindings
{
    /// <summary>The binding of <paramref name="settings"/> as members see it, or <c>null</c> when nothing is bound.</summary>
    public static async Task<LlmBindingDto?> LoadAsync(AideDbContext db, ProjectLlmSettings settings, CancellationToken cancellationToken)
    {
        if (settings.CredentialId is not { } credentialId)
        {
            return null;
        }

        var row = await (
                from c in db.LlmCredentials.AsNoTracking()
                join u in db.Users.AsNoTracking() on c.UserId equals u.Id
                where c.Id == credentialId
                select new { c.Id, c.Provider, c.Label, c.Last4, c.Status, UserId = u.Id, u.Name })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null
            ? null
            : new LlmBindingDto(row.Id, row.Provider, row.Label, row.Last4, row.Status, new LlmBindingUserDto(row.UserId, row.Name));
    }

    /// <summary>Whether the caller is an Owner of the project (only Owners bind or unbind, FR-79).</summary>
    public static bool CanBind(ProjectRole? callerRole) => callerRole == ProjectRole.Owner;

    /// <summary>Clears the binding of <paramref name="settings"/> and records why (the caller saves).</summary>
    public static void Clear(AideDbContext db, ProjectLlmSettings settings, LlmBindingClearedReason reason, string actor, DateTimeOffset now)
    {
        var credentialId = settings.CredentialId;
        var boundByUserId = settings.BoundByUserId;
        settings.CredentialId = null;
        settings.BoundByUserId = null;
        db.AddEvent(
            EventTypes.LlmBindingCleared,
            settings.ProjectId,
            new { projectId = settings.ProjectId, credentialId, boundByUserId, provider = settings.Provider.ToWire(), reason = reason.ToWire() },
            actor,
            now);
    }

    /// <summary>
    /// Clears the project's binding when <paramref name="memberUserId"/> bound it and stops being an Owner
    /// (<paramref name="newRole"/>) or leaves (<paramref name="newRole"/> <c>null</c>). Returns whether it was cleared.
    /// </summary>
    public static async Task<bool> ClearForMemberAsync(
        AideDbContext db,
        Guid projectId,
        Guid memberUserId,
        ProjectRole? newRole,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var settings = await db.ProjectLlmSettings.FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        if (settings is null)
        {
            return false;
        }

        var clears = newRole is { } role
            ? LlmConnectionRules.RoleChangeClearsBinding(settings.BoundByUserId, memberUserId, role)
            : LlmConnectionRules.RemovalClearsBinding(settings.BoundByUserId, memberUserId);
        if (!clears)
        {
            return false;
        }

        Clear(db, settings, newRole is null ? LlmBindingClearedReason.MemberRemoved : LlmBindingClearedReason.OwnerRoleLost, actor, now);
        return true;
    }
}

/// <summary>Row locks that serialise project-level changes within a transaction.</summary>
internal static class ProjectLocks
{
    /// <summary>
    /// Row lock on the project for the current transaction: serialises membership changes (FR-8) and LLM binding
    /// (FR-79) per project, so a demotion or removal and a bind by the same Owner can't interleave.
    /// </summary>
    public static Task<int> LockProjectAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT 1 FROM projects WHERE id = {projectId} FOR UPDATE", cancellationToken);
}
