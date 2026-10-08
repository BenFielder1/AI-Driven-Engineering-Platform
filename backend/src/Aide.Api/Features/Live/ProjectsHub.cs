using System.Text.Json.Nodes;
using Aide.Api.Auth;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Live;

/// <summary>
/// A project event pushed live to everyone viewing the project (FR-46): the SignalR <c>projectEvent</c> message.
/// <c>id</c>, <c>type</c>, <c>payload</c> and <c>createdAt</c> are the stored audit event (FR-50); <c>category</c> tells the
/// page which data to refresh. Not an HTTP response: the schema is added to the OpenAPI components by
/// <see cref="LiveContractDocumentTransformer"/> so the generated client has the type.
/// </summary>
public sealed record ProjectEventMessage(
    Guid Id,
    Guid ProjectId,
    string Type,
    EventCategory Category,
    JsonObject Payload,
    DateTimeOffset CreatedAt);

/// <summary>Server-to-client messages of <see cref="ProjectsHub"/>.</summary>
public interface IProjectsHubClient
{
    /// <summary>The <c>projectEvent</c> message (sent by its wire name, <see cref="ProjectsHub.ProjectEventMethod"/>).</summary>
    Task ProjectEvent(ProjectEventMessage message);
}

/// <summary>
/// FR-46 live updates (§6 <c>WS /api/hubs/projects</c>, D20). A signed-in client calls <c>Join(projectId)</c> for the
/// project it is viewing and <c>Leave(projectId)</c> when it navigates away; non-members (and unknown projects) get a
/// <c>FORBIDDEN</c> hub error on join, using the same access matrix as the HTTP API (Admins may view any project).
/// Members of the project's group receive <c>projectEvent</c> messages (<see cref="SignalRProjectEventPublisher"/>).
/// </summary>
[Authorize]
public sealed class ProjectsHub(AideDbContext db, SystemRoleLookup roles, ProjectViewerRegistry viewers) : Hub<IProjectsHubClient>
{
    /// <summary>The hub route.</summary>
    public const string Path = "/api/hubs/projects";

    /// <summary>The client method name of <see cref="IProjectsHubClient.ProjectEvent"/>.</summary>
    public const string ProjectEventMethod = "projectEvent";

    /// <summary>The hub error message for a join the caller isn't allowed to make.</summary>
    public const string ForbiddenError = ErrorCodes.Forbidden;

    /// <summary>The SignalR group for a project's viewers.</summary>
    public static string GroupFor(Guid projectId) => $"project:{projectId:D}";

    /// <summary>Starts receiving the project's events; a <c>FORBIDDEN</c> hub error for non-members.</summary>
    /// <remarks>
    /// The connection is subscribed (group + viewer registry) <em>before</em> membership is checked, and unsubscribed
    /// if the check fails. A removal that commits before the check is seen by it; one that commits after it finds the
    /// registration and drops it (<see cref="SignalRProjectEventPublisher"/>). Either way no subscription survives a
    /// concurrent <c>MEMBER_REMOVED</c>; at worst the removed user sees events committed between the two.
    /// </remarks>
    public async Task Join(Guid projectId)
    {
        var cancellationToken = Context.ConnectionAborted;
        if (Context.User?.GetUserId() is not { } userId)
        {
            throw new HubException(ForbiddenError);
        }

        var group = GroupFor(projectId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group, cancellationToken);
        viewers.Add(Context.ConnectionId, userId, projectId);

        bool allowed;
        try
        {
            allowed = await CanViewAsync(projectId, userId, cancellationToken);
        }
        catch
        {
            await UnsubscribeAsync(projectId);
            throw;
        }

        if (!allowed)
        {
            await UnsubscribeAsync(projectId);
            throw new HubException(ForbiddenError);
        }
    }

    /// <summary>Stops receiving the project's events.</summary>
    public Task Leave(Guid projectId) => UnsubscribeAsync(projectId);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        viewers.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private async Task UnsubscribeAsync(Guid projectId)
    {
        viewers.Remove(Context.ConnectionId, projectId);
        // Not tied to the connection's token: an unsubscribe must complete even while the connection is closing.
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(projectId), CancellationToken.None);
    }

    private async Task<bool> CanViewAsync(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            return false;
        }

        var systemRole = await roles.GetAsync(userId, cancellationToken);
        if (systemRole is null)
        {
            return false;
        }

        var membership = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.UserId == userId)
            .Select(m => (ProjectRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);
        return ProjectPermissions.Check(systemRole.Value, membership, ProjectAction.View) == AccessDecision.Allowed;
    }
}
