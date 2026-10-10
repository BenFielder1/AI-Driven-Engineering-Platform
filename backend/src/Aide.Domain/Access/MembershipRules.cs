using Aide.Domain.Common;

namespace Aide.Domain.Access;

public sealed record ProjectMember(Guid UserId, ProjectRole Role);

/// <summary>FR-8: member management. A project always has at least one Owner.</summary>
public static class MembershipRules
{
    public static Result<IReadOnlyList<ProjectMember>> Add(IReadOnlyList<ProjectMember> members, Guid userId, ProjectRole role)
    {
        ArgumentNullException.ThrowIfNull(members);
        return members.Any(m => m.UserId == userId)
            ? Result.Failure<IReadOnlyList<ProjectMember>>(new DomainError(ErrorCodes.Conflict, "The user is already a member."))
            : Result.Success<IReadOnlyList<ProjectMember>>([.. members, new ProjectMember(userId, role)]);
    }

    public static Result<IReadOnlyList<ProjectMember>> ChangeRole(IReadOnlyList<ProjectMember> members, Guid userId, ProjectRole role)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.FirstOrDefault(m => m.UserId == userId) is not { } member)
        {
            return NotMember();
        }

        if (member.Role == ProjectRole.Owner && role != ProjectRole.Owner && IsLastOwner(members, userId))
        {
            return LastOwner("demoted");
        }

        return Result.Success<IReadOnlyList<ProjectMember>>([.. members.Select(m => m.UserId == userId ? m with { Role = role } : m)]);
    }

    public static Result<IReadOnlyList<ProjectMember>> Remove(IReadOnlyList<ProjectMember> members, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.FirstOrDefault(m => m.UserId == userId) is not { } member)
        {
            return NotMember();
        }

        if (member.Role == ProjectRole.Owner && IsLastOwner(members, userId))
        {
            return LastOwner("removed");
        }

        return Result.Success<IReadOnlyList<ProjectMember>>([.. members.Where(m => m.UserId != userId)]);
    }

    private static bool IsLastOwner(IReadOnlyList<ProjectMember> members, Guid userId) =>
        !members.Any(m => m.UserId != userId && m.Role == ProjectRole.Owner);

    private static Result<IReadOnlyList<ProjectMember>> NotMember() =>
        Result.Failure<IReadOnlyList<ProjectMember>>(new DomainError(ErrorCodes.NotFound, "The user is not a member of this project."));

    private static Result<IReadOnlyList<ProjectMember>> LastOwner(string verb) =>
        Result.Failure<IReadOnlyList<ProjectMember>>(new DomainError(ErrorCodes.LastOwner, $"The last Owner can't be {verb}."));
}
