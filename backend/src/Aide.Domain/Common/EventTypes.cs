namespace Aide.Domain.Common;

/// <summary>Categories of project events, used to filter the activity feed (FR-50) and to group live updates (FR-46).</summary>
public enum EventCategory
{
    Transition = 0,
    Gate = 1,
    Artifact = 2,
    Run = 3,
    Step = 4,
    Review = 5,
    Release = 6,
    Rollback = 7,

    /// <summary>Project, member and settings changes (FR-5–FR-9).</summary>
    Project = 8,

    /// <summary>Integrity alerts for admins (FR-20): an artifact's content no longer matches its hash.</summary>
    Integrity = 9,
}

/// <summary>
/// Types of the append-only audit events (table <c>events</c>, FR-50, FR-51). Values are SCREAMING_SNAKE strings
/// and are part of the stored data and the live/activity feeds, so they never change once used.
/// </summary>
public static class EventTypes
{
    // Projects (FR-5, FR-7)
    public const string ProjectCreated = "PROJECT_CREATED";
    public const string ProjectUpdated = "PROJECT_UPDATED";

    /// <summary>Written with <c>project_id</c> null (the project's events cascade away); the id is in the payload.</summary>
    public const string ProjectDeleted = "PROJECT_DELETED";

    // Members (FR-8)
    public const string MemberAdded = "MEMBER_ADDED";
    public const string MemberRoleChanged = "MEMBER_ROLE_CHANGED";
    public const string MemberRemoved = "MEMBER_REMOVED";

    /// <summary>Git, LLM or notification settings changed (FR-9). The payload never contains secret values.</summary>
    public const string SettingsUpdated = "SETTINGS_UPDATED";

    /// <summary>DEP-2 seed marker: the dev seed created its demo project. Written with <c>project_id</c> null (so it
    /// survives deleting the project) and the project id in the payload; the seed never creates the demo again.</summary>
    public const string DemoProjectSeeded = "DEMO_PROJECT_SEEDED";

    // Lifecycle (FR-11)
    public const string PhaseChanged = "PHASE_CHANGED";

    // Gates (FR-12–FR-15)
    public const string GateCreated = "GATE_CREATED";
    public const string GateApproved = "GATE_APPROVED";
    public const string GateRejected = "GATE_REJECTED";
    public const string GateCancelled = "GATE_CANCELLED";
    public const string GateOverdue = "GATE_OVERDUE";

    // Artifacts (FR-16–FR-21)
    public const string ArtifactCreated = "ARTIFACT_CREATED";
    public const string ArtifactApproved = "ARTIFACT_APPROVED";
    public const string ArtifactRejected = "ARTIFACT_REJECTED";
    public const string ArtifactSuperseded = "ARTIFACT_SUPERSEDED";
    public const string ArtifactInvalidated = "ARTIFACT_INVALIDATED";
    public const string ArtifactCorrupted = "ARTIFACT_CORRUPTED";

    /// <summary>
    /// FR-20 "integrity admin alert": raised with <see cref="ArtifactCorrupted"/> when a read finds a hash mismatch.
    /// There is no alerts table (§5), so the alert is this event (category <see cref="EventCategory.Integrity"/>),
    /// visible in the project feed and the admin event list.
    /// </summary>
    public const string IntegrityAlert = "INTEGRITY_ALERT";

    // Agent runs (FR-22–FR-26)
    public const string RunStarted = "RUN_STARTED";
    public const string RunCompleted = "RUN_COMPLETED";
    public const string RunFailed = "RUN_FAILED";

    /// <summary>FR-26: a run was refused because the project's token budget is used up (Owners are notified, phase 12).</summary>
    public const string BudgetExceeded = "BUDGET_EXCEEDED";

    // Execution steps (FR-30–FR-34)
    public const string StepStarted = "STEP_STARTED";
    public const string StepCompleted = "STEP_COMPLETED";
    public const string StepRejected = "STEP_REJECTED";
    public const string StepBlocked = "STEP_BLOCKED";

    // Review (FR-38–FR-41)
    public const string ReviewStarted = "REVIEW_STARTED";
    public const string ReviewCompleted = "REVIEW_COMPLETED";

    // Release (FR-42, FR-43)
    public const string ReleaseStarted = "RELEASE_STARTED";
    public const string ReleaseCompleted = "RELEASE_COMPLETED";
    public const string ReleaseFailed = "RELEASE_FAILED";

    // Rollback (FR-44, FR-45)
    public const string RollbackCompleted = "ROLLBACK_COMPLETED";

    private static readonly Dictionary<EventCategory, IReadOnlyList<string>> ByCategory = new()
    {
        [EventCategory.Transition] = [PhaseChanged],
        [EventCategory.Gate] = [GateCreated, GateApproved, GateRejected, GateCancelled, GateOverdue],
        [EventCategory.Artifact] = [ArtifactCreated, ArtifactApproved, ArtifactRejected, ArtifactSuperseded, ArtifactInvalidated, ArtifactCorrupted],
        [EventCategory.Run] = [RunStarted, RunCompleted, RunFailed, BudgetExceeded],
        [EventCategory.Step] = [StepStarted, StepCompleted, StepRejected, StepBlocked],
        [EventCategory.Review] = [ReviewStarted, ReviewCompleted],
        [EventCategory.Release] = [ReleaseStarted, ReleaseCompleted, ReleaseFailed],
        [EventCategory.Rollback] = [RollbackCompleted],
        [EventCategory.Project] = [ProjectCreated, ProjectUpdated, ProjectDeleted, MemberAdded, MemberRoleChanged, MemberRemoved, SettingsUpdated],
        [EventCategory.Integrity] = [IntegrityAlert],
    };

    private static readonly Dictionary<string, EventCategory> CategoryByType = ByCategory
        .SelectMany(kv => kv.Value.Select(type => (type, kv.Key)))
        .ToDictionary(x => x.type, x => x.Key, StringComparer.Ordinal);

    /// <summary>The event types in a category (FR-50 activity filter).</summary>
    public static IReadOnlyList<string> InCategory(EventCategory category) =>
        ByCategory.TryGetValue(category, out var types) ? types : [];

    /// <summary>The category of an event type; <c>null</c> for internal markers such as <see cref="DemoProjectSeeded"/>.</summary>
    public static EventCategory? CategoryOf(string type) =>
        CategoryByType.TryGetValue(type, out var category) ? category : null;

    /// <summary>Every categorised event type.</summary>
    public static IReadOnlyCollection<string> AllCategorised => CategoryByType.Keys;
}
