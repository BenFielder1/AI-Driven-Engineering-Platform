using Aide.Domain.Common;

namespace Aide.Domain.Access;

/// <summary>FR-3: system roles. The first account becomes Admin; Admins change roles; the last Admin stays.</summary>
public static class SystemRoleRules
{
    /// <summary>The role for a new account: Admin when no account exists yet, otherwise User.</summary>
    public static SystemRole ForNewAccount(bool anyAccountExists) =>
        anyAccountExists ? SystemRole.User : SystemRole.Admin;

    /// <summary>
    /// Changing a user's system role. Demoting the only remaining Admin is refused (<see cref="ErrorCodes.Conflict"/>),
    /// so the system is never left without anyone able to manage roles.
    /// </summary>
    public static Result ChangeRole(SystemRole current, SystemRole requested, int adminCount)
    {
        if (current == SystemRole.Admin && requested != SystemRole.Admin && adminCount <= 1)
        {
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "The last Admin can't be demoted."));
        }

        return Result.Success();
    }
}
