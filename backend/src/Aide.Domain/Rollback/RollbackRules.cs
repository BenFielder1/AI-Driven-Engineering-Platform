using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Execution;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;

namespace Aide.Domain.Rollback;

/// <summary>A recorded state transition: the phase entered and the project version it was recorded at.</summary>
public sealed record RecordedTransition(int ProjectVersion, Phase ToPhase, DateTimeOffset CreatedAt);

/// <summary>Everything a rollback decision needs. Time is passed in, so the rule stays pure.</summary>
public sealed record RollbackRequest(
    LifecycleState Current,
    int TargetVersion,
    string Reason,
    IReadOnlyList<RecordedTransition> Transitions,
    IReadOnlyList<ArtifactRef> Artifacts,
    IReadOnlyList<StepRef> Steps,
    bool AgentRunInProgress,
    DateTimeOffset Now,
    DateTimeOffset? LastRollbackAt)
{
    /// <summary>
    /// A release job is running (merging and tagging, FR-42). Its Git work can't be undone by a rollback, so the rollback
    /// waits for it (<c>ROLLBACK_BLOCKED</c>); a release job that is only queued is cancelled by the rollback instead.
    /// </summary>
    public bool ReleaseRunning { get; init; }

    /// <summary>
    /// A release job is queued (waiting out a retry backoff) for an attempt that already merged (its merge commit is on
    /// record) but isn't tagged yet. Cancelling it would leave a merged, untagged release behind, so the rollback waits for
    /// the release to finish (<c>ROLLBACK_BLOCKED</c>, reason <see cref="RollbackRules.ReleasePartiallyApplied"/>).
    /// </summary>
    public bool ReleasePartiallyApplied { get; init; }
}

/// <summary>
/// What to write in the rollback transaction (FR-44). The caller also cancels pending gates and queued
/// jobs, clears the review state and sets the project's review cycle to <see cref="ReviewCycle"/> (0);
/// the Git branch is never rewritten. <see cref="ResetStepIds"/> become <c>invalidated</c> (their plan is invalidated);
/// <see cref="RequeuedStepIds"/> go back to <c>pending</c> with the attempt count reset (their plan survives).
/// </summary>
public sealed record RollbackPlan(
    PhaseTransition Transition,
    int FromVersion,
    int TargetVersion,
    IReadOnlyList<Guid> InvalidatedArtifactIds,
    IReadOnlyList<Guid> ResetStepIds)
{
    /// <summary>
    /// Blocked or rejected steps of plans that survive the rollback: back to <c>pending</c> with attempt 0, so a rollback
    /// that keeps the plan (e.g. to the planning version) never leaves a step blocked with its escalation gate cancelled.
    /// Completed steps stay completed: their commits stay on the branch (§4.8), so re-running them would only re-find them.
    /// </summary>
    public IReadOnlyList<Guid> RequeuedStepIds { get; init; } = [];

    /// <summary>The project's review cycle counter after the rollback: always reset to 0.</summary>
    public int ReviewCycle { get; init; } = ReviewCycleRules.CycleAfterRollback;
}

/// <summary>§4.8, FR-44, FR-45, D5: deterministic rollback from state transitions and <c>project_version_at_creation</c>.</summary>
public static class RollbackRules
{
    /// <summary><c>ROLLBACK_BLOCKED</c> reason: a step is in progress.</summary>
    public const string StepInProgress = "step_in_progress";

    /// <summary><c>ROLLBACK_BLOCKED</c> reason: an agent run is queued or running.</summary>
    public const string AgentRunInProgress = "agent_run_in_progress";

    /// <summary><c>ROLLBACK_BLOCKED</c> reason: a release job is running.</summary>
    public const string ReleaseRunning = "release_running";

    /// <summary><c>ROLLBACK_BLOCKED</c> reason: a queued release job's attempt already merged but isn't tagged.</summary>
    public const string ReleasePartiallyApplied = "release_partially_applied";

