using System.Text.Json.Serialization;

namespace Aide.Api.Features.Projects;

/// <summary>Events a notification channel can subscribe to (FR-47). Wire values are snake_case.</summary>
public enum NotificationEvent
{
    [JsonStringEnumMemberName("gate_required")]
    GateRequired = 0,

    [JsonStringEnumMemberName("gate_overdue")]
    GateOverdue = 1,

    [JsonStringEnumMemberName("artifact_ready")]
    ArtifactReady = 2,

    [JsonStringEnumMemberName("step_blocked")]
    StepBlocked = 3,

    [JsonStringEnumMemberName("run_failed")]
    RunFailed = 4,

    [JsonStringEnumMemberName("budget_exceeded")]
    BudgetExceeded = 5,

    [JsonStringEnumMemberName("review_completed")]
    ReviewCompleted = 6,

    [JsonStringEnumMemberName("release_completed")]
    ReleaseCompleted = 7,

    [JsonStringEnumMemberName("release_failed")]
    ReleaseFailed = 8,

    [JsonStringEnumMemberName("rollback")]
    Rollback = 9,
}
