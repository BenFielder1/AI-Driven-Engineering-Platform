using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Projects;

namespace Aide.Domain.GitHosts;

/// <summary>Why a project's Git binding was cleared (FR-136, notified as <c>git_binding_cleared</c>). Wire values are snake_case.</summary>
public enum GitBindingClearedReason
{
    /// <summary>The binding Owner's role changed to Contributor or Viewer.</summary>
    OwnerRoleLost = 0,

    /// <summary>The binding Owner left or was removed from the project.</summary>
    MemberRemoved = 1,

    /// <summary>The binding Owner deleted the connection.</summary>
    ConnectionDeleted = 2,

    /// <summary>The host rejected the connection (401 after one refresh): it was marked <c>invalid</c>.</summary>
    ConnectionInvalid = 3,
}

/// <summary>A user's GitLab or Bitbucket connection as the binding rules see it (no secret).</summary>
public sealed record GitConnectionRef(Guid Id, Guid UserId, Guid InstanceId, bool Valid);

/// <summary>
/// FR-136: the binding of a GitLab or Bitbucket project to its binding Owner's connection, which all background Git work
/// uses. The binding is cleared when that Owner loses the role or leaves, or the connection is deleted or invalid; Git jobs
/// then fail with <c>GIT_NOT_CONNECTED</c>, and another Owner re-binds their own valid connection to the same instance
/// once AIDE has checked it has developer/write access to the repo. Pure.
/// </summary>
public static class GitBindingRules
{
    /// <summary>FR-136: a role change clears the binding when the binding Owner stops being an Owner.</summary>
    public static bool RoleChangeClearsBinding(Guid? boundByUserId, Guid memberUserId, ProjectRole newRole) =>
        LlmConnectionRules.RoleChangeClearsBinding(boundByUserId, memberUserId, newRole);

    /// <summary>FR-136: removing (or leaving) clears the binding when the member is the binding Owner.</summary>
    public static bool RemovalClearsBinding(Guid? boundByUserId, Guid memberUserId) =>
        LlmConnectionRules.RemovalClearsBinding(boundByUserId, memberUserId);

    /// <summary>
    /// FR-136: a connection that stops being valid — <c>invalid</c> (rejected, expired, revoked) or
    /// <c>insufficient_scope</c> (build decision: treated as an invalidation) — clears every binding to it.
    /// </summary>
    public static bool StatusChangeClearsBinding(Entities.GitConnectionStatus previous, Entities.GitConnectionStatus next) =>
        previous == Entities.GitConnectionStatus.Valid && next != Entities.GitConnectionStatus.Valid;

    /// <summary>FR-136: deleting or invalidating a connection clears every binding to it.</summary>
    public static bool ConnectionChangeClearsBinding(Guid? boundConnectionId, Guid connectionId) => boundConnectionId == connectionId;

    /// <summary>
    /// FR-136: whether a Git job can run with the project's binding: a bound, valid connection on an enabled instance;
    /// otherwise <c>GIT_NOT_CONNECTED</c>.
    /// </summary>
    public static Result CanRunGitJob(GitConnectionRef? boundConnection, bool instanceEnabled, string hostName)
    {
        ArgumentNullException.ThrowIfNull(hostName);
        if (boundConnection is null)
        {
            return NotConnected($"No {hostName} connection is bound to the project. An Owner can re-bind their own connection in Settings → Git.");
        }

        if (!instanceEnabled)
        {
            return NotConnected($"The {hostName} instance is disabled.");
        }

        return boundConnection.Valid
            ? Result.Success()
            : NotConnected($"The bound {hostName} connection is invalid. Its owner can reconnect, or another Owner can re-bind theirs.");
    }

    /// <summary>
    /// FR-136: whether <paramref name="callerId"/> may bind <paramref name="connection"/> to a project on
    /// <paramref name="projectInstanceId"/>: only an Owner (<c>FORBIDDEN</c>), an existing connection (<c>NOT_FOUND</c>),
    /// their own (<c>CREDENTIAL_NOT_OWNER</c>), on the project's instance (<c>VALIDATION</c> on <c>gitConnectionId</c>),
    /// valid (<c>GIT_NOT_CONNECTED</c>), with developer/write access to the repo (<c>FORBIDDEN</c>; <c>null</c>: not
    /// checked yet, so not allowed).
    /// </summary>
    public static Result CanRebind(
        Guid callerId, ProjectRole? callerRole, GitConnectionRef? connection, Guid projectInstanceId, bool? hasWriteAccess, string hostName)
    {
        ArgumentNullException.ThrowIfNull(hostName);
        if (callerRole != ProjectRole.Owner)
        {
            return Result.Failure(new DomainError(ErrorCodes.Forbidden, "Only a project Owner can bind a Git connection."));
        }

        if (connection is null)
        {
            return Result.Failure(new DomainError(ErrorCodes.NotFound, "The connection was not found."));
        }

        if (connection.UserId != callerId)
        {
            return Result.Failure(new DomainError(ErrorCodes.CredentialNotOwner, "You can only bind your own connections."));
        }

        if (connection.InstanceId != projectInstanceId)
        {
            return Result.Failure(new DomainError(ErrorCodes.Validation, "The Git settings are invalid.")
            {
                Details = [$"gitConnectionId: The connection is for a different {hostName} instance than the project's repository."],
            });
        }

        if (!connection.Valid)
        {
            return NotConnected($"The {hostName} connection is invalid. Reconnect it under Account → Connections, then bind it.");
        }

        return hasWriteAccess == true
            ? Result.Success()
            : Result.Failure(new DomainError(ErrorCodes.Forbidden, $"Your {hostName} account needs developer (write) access to the repository to bind it."));
    }

    /// <summary>FR-136, §4.19: GitLab developer access (access level 30 or more: push, MR, tags).</summary>
    public static bool GitLabHasDeveloperAccess(int? accessLevel) => accessLevel >= GitLabMergeReadiness.DeveloperAccess;

    /// <summary>FR-136: Bitbucket write access (repository permission <c>write</c> or <c>admin</c>).</summary>
    public static bool BitbucketHasWriteAccess(string? permission) => permission is "write" or "admin";

    private static Result NotConnected(string message) => Result.Failure(new DomainError(ErrorCodes.GitNotConnected, message));
}
