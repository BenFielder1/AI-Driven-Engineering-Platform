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
/// Whether an event records something going wrong (FR-51 admin event log "status" filter). Events are append-only facts
/// with no delivery status of their own, so the admin log filters on this outcome instead.
/// </summary>
public enum EventOutcome
{
    /// <summary>A normal state change or decision.</summary>
    Ok = 0,

    /// <summary>A failure or alert that may need attention: failed runs and jobs, rejected or blocked steps, integrity alerts.</summary>
    Failure = 1,
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

    /// <summary>
    /// FR-79: the project's LLM binding was cleared because the binding Owner lost the Owner role, left or was removed, or
    /// deleted the connection: <c>{projectId, credentialId, boundByUserId, provider, reason}</c>
    /// (<see cref="Projects.LlmBindingClearedReason"/>). Runs then fail with <c>PROVIDER_NOT_CONFIGURED</c>.
    /// </summary>
    public const string LlmBindingCleared = "LLM_BINDING_CLEARED";

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
    /// FR-58: a writer restored an older version as a new draft: <c>{artifactId, type, version, parentVersion,
    /// restoredFromVersion, restoredFromArtifactId}</c>. Like <see cref="ArtifactCreated"/>, it tells Owners a draft awaits approval.
    /// </summary>
    public const string ArtifactRestored = "ARTIFACT_RESTORED";

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

    /// <summary>
    /// FR-78: the provider rejected a bound connection's key during a run (HTTP 401), so the connection was marked
    /// <c>invalid</c>: <c>{credentialId, userId, provider, runId}</c> (no secret). Its owner is notified through the run's
    /// <see cref="RunFailed"/> notification until FR-92 adds the "connection invalid" notification type.
    /// </summary>
    public const string ConnectionInvalid = "CONNECTION_INVALID";

    /// <summary>FR-26: a run was refused because the project's token budget is used up (Owners are notified, phase 12).</summary>
    public const string BudgetExceeded = "BUDGET_EXCEEDED";

    // Execution steps (FR-30–FR-34)
    public const string StepStarted = "STEP_STARTED";
    public const string StepCompleted = "STEP_COMPLETED";
    public const string StepRejected = "STEP_REJECTED";
    public const string StepBlocked = "STEP_BLOCKED";

    /// <summary>An escalation <c>continue</c> put a blocked step back to pending with fresh attempts (FR-14, FR-33).</summary>
    public const string StepUnblocked = "STEP_UNBLOCKED";

    // Execution runner (FR-31, FR-36, FR-37): category step, like the step events.
    public const string ExecutionStarted = "EXECUTION_STARTED";
    public const string ExecutionPaused = "EXECUTION_PAUSED";
    public const string ExecutionResumed = "EXECUTION_RESUMED";

    /// <summary>Every step of the current plan is completed (the PR and the advance to review follow, FR-36).</summary>
    public const string ExecutionCompleted = "EXECUTION_COMPLETED";

    /// <summary>
    /// FR-34: pushing the project branch after a step commit failed. The step stays completed; the next step's push or the
    /// pull request job pushes the branch again. The payload carries a reason that never contains a token.
    /// </summary>
    public const string GitPushFailed = "GIT_PUSH_FAILED";

    /// <summary>FR-36: the project PR was opened or reused (GitHub), or the branch comparison was recorded (Local).</summary>
    public const string PullRequestOpened = "PULL_REQUEST_OPENED";

    /// <summary>FR-36: opening the project PR failed for good (a permanent error, or its retries ran out).</summary>
    public const string PullRequestFailed = "PULL_REQUEST_FAILED";

    /// <summary>
    /// FR-36: the project's Git provider or repository changed, so the recorded PR belongs to the old repository and is
    /// forgotten: earlier <see cref="PullRequestOpened"/> events no longer count, and the next pull request job opens a new one.
    /// </summary>
    public const string PullRequestForgotten = "PULL_REQUEST_FORGOTTEN";

    // Review (FR-38–FR-41)

    /// <summary>A review cycle started: <c>{jobId, cycle, trigger, checkpoint, releaseVersionId}</c>.</summary>
    public const string ReviewStarted = "REVIEW_STARTED";

    /// <summary>A review cycle recorded its report: <c>{jobId, reportId, cycle, checkpoint, releaseVersionId, overallScore, threshold, driftLevel, decision, decisionReason, optimizationPlanArtifactId}</c>.</summary>
    public const string ReviewCompleted = "REVIEW_COMPLETED";

