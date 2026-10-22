using System.Text.Json.Serialization;

namespace Aide.Api.Features.Projects;

/// <summary>
/// Events a notification channel can subscribe to (FR-47, FR-92). Wire values are snake_case and equal the notification
/// types (<c>NotificationRules.ChannelTypes</c>); the personal <c>connection_invalid</c> never goes to project channels.
/// </summary>
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

    /// <summary>FR-92: the release waits for a merge on GitHub.</summary>
    [JsonStringEnumMemberName("release_awaiting_merge")]
    ReleaseAwaitingMerge = 10,

    /// <summary>FR-92: a production deployment failed (or Vercel rejected the token).</summary>
    [JsonStringEnumMemberName("deployment_failed")]
    DeploymentFailed = 11,

    /// <summary>FR-92: the preview deployment is ready.</summary>
    [JsonStringEnumMemberName("preview_ready")]
    PreviewReady = 12,

    /// <summary>FR-92: the project's LLM binding was cleared.</summary>
    [JsonStringEnumMemberName("llm_binding_cleared")]
    LlmBindingCleared = 13,
}
