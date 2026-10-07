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
    DateTimeOffset? LastRollbackAt);

/// <summary>
/// What to write in the rollback transaction (FR-44). The caller also cancels pending gates and queued
/// jobs, clears the review state and sets the project's review cycle to <see cref="ReviewCycle"/> (0);
/// the Git branch is never rewritten.
/// </summary>
public sealed record RollbackPlan(
    PhaseTransition Transition,
    int FromVersion,
    int TargetVersion,
    IReadOnlyList<Guid> InvalidatedArtifactIds,
    IReadOnlyList<Guid> ResetStepIds)
{
    /// <summary>The project's review cycle counter after the rollback: always reset to 0.</summary>
    public int ReviewCycle { get; init; } = ReviewCycleRules.CycleAfterRollback;
}

/// <summary>§4.8, FR-44, FR-45, D5: deterministic rollback from state transitions and <c>project_version_at_creation</c>.</summary>
public static class RollbackRules
{
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

        if (request.AgentRunInProgress || request.Steps.Any(s => s.Status == StepStatus.InProgress))
        {
            return Fail(ErrorCodes.RollbackBlocked, "A step or agent run is in progress; wait for it to finish.");
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

        return Result.Success(new RollbackPlan(
            transition.Value,
            current.Version,
            target,
            [.. invalidated.Select(a => a.Id)],
            resetSteps));
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

    private static Result<RollbackPlan> Fail(string code, string message) =>
        Result.Failure<RollbackPlan>(new DomainError(code, message));
}
