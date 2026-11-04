namespace Aide.Domain.Notifications;

/// <summary>
/// The kind of an in-app notification (FR-47, FR-15). The project channel events (<c>NotificationEvent</c>) plus
/// <c>gate_reminder</c>, the 24-hour reminder of a pending gate, and the personal <c>connection_invalid</c> (FR-92; also the
/// FR-165 warning that a Jira or Linear token expires within 14 days).
/// Wire values are snake_case.
/// </summary>
public enum NotificationType
{
    GateRequired = 0,
    GateOverdue = 1,
    GateReminder = 2,
    ArtifactReady = 3,
    StepBlocked = 4,
    RunFailed = 5,
    BudgetExceeded = 6,
    ReviewCompleted = 7,
    ReleaseCompleted = 8,
    ReleaseFailed = 9,
    Rollback = 10,

    /// <summary>FR-65, FR-92: the release waits for a merge on GitHub (Owners; PR link and reasons).</summary>
    ReleaseAwaitingMerge = 11,

    /// <summary>FR-91, FR-92: a production deployment failed, or Vercel rejected the token (Owners; Redeploy link).</summary>
    DeploymentFailed = 12,

    /// <summary>FR-90, FR-92: the preview deployment is ready (every member; its URL).</summary>
    PreviewReady = 13,

    /// <summary>FR-78, FR-87, FR-92: a provider rejected a connection (the connection's owner only; personal).</summary>
    ConnectionInvalid = 14,

    /// <summary>FR-79, FR-92: the project's LLM binding was cleared (Owners; the reason).</summary>
    LlmBindingCleared = 15,

    /// <summary>FR-136: the project's Git binding (GitLab or Bitbucket connection) was cleared (Owners; the reason).</summary>
    GitBindingCleared = 16,

    /// <summary>FR-152, FR-155: setting up a database failed (Owners; the error code).</summary>
    DatabaseProvisioningFailed = 17,

    /// <summary>§4.21, FR-155: a Render free database expires within 7 days, or a Supabase project is paused (Owners; the date).</summary>
    DatabaseExpiring = 18,

    /// <summary>FR-153, FR-154, FR-155: a migration failed (Owners; the environment and error code).</summary>
    MigrationFailed = 19,

    /// <summary>FR-144, FR-155: the platform lost Git access, or the target's token is invalid (Owners; the component and platform).</summary>
    DeploymentTargetAttention = 20,

    /// <summary>FR-193: a <c>stack_profile</c> draft waits for review, or the Stack Analyst run failed (Owners; the Analysis page).</summary>
    StackProfileReady = 21,

    /// <summary>FR-193: a tracker import ended without a snapshot (Owners and the importer; the Refinement page).</summary>
    TrackerImportFailed = 22,
}
