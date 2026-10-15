namespace Aide.Domain.Access;

/// <summary>§2 role capability matrix, as a pure function.</summary>
public static class ProjectPermissions
{
    /// <summary>The lowest project role that may perform <paramref name="action"/>.</summary>
    public static ProjectRole MinimumRole(ProjectAction action) => action switch
    {
        ProjectAction.View => ProjectRole.Viewer,
        ProjectAction.RunAgents
            or ProjectAction.ControlExecution
            or ProjectAction.CreateDraft
            or ProjectAction.TriggerReview
            or ProjectAction.AdvancePhase => ProjectRole.Contributor,
        ProjectAction.DecideGate
            or ProjectAction.DecideArtifact
            or ProjectAction.RequestRelease
            or ProjectAction.Rollback
            or ProjectAction.ChangeSettings
            or ProjectAction.ManageMembers
            or ProjectAction.DeleteProject
            or ProjectAction.StartVersion => ProjectRole.Owner,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>
    /// Admins may do everything on every project. Non-members get <see cref="AccessDecision.NotFound"/>
    /// (404, the project isn't revealed); members below the required role get <see cref="AccessDecision.Forbidden"/>.
    /// </summary>
    public static AccessDecision Check(SystemRole systemRole, ProjectRole? membership, ProjectAction action)
    {
        if (systemRole == SystemRole.Admin)
        {
            return AccessDecision.Allowed;
        }

        if (membership is not { } role)
        {
            return AccessDecision.NotFound;
        }

        return role >= MinimumRole(action) ? AccessDecision.Allowed : AccessDecision.Forbidden;
    }
}
