using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Lifecycle;
using Aide.Domain.Projects;

namespace Aide.Domain.Execution;

/// <summary>What the start/resume/pause rules need to know about a project's execution (FR-26, FR-31, FR-37).</summary>
/// <param name="Phase">The project's current phase.</param>
/// <param name="HasPlan">A current (locked) plan exists.</param>
/// <param name="Steps">The current plan's steps (invalidated ones excluded).</param>
/// <param name="TokenBudget">The optional token budget (FR-26).</param>
/// <param name="TokensUsed">Tokens used so far.</param>
public sealed record ExecutionFacts(Phase Phase, bool HasPlan, IReadOnlyList<StepState> Steps, long? TokenBudget, long TokensUsed);

/// <summary>
/// The execution runner's rules (FR-31, FR-33, FR-35, FR-37, §4.9): when execution may start or resume, whether work
/// remains, the runner's overall status and the project branch name.
/// </summary>
public static class ExecutionRules
{
    /// <summary>Hex characters of the project id in the project branch name (as the old stack: the first eight).</summary>
    public const int BranchShortIdLength = 8;

    /// <summary>
    /// Is there a step to run: one <c>in_progress</c> (an interrupted attempt to finish) or a next step
    /// (<see cref="StepRules.NextStep"/>)? A <c>blocked</c> step stops execution until its escalation is decided (FR-33).
    /// </summary>
    public static bool HasWork(IReadOnlyCollection<StepState> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Any(s => s.Status == StepStatus.Blocked))
        {
            return false;
        }

        return steps.Any(s => s.Status == StepStatus.InProgress) || StepRules.NextStep(steps) is not null;
    }

    /// <summary>§4.9 / FR-37: after a step attempt, queue the next one unless paused or nothing is runnable.</summary>
    public static bool ShouldContinue(IReadOnlyCollection<StepState> steps, bool paused) => !paused && HasWork(steps);

    /// <summary>Every step of the plan is <c>completed</c> (and there is at least one).</summary>
    public static bool AllCompleted(IReadOnlyCollection<StepStatus> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.Count > 0 && steps.All(s => s == StepStatus.Completed);
    }

    /// <summary>
    /// The runner's status: <c>completed</c> when every step is completed, <c>blocked</c> when a step awaits an
    /// escalation, <c>paused</c> when the pause flag is set, <c>running</c> while an execution job is queued or running,
    /// otherwise <c>idle</c>.
    /// </summary>
    public static ExecutionRunStatus Status(IReadOnlyCollection<StepStatus> steps, bool paused, bool jobActive)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (AllCompleted(steps))
        {
            return ExecutionRunStatus.Completed;
        }

        if (steps.Contains(StepStatus.Blocked))
        {
            return ExecutionRunStatus.Blocked;
        }

        if (paused)
        {
            return ExecutionRunStatus.Paused;
        }

        return jobActive ? ExecutionRunStatus.Running : ExecutionRunStatus.Idle;
    }

    /// <summary>
    /// FR-31, FR-26: execution starts only in <c>execution</c>, with a locked plan that has steps, no blocked step and
    /// something runnable (<c>PHASE_PRECONDITION_FAILED</c>), and while the token budget isn't used up
    /// (<c>BUDGET_EXCEEDED</c>).
    /// </summary>
    public static Result CheckStart(ExecutionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Phase != Phase.Execution)
        {
            return Precondition($"The project is in {facts.Phase.ToWire()}; execution runs only in the execution phase.");
        }

        if (!facts.HasPlan || facts.Steps.Count == 0)
        {
            return Precondition("There is no approved plan with steps to execute.");
        }

        if (facts.Steps.Any(s => s.Status == StepStatus.Blocked))
        {
            return Precondition("A step is blocked: decide its escalation gate first.");
        }

        if (AllCompleted([.. facts.Steps.Select(s => s.Status)]))
        {
            return Precondition("Every step of the current plan is already completed.");
        }

        if (!HasWork(facts.Steps))
        {
            return Precondition("No step can run: the remaining steps depend on steps that aren't completed.");
        }

        return TokenBudget.CheckCanStart(facts.TokenBudget, facts.TokensUsed);
    }

    /// <summary>
    /// FR-37: resume needs the execution phase (<c>PHASE_PRECONDITION_FAILED</c>); when work remains, the token budget
    /// must not be used up (<c>BUDGET_EXCEEDED</c>). Resuming with nothing to run only clears the pause flag.
    /// </summary>
    public static Result CheckResume(ExecutionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Phase != Phase.Execution)
        {
            return Precondition($"The project is in {facts.Phase.ToWire()}; execution can be resumed only in the execution phase.");
        }

        return HasWork(facts.Steps) ? TokenBudget.CheckCanStart(facts.TokenBudget, facts.TokensUsed) : Result.Success();
    }

    /// <summary>FR-37: pausing needs the execution phase (<c>PHASE_PRECONDITION_FAILED</c>); it is idempotent.</summary>
    public static Result CheckPause(Phase phase) =>
        phase == Phase.Execution
            ? Result.Success()
            : Precondition($"The project is in {phase.ToWire()}; execution can be paused only in the execution phase.");

    /// <summary>
    /// FR-34: the project branch <c>aide/&lt;project-slug&gt;-&lt;short-id&gt;</c>, the short id being the first
    /// <see cref="BranchShortIdLength"/> hex characters of the project id. A slug that would make an unsafe branch name
    /// falls back to <see cref="ProjectRules.SlugFallback"/>.
    /// </summary>
    public static string BranchName(string slug, Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(slug);
        var shortId = projectId.ToString("N")[..BranchShortIdLength];
        var candidate = $"aide/{slug.Trim()}-{shortId}";
        return slug.Trim().Length > 0 && ProjectSettingsRules.IsValidBranchName(candidate)
            ? candidate
            : $"aide/{ProjectRules.SlugFallback}-{shortId}";
    }

    private static Result Precondition(string message) =>
        Result.Failure(new DomainError(ErrorCodes.PhasePreconditionFailed, message) { Details = [message] });
}
