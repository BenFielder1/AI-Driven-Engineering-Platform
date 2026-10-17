namespace Aide.Domain.Access;

public enum SystemRole
{
    User = 0,
    Admin = 1,
}

public enum ProjectRole
{
    Viewer = 0,
    Contributor = 1,
    Owner = 2,
}

/// <summary>Project-scoped actions from the §2 permission table.</summary>
public enum ProjectAction
{
    View = 0,
    RunAgents = 1,
    ControlExecution = 2,
    CreateDraft = 3,
    TriggerReview = 4,
    AdvancePhase = 5,
    DecideGate = 6,
    DecideArtifact = 7,
    RequestRelease = 8,
    Rollback = 9,
    ChangeSettings = 10,
    ManageMembers = 11,
    DeleteProject = 12,

    /// <summary>FR-60: start a new release version.</summary>
    StartVersion = 13,

    /// <summary>FR-90: trigger (or redeploy) a preview deployment.</summary>
    TriggerDeployment = 14,

    /// <summary>FR-88, FR-91: deployment settings and env vars, promote and production redeploy.</summary>
    ManageDeployments = 15,
}

public enum AccessDecision
{
    Allowed = 0,

    /// <summary>A member without the required role: 403.</summary>
    Forbidden = 1,

    /// <summary>Not a member (and not Admin): 404, so the project's existence isn't revealed.</summary>
    NotFound = 2,
}