    /// <summary>A review job ended without a report (failed for good or dead-lettered): <c>{jobId, cycle, reason, deadLettered}</c>; the reason is redacted.</summary>
    public const string ReviewFailed = "REVIEW_FAILED";

    // Release (FR-42, FR-43)
    public const string ReleaseStarted = "RELEASE_STARTED";
    public const string ReleaseCompleted = "RELEASE_COMPLETED";
    public const string ReleaseFailed = "RELEASE_FAILED";

    /// <summary>
    /// FR-60: an Owner started a new release version: <c>{releaseVersionId, version, baseTag, baseCommitSha, branchName,
    /// presetId, request}</c> (the request is truncated). The project is then in refinement at checkpoint 1.
    /// </summary>
    public const string VersionStarted = "VERSION_STARTED";

    /// <summary>
    /// FR-70: the Repository Analyst stored a locked <c>codebase_summary</c>: <c>{artifactId, type, version,
    /// releaseVersionId, agentRunId}</c>. No gate: the summary is auto-locked.
    /// </summary>
    public const string CodebaseSummaryCreated = "CODEBASE_SUMMARY_CREATED";

    // Rollback (FR-44, FR-45)
    public const string RollbackCompleted = "ROLLBACK_COMPLETED";

    /// <summary>
    /// FR-51: an Admin retried a dead-lettered job: <c>{deadJobId, jobId, jobType}</c> (<c>jobId</c> is the new queued job).
    /// Written with the job's <c>project_id</c> (null for jobs without a project). Uncategorised: it is an operations
    /// record, not a lifecycle change, so it has no activity-feed category.
    /// </summary>
    public const string DeadLetterRetried = "DEAD_LETTER_RETRIED";

    private static readonly Dictionary<EventCategory, IReadOnlyList<string>> ByCategory = new()
    {
        [EventCategory.Transition] = [PhaseChanged],
        [EventCategory.Gate] = [GateCreated, GateApproved, GateRejected, GateCancelled, GateOverdue],
        [EventCategory.Artifact] = [ArtifactCreated, ArtifactApproved, ArtifactRejected, ArtifactSuperseded, ArtifactInvalidated, ArtifactCorrupted, ArtifactRestored, CodebaseSummaryCreated],
        [EventCategory.Run] = [RunStarted, RunCompleted, RunFailed, BudgetExceeded, ConnectionInvalid],
        [EventCategory.Step] = [StepStarted, StepCompleted, StepRejected, StepBlocked, StepUnblocked, ExecutionStarted, ExecutionPaused, ExecutionResumed, ExecutionCompleted, GitPushFailed, PullRequestOpened, PullRequestFailed, PullRequestForgotten],
        [EventCategory.Review] = [ReviewStarted, ReviewCompleted, ReviewFailed],
        [EventCategory.Release] = [ReleaseStarted, ReleaseCompleted, ReleaseFailed, VersionStarted],
        [EventCategory.Rollback] = [RollbackCompleted],
        [EventCategory.Project] = [ProjectCreated, ProjectUpdated, ProjectDeleted, MemberAdded, MemberRoleChanged, MemberRemoved, SettingsUpdated, LlmBindingCleared],
        [EventCategory.Integrity] = [IntegrityAlert],
    };

    private static readonly HashSet<string> FailureTypes = new(StringComparer.Ordinal)
    {
        RunFailed,
        BudgetExceeded,
        ConnectionInvalid,
        LlmBindingCleared,
        StepRejected,
        StepBlocked,
        GitPushFailed,
        PullRequestFailed,
        ReviewFailed,
        ReleaseFailed,
        ArtifactCorrupted,
        IntegrityAlert,
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

    /// <summary>FR-51: the outcome of an event type; unknown and uncategorised types are <see cref="EventOutcome.Ok"/>.</summary>
    public static EventOutcome OutcomeOf(string type) =>
        FailureTypes.Contains(type) ? EventOutcome.Failure : EventOutcome.Ok;

    /// <summary>FR-51: the event types whose outcome is <see cref="EventOutcome.Failure"/>.</summary>
    public static IReadOnlyCollection<string> Failures => FailureTypes;

    /// <summary>Every categorised event type.</summary>
    public static IReadOnlyCollection<string> AllCategorised => CategoryByType.Keys;
}
