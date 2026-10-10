using System.ComponentModel.DataAnnotations;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Auth;
using Aide.Domain.Access;
using Aide.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Admin;

/// <summary>One page of results. <see cref="Page"/> is 1-based.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Limit, int Total);

/// <summary>A user as shown on the admin users page.</summary>
public sealed record AdminUserDto(
    Guid Id,
    string Email,
    string Name,
    SystemRole SystemRole,
    string? AvatarUrl,
    bool GithubLinked,
    DateTimeOffset CreatedAt);

public sealed record ChangeSystemRoleRequest
{
    public required SystemRole SystemRole { get; init; }
}

/// <summary>FR-3: Admins list users and change system roles (§6 <c>/api/admin/users</c>).</summary>
public static partial class AdminUserEndpoints
{
    public const int MaxLimit = 100;

    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users")
            .WithTags("Admin")
            .RequireAuthorization(AuthConstants.AdminPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", ListUsers)
            .WithName("ListUsers")
            .WithSummary("Users, newest first, optionally filtered by a search over email and name.")
            .ProducesValidationProblem();

        group.MapPatch("/{id:guid}/role", ChangeSystemRole)
            .WithName("ChangeUserSystemRole")
            .WithSummary("Change a user's system role (browser session only). The last Admin can't be demoted (409 CONFLICT).")
            .RequireAuthorization(AuthConstants.BrowserSessionPolicy)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<Ok<PagedResult<AdminUserDto>>> ListUsers(
        AideDbContext db,
        CancellationToken cancellationToken,
        [Range(1, int.MaxValue)] int page = 1,
        [Range(1, MaxLimit)] int limit = 20,
        [MaxLength(200)] string? search = null)
    {
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(u => EF.Functions.ILike(u.Email!, pattern, "\\") || EF.Functions.ILike(u.Name, pattern, "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(u => u.CreatedAt)
            .ThenByDescending(u => u.Id)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(u => new AdminUserDto(u.Id, u.Email!, u.Name, u.SystemRole, u.AvatarUrl, u.GithubId != null, u.CreatedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new PagedResult<AdminUserDto>(items, page, limit, total));
    }

    private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> ChangeSystemRole(
        Guid id,
        ChangeSystemRoleRequest request,
        AideDbContext db,
        AccountService accounts,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var requested = request.SystemRole;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await accounts.LockAccountsAsync(cancellationToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return ApiProblems.NotFound("The user was not found.");
        }

        var adminCount = await db.Users.CountAsync(u => u.SystemRole == SystemRole.Admin, cancellationToken);
        var change = SystemRoleRules.ChangeRole(user.SystemRole, requested, adminCount);
        if (change.IsFailure)
        {
            return ApiProblems.FromError(change.Error!);
        }

        if (user.SystemRole != requested)
        {
            var previous = user.SystemRole;
            user.SystemRole = requested;
            await db.SaveChangesAsync(cancellationToken);
            var logger = loggers.CreateLogger(typeof(AdminUserEndpoints));
            LogRoleChanged(logger, user.Id, previous, requested);
        }

        await transaction.CommitAsync(cancellationToken);
        // Roles are read per request (SystemRoleLookup), so the change applies to the user's next request.
        return TypedResults.Ok(UserDto.From(user));
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Information, Message = "System role of user {TargetUserId} changed from {PreviousRole} to {NewRole}")]
    private static partial void LogRoleChanged(ILogger logger, Guid targetUserId, SystemRole previousRole, SystemRole newRole);
}
