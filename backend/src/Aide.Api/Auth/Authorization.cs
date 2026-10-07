using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Auth;

/// <summary>§2: Admin-only routes. The role is read from the database per request (<see cref="SystemRoleLookup"/>).</summary>
public sealed class AdminRequirement : IAuthorizationRequirement;

internal sealed class AdminRequirementHandler(SystemRoleLookup roles, IHttpContextAccessor accessor)
    : AuthorizationHandler<AdminRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (context.User.GetUserId() is { } userId
            && await roles.GetAsync(userId, accessor.HttpContext?.RequestAborted ?? CancellationToken.None) == SystemRole.Admin)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>The caller's standing on a project after an access check.</summary>
public sealed record ProjectAccessGrant(Guid UserId, SystemRole SystemRole, ProjectRole? ProjectRole);

/// <summary>
/// Project-scoped authorization (§2, D9): loads the caller's project role and applies the domain access matrix
/// (<see cref="ProjectPermissions.Check"/>). Non-members get <c>NOT_FOUND</c> (Admins see every project),
/// members below the required role get <c>FORBIDDEN</c>.
/// </summary>
public interface IProjectAccess
{
    Task<Result<ProjectAccessGrant>> CheckAsync(Guid projectId, ProjectAction action, CancellationToken cancellationToken);
}

internal sealed class ProjectAccess(AideDbContext db, ICurrentUser currentUser, SystemRoleLookup roles) : IProjectAccess
{
    public async Task<Result<ProjectAccessGrant>> CheckAsync(Guid projectId, ProjectAction action, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Result.Failure<ProjectAccessGrant>(new DomainError(ErrorCodes.Unauthenticated, "Sign in to continue."));
        }

        var systemRole = await roles.GetAsync(userId, cancellationToken) ?? SystemRole.User;
        var membership = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.UserId == userId)
            .Select(m => (ProjectRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);

        // Admins pass the matrix for any project id, so check that the project exists.
        if (systemRole == SystemRole.Admin && membership is null
            && !await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            return NotFound();
        }

        return ProjectPermissions.Check(systemRole, membership, action) switch
        {
            AccessDecision.Allowed => Result.Success(new ProjectAccessGrant(userId, systemRole, membership)),
            AccessDecision.Forbidden => Result.Failure<ProjectAccessGrant>(
                new DomainError(ErrorCodes.Forbidden, "Your project role doesn't allow this action.")),
            _ => NotFound(),
        };
    }

    private static Result<ProjectAccessGrant> NotFound() =>
        Result.Failure<ProjectAccessGrant>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
}
