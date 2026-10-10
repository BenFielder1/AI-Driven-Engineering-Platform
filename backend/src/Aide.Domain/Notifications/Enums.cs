namespace Aide.Domain.Notifications;

/// <summary>
/// The kind of an in-app notification (FR-47, FR-15). The project channel events (<c>NotificationEvent</c>) plus
/// <c>gate_reminder</c>, the 24-hour reminder of a pending gate. Wire values are snake_case.
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
}
