using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aide.Api.Features.Projects;

/// <summary>
/// FR-8: project members (§6 <c>/api/projects/:id/members</c>). Members list; Owners add, change roles and remove.
/// Any member may remove themselves (leave the project). The last Owner can't be demoted or removed (409 LAST_OWNER).
/// Role changes and removals lock the project row, so two Owners demoting each other can't leave it ownerless.
/// FR-79: when the member who bound the project's LLM connection stops being an Owner or leaves, the binding is cleared
/// in the same transaction.
/// </summary>
public static partial class ProjectMemberEndpoints
{
    public static IEndpointRouteBuilder MapProjectMemberEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}/members")
            .WithTags("Project members")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/", ListMembers)
            .WithName("ListMembers")
            .WithSummary("The project's members (members and Admins).")
            .Produces<List<MemberDto>>();

        group.MapPost("/", AddMember)
            .WithName("AddMember")
            .WithSummary("Add an existing user by email (Owner). 404 USER_NOT_FOUND for an unknown email, 409 CONFLICT if already a member.")
            .Produces<MemberDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPatch("/{userId:guid}", UpdateMemberRole)
            .WithName("UpdateMemberRole")
            .WithSummary("Change a member's role (Owner). The last Owner can't be demoted (409 LAST_OWNER).")
            .Produces<MemberDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{userId:guid}", RemoveMember)
            .WithName("RemoveMember")
            .WithSummary("Remove a member (Owner), or leave the project (any member removing themselves). The last Owner can't be removed (409 LAST_OWNER).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<Results<Ok<List<MemberDto>>, ProblemHttpResult>> ListMembers(
        Guid id,
        IProjectAccess access,
        AideDbContext db,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.View, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        var members = await MembersQuery(db, id).ToListAsync(cancellationToken);
        return TypedResults.Ok(members
            .OrderByDescending(m => m.Role)
            .ThenBy(m => m.CreatedAt)
            .ThenBy(m => m.UserId)
            .ToList());
    }

    private static async Task<Results<Created<MemberDto>, ValidationProblem, ProblemHttpResult>> AddMember(
        Guid id,
        AddMemberRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        UserManager<AppUser> users,
        AideDbContext db,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageMembers, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        // FindByEmailAsync compares the normalized email, so the lookup is case-insensitive.
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            return ApiProblems.Of(ErrorCodes.UserNotFound, "No account uses that email address.");
        }

        var current = await CurrentMembersAsync(db, id, cancellationToken);
        var added = MembershipRules.Add(current, user.Id, request.Role);
        if (added.IsFailure)
        {
            return ApiProblems.FromError(added.Error!);
        }

        var now = time.GetUtcNow();
        var membership = new ProjectMembership { ProjectId = id, UserId = user.Id, Role = request.Role, CreatedAt = now };
        db.ProjectMembers.Add(membership);
        db.AddEvent(EventTypes.MemberAdded, id, new { projectId = id, userId = user.Id, role = request.Role }, currentUser.Actor, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ApiProblems.Of(ErrorCodes.Conflict, "The user is already a member.");
        }

        var logger = loggers.CreateLogger(typeof(ProjectMemberEndpoints));
        LogMemberAdded(logger, id, user.Id, request.Role);
        return TypedResults.Created(
            $"/api/projects/{id}/members/{user.Id}",
            new MemberDto(user.Id, user.Email!, user.Name, user.AvatarUrl, membership.Role, membership.CreatedAt));
    }

    private static async Task<Results<Ok<MemberDto>, ValidationProblem, ProblemHttpResult>> UpdateMemberRole(
        Guid id,
        Guid userId,
        UpdateMemberRoleRequest request,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var grant = await access.CheckAsync(id, ProjectAction.ManageMembers, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ProjectLocks.LockProjectAsync(db, id, cancellationToken);

        var current = await CurrentMembersAsync(db, id, cancellationToken);
        var changed = MembershipRules.ChangeRole(current, userId, request.Role);
        if (changed.IsFailure)
        {
            return ApiProblems.FromError(changed.Error!);
        }

        var membership = await db.ProjectMembers.FirstAsync(m => m.ProjectId == id && m.UserId == userId, cancellationToken);
        var previous = membership.Role;
        if (previous != request.Role)
        {
            var now = time.GetUtcNow();
            membership.Role = request.Role;
            db.AddEvent(
                EventTypes.MemberRoleChanged,
                id,
                new { projectId = id, userId, from = previous, to = request.Role },
                currentUser.Actor,
                now);

            // FR-79: a binding Owner who is no longer an Owner takes their connection with them.
            await LlmBindings.ClearForMemberAsync(db, id, userId, request.Role, currentUser.Actor, now, cancellationToken);
            await Deployments.DeploymentBindings.ClearForMemberAsync(db, id, userId, request.Role, currentUser.Actor, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            var logger = loggers.CreateLogger(typeof(ProjectMemberEndpoints));
            LogRoleChanged(logger, id, userId, previous, request.Role);
        }

        await transaction.CommitAsync(cancellationToken);
        var dto = await MembersQuery(db, id, userId).FirstAsync(cancellationToken);
        return TypedResults.Ok(dto);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveMember(
        Guid id,
        Guid userId,
        IProjectAccess access,
        ICurrentUser currentUser,
        AideDbContext db,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        // Leaving (removing yourself) only needs membership; removing someone else is an Owner action.
        var self = currentUser.UserId == userId;
        var grant = await access.CheckAsync(id, self ? ProjectAction.View : ProjectAction.ManageMembers, cancellationToken);
        if (grant.IsFailure)
        {
            return ApiProblems.FromError(grant.Error!);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ProjectLocks.LockProjectAsync(db, id, cancellationToken);

        var current = await CurrentMembersAsync(db, id, cancellationToken);
        var removed = MembershipRules.Remove(current, userId);
        if (removed.IsFailure)
        {
            return ApiProblems.FromError(removed.Error!);
        }

        var deleted = await db.ProjectMembers.Where(m => m.ProjectId == id && m.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            var now = time.GetUtcNow();
            db.AddEvent(EventTypes.MemberRemoved, id, new { projectId = id, userId, self }, currentUser.Actor, now);

            // FR-79: so does a binding Owner who leaves or is removed.
            await LlmBindings.ClearForMemberAsync(db, id, userId, newRole: null, currentUser.Actor, now, cancellationToken);
            await Deployments.DeploymentBindings.ClearForMemberAsync(db, id, userId, newRole: null, currentUser.Actor, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var logger = loggers.CreateLogger(typeof(ProjectMemberEndpoints));
        LogMemberRemoved(logger, id, userId, self);
        return TypedResults.NoContent();
    }

    /// <summary>Members of a project (optionally one user). Filters go before the <c>MemberDto</c> projection: EF can't
    /// translate a predicate on a constructor-projected record.</summary>
    private static IQueryable<MemberDto> MembersQuery(AideDbContext db, Guid projectId, Guid? userId = null) =>
        from m in db.ProjectMembers.AsNoTracking()
        join u in db.Users.AsNoTracking() on m.UserId equals u.Id
        where m.ProjectId == projectId && (userId == null || m.UserId == userId)
        select new MemberDto(u.Id, u.Email!, u.Name, u.AvatarUrl, m.Role, m.CreatedAt);

    private static async Task<IReadOnlyList<ProjectMember>> CurrentMembersAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken) =>
        await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .Select(m => new ProjectMember(m.UserId, m.Role))
            .ToListAsync(cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {MemberUserId} added to project {ProjectId} as {Role}")]
    private static partial void LogMemberAdded(ILogger logger, Guid projectId, Guid memberUserId, ProjectRole role);

    [LoggerMessage(Level = LogLevel.Information, Message = "Role of user {MemberUserId} on project {ProjectId} changed from {PreviousRole} to {Role}")]
    private static partial void LogRoleChanged(ILogger logger, Guid projectId, Guid memberUserId, ProjectRole previousRole, ProjectRole role);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {MemberUserId} removed from project {ProjectId} (left: {Self})")]
    private static partial void LogMemberRemoved(ILogger logger, Guid projectId, Guid memberUserId, bool self);
}