    /// <summary>The ProblemDetails <c>details</c> entry naming why a rollback is blocked: <c>reason: &lt;reason&gt;</c>.</summary>
    public static string BlockedDetail(string reason) => $"reason: {reason}";

    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    public static Result<RollbackPlan> Plan(RollbackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = request.Current;
        var target = request.TargetVersion;

        if (target < 1 || target >= current.Version)
        {
            return Fail(ErrorCodes.InvalidTargetVersion, $"Version {target} is not an earlier version; the current version is {current.Version}.");
        }

        if (RestoredPhase(request.Transitions, target) is not { } phase)
        {
            return Fail(ErrorCodes.InvalidTargetVersion, $"Version {target} has no recorded state.");
        }

        if (phase == Phase.Release)
        {
            return Fail(ErrorCodes.InvalidTargetVersion, $"Version {target} is in the release phase; a rollback can't restore a release.");
        }

        if (request.Steps.Any(s => s.Status == StepStatus.InProgress))
        {
            return Blocked(StepInProgress, "A step is in progress; wait for it to finish.");
        }

        if (request.AgentRunInProgress)
        {
            return Blocked(AgentRunInProgress, "An agent run is in progress; wait for it to finish.");
        }

        if (request.ReleaseRunning)
        {
            return Blocked(ReleaseRunning, "A release is merging and tagging; wait for it to finish.");
        }

        if (request.ReleasePartiallyApplied)
        {
            return Blocked(
                ReleasePartiallyApplied,
                "The release was partially applied: it merged but isn't tagged yet, and its job is waiting to retry. Let the release finish (or retry it) first.");
        }

        if (request.LastRollbackAt is { } last && request.Now - last < MinimumInterval)
        {
            var retryAfter = MinimumInterval - (request.Now - last);
            return Result.Failure<RollbackPlan>(new DomainError(ErrorCodes.RateLimited, "Only one rollback per project per minute is allowed.")
            {
                RetryAfter = retryAfter,
            });
        }

        var transition = LifecycleStateMachine.Apply(
            current,
            TransitionRequest.Rollback(phase, target, request.Reason),
            LifecycleSnapshot.Empty);
        if (transition.IsFailure)
        {
            return Result.Failure<RollbackPlan>(transition.Error!);
        }

        var invalidated = request.Artifacts
            .Where(a => a.ProjectVersionAtCreation > target && ArtifactRules.ShouldInvalidate(a.Status))
            .ToList();
        var invalidatedPlanIds = invalidated
            .Where(a => a.Type is ArtifactType.ImplementationPlan or ArtifactType.OptimizationPlan)
            .Select(a => a.Id)
            .ToHashSet();
        var resetSteps = request.Steps
            .Where(s => invalidatedPlanIds.Contains(s.PlanArtifactId) && s.Status != StepStatus.Invalidated)
            .Select(s => s.Id)
            .ToList();

        var requeuedSteps = request.Steps
            .Where(s => !invalidatedPlanIds.Contains(s.PlanArtifactId) && StepRules.RequeueOnRollback(s.Status))
            .Select(s => s.Id)
            .ToList();

        return Result.Success(new RollbackPlan(
            transition.Value,
            current.Version,
            target,
            [.. invalidated.Select(a => a.Id)],
            resetSteps)
        {
            RequeuedStepIds = requeuedSteps,
        });
    }

    /// <summary>
    /// The phase of the latest transition recorded at <paramref name="version"/>. Version 1 with no
    /// recorded transition is the project's creation state: refinement.
    /// </summary>
    public static Phase? RestoredPhase(IEnumerable<RecordedTransition> transitions, int version)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        var latest = transitions
            .Where(t => t.ProjectVersion == version)
            .OrderBy(t => t.CreatedAt)
            .LastOrDefault();
        return latest?.ToPhase ?? (version == 1 ? Phase.Refinement : null);
    }

    /// <summary>
    /// FR-44: the project's versions up to <paramref name="currentVersion"/>, newest first, for the rollback target picker:
    /// the phase restored at each version (<see cref="RestoredPhase"/>), when its latest transition was recorded, and why it
    /// can't be a target (the current version, or a release-phase version). Versions above the current one belong to a
    /// timeline a rollback left, and versions with no recorded phase can't be restored, so neither is listed (the current
    /// version is always listed, with <paramref name="currentPhase"/> if nothing was recorded at it).
    /// </summary>
    public static IReadOnlyList<ProjectVersionInfo> Versions(
        IReadOnlyList<RecordedTransition> transitions, int currentVersion, Phase currentPhase, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        var versions = new List<ProjectVersionInfo>();
        for (var version = currentVersion; version >= 1; version--)
        {
            var latest = transitions.Where(t => t.ProjectVersion == version).OrderBy(t => t.CreatedAt).LastOrDefault();
            var phase = RestoredPhase(transitions, version) ?? (version == currentVersion ? currentPhase : null);
            if (phase is not { } restored)
            {
                continue;
            }

            VersionBlock? block = version == currentVersion
                ? VersionBlock.CurrentVersion
                : restored == Phase.Release ? VersionBlock.ReleasePhase : null;
            versions.Add(new ProjectVersionInfo(version, restored, latest?.CreatedAt ?? createdAt, block));
        }

        return versions;
    }

    private static Result<RollbackPlan> Fail(string code, string message) =>
        Result.Failure<RollbackPlan>(new DomainError(code, message));

    /// <summary><c>ROLLBACK_BLOCKED</c> with <c>reason: &lt;reason&gt;</c> in the details (the frontend reads it).</summary>
    private static Result<RollbackPlan> Blocked(string reason, string message) =>
        Result.Failure<RollbackPlan>(new DomainError(ErrorCodes.RollbackBlocked, message) { Details = [BlockedDetail(reason)] });
}

/// <summary>Why a recorded project version can't be a rollback target (FR-44, §4.8 and the release-phase decision).</summary>
public enum VersionBlock
{
    /// <summary>It is the current version: a rollback needs an earlier one.</summary>
    CurrentVersion = 0,

    /// <summary>The version is in the release phase; restoring it would leave an unrecoverable release/rolled_back state.</summary>
    ReleasePhase = 1,
}

/// <summary>A project version for the rollback picker (FR-44): its restored phase, when it was entered, and any block.</summary>
public sealed record ProjectVersionInfo(int Version, Phase Phase, DateTimeOffset EnteredAt, VersionBlock? Block)
{
    public bool RollbackAllowed => Block is null;
}
