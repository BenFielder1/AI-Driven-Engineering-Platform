namespace Aide.Domain.Notifications;

/// <summary>
/// The kind of an in-app notification (FR-47, FR-15). The project channel events (<c>NotificationEvent</c>) plus
/// <c>gate_reminder</c>, the 24-hour reminder of a pending gate, and the personal <c>connection_invalid</c> (FR-92).
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
}
