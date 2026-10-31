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

    /// <summary>
    /// FR-136: the project's Git binding (a GitLab or Bitbucket connection) was cleared because the binding Owner lost the
    /// Owner role, left or was removed, or deleted the connection, or the host rejected it (marked <c>invalid</c>):
    /// <c>{projectId, gitConnectionId, boundByUserId, provider, reason}</c> (<see cref="GitHosts.GitBindingClearedReason"/>).
    /// Git jobs then fail with <c>GIT_NOT_CONNECTED</c> until an Owner re-binds; Owners are notified (<c>git_binding_cleared</c>).
    /// </summary>
    public const string GitBindingCleared = "GIT_BINDING_CLEARED";

    /// <summary>DEP-2 seed marker: the dev seed created its demo project. Written with <c>project_id</c> null (so it
    /// survives deleting the project) and the project id in the payload; the seed never creates the demo again.</summary>
    public const string DemoProjectSeeded = "DEMO_PROJECT_SEEDED";

    /// <summary>§9 seed marker: the dev seed created its preset demo project (<c>{projectId, presetId}</c>, project_id null).</summary>
    public const string DemoPresetProjectSeeded = "DEMO_PRESET_PROJECT_SEEDED";

    /// <summary>§9 (v4) seed marker: the dev seed created its BaaS stack demo project (<c>{projectId, packs}</c>, project_id null).</summary>
    public const string DemoStackProjectSeeded = "DEMO_STACK_PROJECT_SEEDED";

    /// <summary>
    /// DEP-V2 (§9) seed marker: the one-off upgrade of a v1-seeded demo project to released 0.1.0 ran
    /// (<c>{projectId, outcome}</c>, project_id null; <c>outcome</c> <c>released</c>, or why it was skipped).
    /// </summary>
    public const string DemoProjectReleaseUpgraded = "DEMO_PROJECT_RELEASE_UPGRADED";

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
    /// <c>invalid</c>: <c>{credentialId, userId, provider, runId}</c> (no secret). Also written when Vercel rejects a
    /// connection's token (FR-87): <c>{connectionId, userId, provider: "vercel"}</c>, and when GitHub rejects a user connection
    /// (§4.14, project_id null): <c>{userId, provider: "github", login, reason}</c>. FR-92: the connection's owner
    /// (<c>userId</c>) gets the personal <c>connection_invalid</c> notification (in-app and email).
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
    /// FR-65: the release merge was refused (branch protection, required reviews or checks, missing permission) or the merge
    /// strategy is <c>manual</c>: <c>{releaseId, releaseVersionId, version, checkpoint, tag, attempt, pullRequestUrl,
    /// mergeStrategy, awaitingMerge: true, reasons[{code, message}]}</c>. Owners are notified with the PR link and the reasons; the merge poll waits (§4.14).
    /// </summary>
    public const string ReleaseAwaitingMerge = "RELEASE_AWAITING_MERGE";

    /// <summary>
    /// FR-65, §4.14: the merge poll saw the PR merged on GitHub (or the Local branch merged by someone else):
    /// <c>{releaseId, releaseVersionId, version, checkpoint, tag, mergeCommitSha, githubReleaseUrl, pullRequestUrl}</c>. Followed by
    /// <see cref="ReleaseCompleted"/> once tagged.
    /// </summary>
    public const string ReleaseMergedExternally = "RELEASE_MERGED_EXTERNALLY";

    /// <summary>
    /// FR-60: an Owner started a new release version: <c>{releaseVersionId, version, baseTag, baseCommitSha, branchName,
    /// stackPacks, catalogVersion, request}</c> (<c>presetId</c> in events before FR-129) (the request is truncated). The project is then in refinement at checkpoint 1.
    /// </summary>
    public const string VersionStarted = "VERSION_STARTED";

    /// <summary>
    /// FR-74, FR-92 (history only): an Owner changed the preset of the version in development: <c>{releaseVersionId, version,
    /// fromPresetId, fromPresetVersion, presetId, presetVersion, presetHash}</c> (ids <c>null</c> = no preset). FR-129: no
    /// longer written (presets were retired); events recorded before the upgrade stay readable.
    /// </summary>
    public const string PresetChanged = "PRESET_CHANGED";

    /// <summary>
    /// FR-117, FR-155: an Owner changed the stack composition of the version in development: <c>{releaseVersionId, version,
    /// from, to, layersChanged, catalogVersion, catalogHash, fromPresetId}</c>. <c>from</c>/<c>to</c> are the normalised
    /// compositions (§4.18; <c>from</c> <c>null</c>: no stack before, or the legacy preset <c>fromPresetId</c>);
    /// <c>layersChanged</c> lists the catalog layers that differ. Replaces <see cref="PresetChanged"/>.
    /// </summary>
    public const string StackChanged = "STACK_CHANGED";

    /// <summary>
    /// FR-118, FR-128, FR-155: the stack detector's report of a version's base commit was used by the Repository Analyst (the
    /// first point where detection has a project: <c>POST /api/stack/detect</c> runs before one exists):
    /// <c>{releaseVersionId, version, baseCommitSha, suggestions[{choice, option, path, mode}], conflicts[choice], mismatches}</c>.
    /// </summary>
    public const string StackDetected = "STACK_DETECTED";

    /// <summary>
    /// FR-130, FR-133, FR-155: an Admin changed a Git host instance: <c>{instanceId, kind, change, name, baseUrl, enabled}</c>
    /// (<c>change</c>: <c>created</c>, <c>updated</c> or <c>removed</c>; never a secret). Written with <c>project_id</c> null.
    /// </summary>
    public const string GitHostInstanceChanged = "GIT_HOST_INSTANCE_CHANGED";

    /// <summary>
    /// FR-70: the Repository Analyst stored a locked <c>codebase_summary</c>: <c>{artifactId, type, version,
    /// releaseVersionId, agentRunId}</c>. No gate: the summary is auto-locked.
    /// </summary>
    public const string CodebaseSummaryCreated = "CODEBASE_SUMMARY_CREATED";

    // Deployments (FR-90, FR-91, §4.15): category release.

    /// <summary>
    /// FR-90, FR-91: a deployment row was recorded: <c>{deploymentId, target, provider, trigger, gitRef, gitSha,
    /// releaseVersionId, releaseId, promotedFromDeploymentId}</c>.
    /// </summary>
    public const string DeploymentCreated = "DEPLOYMENT_CREATED";

    /// <summary>
    /// §4.15: a deployment changed state: <c>{deploymentId, target, status, url, errorCode}</c> (never env values or the
    /// build log).
    /// </summary>
    public const string DeploymentUpdated = "DEPLOYMENT_UPDATED";

    /// <summary>
    /// FR-91: a deployment failed in a way Owners act on — a production deployment (the release stands; "Redeploy"), or any
    /// deployment whose Vercel token was rejected: <c>{deploymentId, target, status, errorCode, releaseVersion, gitSha,
    /// url}</c>. Owners get the <c>deployment_failed</c> notification (FR-92).
    /// </summary>
    public const string DeploymentFailed = "DEPLOYMENT_FAILED";

    /// <summary>
    /// FR-90: a preview deployment became ready: <c>{deploymentId, target, status, url, releaseVersion, gitSha}</c>. Every
    /// member gets the <c>preview_ready</c> notification, linking to the Review page (FR-92).
    /// </summary>
    public const string DeploymentPreviewReady = "DEPLOYMENT_PREVIEW_READY";

    /// <summary>
    /// FR-147, FR-155: a preview rollout finished: <c>{rolloutId, releaseVersionId, gitSha, state, components[{component,
    /// status, url}], migrationRunId}</c>.
    /// </summary>
    public const string PreviewRolloutCompleted = "PREVIEW_ROLLOUT_COMPLETED";

    /// <summary>
    /// FR-144, FR-155: a deployment target's setup finished: <c>{targetId, component, provider, resourceId, status}</c>.
    /// </summary>
    public const string DeploymentTargetConfigured = "DEPLOYMENT_TARGET_CONFIGURED";

    /// <summary>
    /// FR-144, FR-155: a deployment target failed or needs attention: <c>{targetId, component, provider, reason, errorCode}</c>
    /// (<see cref="Deployments.DeploymentTargetFailureReasons"/>). Owners get <c>deployment_target_attention</c> only when
    /// the platform lost Git access or the token is invalid (<see cref="Notifications.NotificationRules.Notifies"/>).
    /// </summary>
    public const string DeploymentTargetFailed = "DEPLOYMENT_TARGET_FAILED";

    /// <summary>
    /// FR-152, FR-155: a database became available through its provisioning job (created, linked, External or restored):
    /// <c>{databaseId, environment, provider, mode, status, expiresAt}</c>.
    /// </summary>
    public const string DatabaseProvisioned = "DATABASE_PROVISIONED";

    /// <summary>
    /// FR-152, FR-155: provisioning a database failed: <c>{databaseId, environment, provider, errorCode}</c>. Owners get
    /// <c>database_provisioning_failed</c>.
    /// </summary>
    public const string DatabaseProvisioningFailed = "DATABASE_PROVISIONING_FAILED";

    /// <summary>
    /// §4.21, FR-155: a database is about to go away: a Render free database within 7 days of its expiry, or a paused
    /// Supabase project: <c>{databaseId, environment, provider, reason, expiresAt}</c> (<c>reason</c>:
    /// <see cref="Databases.ManagedDatabaseRules.ExpiringReasonExpiry"/> or <see cref="Databases.ManagedDatabaseRules.ExpiringReasonPaused"/>).
    /// Owners get <c>database_expiring</c>.
    /// </summary>
    public const string DatabaseExpiring = "DATABASE_EXPIRING";

    /// <summary>FR-148, FR-155: an Owner reset the preview database: <c>{databaseId, provider, method, migrationRunId}</c>.</summary>
    public const string DatabaseReset = "DATABASE_RESET";

    /// <summary>FR-153, FR-155: a Supabase BaaS migration run started: <c>{runId, environment, releaseId, gitSha}</c>.</summary>
    public const string MigrationRunStarted = "MIGRATION_RUN_STARTED";

    /// <summary>FR-153, FR-155: a Supabase BaaS migration run succeeded: <c>{runId, environment, releaseId, gitSha, applied}</c> (<c>applied</c>: how many files).</summary>
    public const string MigrationRunCompleted = "MIGRATION_RUN_COMPLETED";

    /// <summary>
    /// FR-153, FR-154, FR-155: a migration failed: a Supabase BaaS run (<c>{runId, environment, releaseId, gitSha, errorCode}</c>)
    /// or a host-mode platform pre-deploy (<c>runId</c> null, with <c>deploymentId</c>); never SQL or row data. Owners get
    /// <c>migration_failed</c>.
    /// </summary>
    public const string MigrationRunFailed = "MIGRATION_RUN_FAILED";

    /// <summary>
    /// FR-153, §4.21: the host merged a pull request head other than the commit the pre-merge production migrations ran on
    /// (Bitbucket can't pin its merge): <c>{releaseId, expectedHeadSha, mergedHeadSha, mergeCommitSha}</c>. The production
    /// rollout's post-merge run on the merge commit applies whatever the merged head added.
    /// </summary>
    public const string MergedHeadMismatch = "MERGED_HEAD_MISMATCH";

    // Rollback (FR-44, FR-45)
    public const string RollbackCompleted = "ROLLBACK_COMPLETED";

    /// <summary>
    /// FR-51: an Admin retried a dead-lettered job: <c>{deadJobId, jobId, jobType}</c> (<c>jobId</c> is the new queued job).
    /// Written with the job's <c>project_id</c> (null for jobs without a project). Uncategorised: it is an operations
    /// record, not a lifecycle change, so it has no activity-feed category.
    /// </summary>
    public const string DeadLetterRetried = "DEAD_LETTER_RETRIED";

    /// <summary>
    /// FR-84: merge readiness of the version's PR was checked (when the pre-release gate was created, or on demand):
    /// <c>{releaseVersionId, readiness}</c> with the readiness as the API returns it. The latest one is what
    /// <c>GET /git/merge-readiness</c> returns while it is fresh. Uncategorised: an operations record, not an activity entry.
    /// </summary>
    public const string MergeReadinessChecked = "MERGE_READINESS_CHECKED";

    private static readonly Dictionary<EventCategory, IReadOnlyList<string>> ByCategory = new()
    {
        [EventCategory.Transition] = [PhaseChanged],
        [EventCategory.Gate] = [GateCreated, GateApproved, GateRejected, GateCancelled, GateOverdue],
        [EventCategory.Artifact] = [ArtifactCreated, ArtifactApproved, ArtifactRejected, ArtifactSuperseded, ArtifactInvalidated, ArtifactCorrupted, ArtifactRestored, CodebaseSummaryCreated],
        [EventCategory.Run] = [RunStarted, RunCompleted, RunFailed, BudgetExceeded, ConnectionInvalid],
        [EventCategory.Step] = [StepStarted, StepCompleted, StepRejected, StepBlocked, StepUnblocked, ExecutionStarted, ExecutionPaused, ExecutionResumed, ExecutionCompleted, GitPushFailed, PullRequestOpened, PullRequestFailed, PullRequestForgotten],
        [EventCategory.Review] = [ReviewStarted, ReviewCompleted, ReviewFailed],
        [EventCategory.Release] = [ReleaseStarted, ReleaseCompleted, ReleaseFailed, ReleaseAwaitingMerge, ReleaseMergedExternally, VersionStarted, DeploymentCreated, DeploymentUpdated, DeploymentFailed, DeploymentPreviewReady, PreviewRolloutCompleted, DatabaseReset, MigrationRunStarted, MigrationRunCompleted, MigrationRunFailed, MergedHeadMismatch, DeploymentTargetConfigured, DeploymentTargetFailed, DatabaseProvisioned, DatabaseProvisioningFailed, DatabaseExpiring],
        [EventCategory.Rollback] = [RollbackCompleted],
        [EventCategory.Project] = [ProjectCreated, ProjectUpdated, ProjectDeleted, MemberAdded, MemberRoleChanged, MemberRemoved, SettingsUpdated, LlmBindingCleared, GitBindingCleared, GitHostInstanceChanged, PresetChanged, StackChanged, StackDetected],
        [EventCategory.Integrity] = [IntegrityAlert],
    };

    private static readonly HashSet<string> FailureTypes = new(StringComparer.Ordinal)
    {
        RunFailed,
        BudgetExceeded,
        ConnectionInvalid,
        LlmBindingCleared,
        GitBindingCleared,
        StepRejected,
        StepBlocked,
        GitPushFailed,
        PullRequestFailed,
        ReviewFailed,
        ReleaseFailed,
        DeploymentFailed,
        MigrationRunFailed,
        DeploymentTargetFailed,
        DatabaseProvisioningFailed,
        DatabaseExpiring,
        ArtifactCorrupted,
        IntegrityAlert,
    };

    private static readonly Dictionary<string, EventCategory> CategoryByType = ByCategory
        .SelectMany(kv => kv.Value.Select(type => (type, kv.Key)))
        .ToDictionary(x => x.type, x => x.Key, StringComparer.Ordinal);

    /// <summary>The event types in a category (FR-50 activity filter).</summary>
    public static IReadOnlyList<string> InCategory(EventCategory category) =>
        ByCategory.TryGetValue(category, out var types) ? types : [];

    /// <summary>The category of an event type; <c>null</c> for internal markers such as <see cref="DemoProjectSeeded"/> and <see cref="DemoProjectReleaseUpgraded"/>.</summary>
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
