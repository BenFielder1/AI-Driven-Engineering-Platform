using Aide.Domain.Common;
using Aide.Domain.Gates;

namespace Aide.Domain.Notifications;

/// <summary>Who receives an in-app notification of a project (FR-47).</summary>
public enum RecipientScope
{
    /// <summary>The project's Owners: they decide gates, approve artifacts and act on failures.</summary>
    Owners = 0,

    /// <summary>Every member (Owner, Contributor, Viewer): outcomes everyone on the project wants to know about.</summary>
    AllMembers = 1,

    /// <summary>
    /// FR-92: one user — the owner of the connection the event names (<c>userId</c> in its payload). Personal: in-app and
    /// email to the user's account address only, never a project channel.
    /// </summary>
    ConnectionOwner = 2,
}

/// <summary>
/// Payload of a <c>notification</c> job (FR-47, FR-15): the logical notification <c>&lt;type&gt;:&lt;sourceId&gt;</c> of a
/// project. The source is the gate for gate notifications (required, reminder, overdue) and the audit event otherwise.
/// </summary>
/// <remarks><see cref="ProjectId"/> is <c>null</c> only for personal notifications of events outside any project (a GitHub
/// connection marked invalid, FR-92).</remarks>
public sealed record NotificationJobPayload(Guid? ProjectId, NotificationType Type, Guid SourceId);

/// <summary>FR-47, FR-15, §2: which events notify, whom, through which channel subscription, and under which key.</summary>
public static class NotificationRules
{
    /// <summary>The notification an audit event raises, or <c>null</c> when it raises none.</summary>
    public static NotificationType? ForEvent(string eventType) => eventType switch
    {
        EventTypes.GateCreated => NotificationType.GateRequired,
        EventTypes.GateOverdue => NotificationType.GateOverdue,
        EventTypes.ArtifactCreated or EventTypes.ArtifactRestored => NotificationType.ArtifactReady,
        EventTypes.StepBlocked => NotificationType.StepBlocked,
        EventTypes.RunFailed => NotificationType.RunFailed,
        EventTypes.BudgetExceeded => NotificationType.BudgetExceeded,
        EventTypes.ReviewCompleted => NotificationType.ReviewCompleted,
        EventTypes.ReleaseCompleted => NotificationType.ReleaseCompleted,
        EventTypes.ReleaseFailed => NotificationType.ReleaseFailed,
        EventTypes.ReleaseAwaitingMerge => NotificationType.ReleaseAwaitingMerge,
        EventTypes.RollbackCompleted => NotificationType.Rollback,
        EventTypes.DeploymentFailed => NotificationType.DeploymentFailed,
        EventTypes.DeploymentPreviewReady => NotificationType.PreviewReady,
        EventTypes.ConnectionInvalid => NotificationType.ConnectionInvalid,
        EventTypes.LlmBindingCleared => NotificationType.LlmBindingCleared,
        EventTypes.GitBindingCleared => NotificationType.GitBindingCleared,
        _ => null,
    };

    /// <summary>Gate notifications are keyed by the gate (one per gate), the others by the event that raised them.</summary>
    public static bool IsAboutGate(NotificationType type) =>
        type is NotificationType.GateRequired or NotificationType.GateReminder or NotificationType.GateOverdue;

    /// <summary>
    /// Recipients: Owners for what needs an Owner's action (gates, drafts to approve, blocked steps, failed runs, budget,
    /// failed releases, releases awaiting a merge, failed deployments, cleared LLM and Git bindings); every member for outcomes
    /// (review completed, release completed, rollback, preview ready); the connection's owner alone for an invalid connection.
    /// </summary>
    public static RecipientScope Recipients(NotificationType type) => type switch
    {
        NotificationType.ReviewCompleted or NotificationType.ReleaseCompleted or NotificationType.Rollback
            or NotificationType.PreviewReady => RecipientScope.AllMembers,
        NotificationType.ConnectionInvalid => RecipientScope.ConnectionOwner,
        _ => RecipientScope.Owners,
    };

    /// <summary>FR-92: personal notifications go to one user (in-app and their account email), never to project channels.</summary>
    public static bool IsPersonal(NotificationType type) => Recipients(type) == RecipientScope.ConnectionOwner;

    /// <summary>
    /// The channel event (a project channel's <c>events</c> entry) that subscribes to <paramref name="type"/>, or <c>null</c>
    /// for personal types, which no project channel receives. Channels have no separate reminder event: a gate reminder
    /// goes to the channels subscribed to <c>gate_required</c>.
    /// </summary>
    public static string? ChannelEvent(NotificationType type) =>
        IsPersonal(type) ? null
        : type == NotificationType.GateReminder ? NotificationType.GateRequired.ToWire()
        : type.ToWire();

    /// <summary>The event types a project channel can subscribe to: every type but the reminder (it follows <c>gate_required</c>) and personal ones.</summary>
    public static IReadOnlyList<NotificationType> ChannelTypes { get; } =
        [.. Enum.GetValues<NotificationType>().Where(t => t != NotificationType.GateReminder && !IsPersonal(t))];

    /// <summary>The idempotency key of a logical notification: <c>&lt;type&gt;:&lt;source id&gt;</c>.</summary>
    public static string Key(NotificationType type, Guid sourceId) => $"{type.ToWire()}:{sourceId:D}";

    public static bool TryParseKey(string? key, out NotificationType type, out Guid sourceId)
    {
        type = default;
        sourceId = Guid.Empty;
        var separator = key?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        return separator > 0
            && EnumStrings.TryParse(key![..separator], out type)
            && Guid.TryParseExact(key[(separator + 1)..], "D", out sourceId);
    }

    /// <summary>Whether the user's preferences (§2) let <paramref name="type"/> into their notification centre.</summary>
    public static bool Wants(NotificationType type, IReadOnlyCollection<NotificationType>? mutedTypes) =>
        mutedTypes is null || !mutedTypes.Contains(type);

    /// <summary>
    /// FR-48: one-click approve/reject links exist only for spec, plan and pre-release gates. An escalation needs a decision
    /// (continue, rollback with a target, accept), so its notification links to the project page.
    /// </summary>
    public static bool OffersApprovalLinks(GateType type) =>
        type is GateType.SpecApproval or GateType.PlanApproval or GateType.PreRelease;

    /// <summary>
    /// The app page a notification opens (a relative path; never an approval token). <paramref name="artifactType"/> picks
    /// the artifact page of <c>artifact_ready</c>; <paramref name="deploymentTarget"/> picks the page that redeploys a failed
    /// deployment (production: Release; preview: Review). Personal and project-less notifications open <c>/connections</c>
    /// or the projects list.
    /// </summary>
    public static string LinkPath(NotificationType type, Guid? projectId, string? artifactType = null, string? deploymentTarget = null)
    {
        if (type == NotificationType.ConnectionInvalid)
        {
            return "/connections";
        }

        if (projectId is not { } id)
        {
            return "/projects";
        }

        return type switch
        {
            NotificationType.ArtifactReady when !string.IsNullOrEmpty(artifactType) => $"/projects/{id:D}/artifacts/{Uri.EscapeDataString(artifactType)}",
            NotificationType.StepBlocked => $"/projects/{id:D}/execution",
            NotificationType.RunFailed or NotificationType.BudgetExceeded => $"/projects/{id:D}/runs",
            NotificationType.ReviewCompleted or NotificationType.PreviewReady => $"/projects/{id:D}/review",
            NotificationType.DeploymentFailed when deploymentTarget == "preview" => $"/projects/{id:D}/review",
            NotificationType.ReleaseAwaitingMerge or NotificationType.DeploymentFailed => $"/projects/{id:D}/release",
            NotificationType.LlmBindingCleared => $"/projects/{id:D}/settings/llm",
            NotificationType.GitBindingCleared => $"/projects/{id:D}/settings/git",
            _ => $"/projects/{id:D}",
        };
    }

    /// <summary>
    /// Budget notifications are deduplicated per budget state: every refused start writes a <c>BUDGET_EXCEEDED</c> event
    /// (the audit trail), but only the first refusal since the project's token budget was last set notifies. A refusal
    /// notifies when there is no earlier refusal (<paramref name="previousRefusalAt"/> is <c>null</c>) or the budget was
    /// changed (raised, lowered or cleared) at or after the previous refusal. Used tokens are never reset, so the budget
    /// value is the only thing that changes the budget state.
    /// </summary>
    public static bool NotifiesBudgetExceeded(DateTimeOffset? previousRefusalAt, IEnumerable<DateTimeOffset> budgetChangesAt)
    {
        ArgumentNullException.ThrowIfNull(budgetChangesAt);
        return previousRefusalAt is not { } previous || budgetChangesAt.Any(changedAt => changedAt >= previous);
    }

    /// <summary>The notification types, for validating preference updates.</summary>
    public static IReadOnlyList<NotificationType> All { get; } = Enum.GetValues<NotificationType>();
}
