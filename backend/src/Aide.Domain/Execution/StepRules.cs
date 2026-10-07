using Aide.Domain.Common;

namespace Aide.Domain.Execution;

/// <summary>A step's position in its plan, as the runner and Guardian see it.</summary>
public sealed record StepState(int Number, StepStatus Status, IReadOnlyList<int> Dependencies, IReadOnlyList<string> DeclaredPaths)
{
    public StepState(int number, StepStatus status, params int[] dependencies)
        : this(number, status, dependencies, [])
    {
    }
}

/// <summary>What happens to a step after the Guardian rejects an attempt (FR-33).</summary>
public sealed record StepRejectionOutcome(StepStatus NewStatus, bool Retry, bool Escalate);

/// <summary>A start/complete outcome; <see cref="Changed"/> is false for an idempotent re-delivery (D17).</summary>
public sealed record StepStatusChange(StepStatus NewStatus, bool Changed);

/// <summary>Execution step rules (FR-30, FR-31, FR-33, D17).</summary>
public static class StepRules
{
    /// <summary>A rejected step is retried up to this many times; the next rejection blocks it.</summary>
    public const int MaxRetries = 3;

    /// <summary>
    /// FR-31: the lowest-numbered <c>pending</c> step whose dependencies are all <c>completed</c>, or <c>null</c>.
    /// Steps run one at a time, so nothing is picked while a step is <c>in_progress</c>. A <c>rejected</c> step
    /// awaiting retry is picked before anything else.
    /// </summary>
    public static int? NextStep(IEnumerable<StepState> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var list = steps.ToList();
        if (list.Any(s => s.Status == StepStatus.InProgress))
        {
            return null;
        }

        if (list.Where(s => s.Status == StepStatus.Rejected).Select(s => (int?)s.Number).Min() is { } retry)
        {
            return retry;
        }

        var completed = list.Where(s => s.Status == StepStatus.Completed).Select(s => s.Number).ToHashSet();
        return list
            .Where(s => s.Status == StepStatus.Pending && s.Dependencies.All(completed.Contains))
            .Select(s => (int?)s.Number)
            .Min();
    }

    /// <summary>
    /// FR-33: <paramref name="attempt"/> is the 1-based attempt that was just rejected. Attempts 1–3 are
    /// retried with the violations as feedback; the 4th rejection blocks the step and raises an escalation gate.
    /// </summary>
    public static StepRejectionOutcome OnRejected(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return attempt <= MaxRetries
            ? new StepRejectionOutcome(StepStatus.Rejected, Retry: true, Escalate: false)
            : new StepRejectionOutcome(StepStatus.Blocked, Retry: false, Escalate: true);
    }

    /// <summary>Start (or retry) a step: pending/rejected → in_progress.</summary>
    public static Result<StepStatusChange> Start(StepStatus status) => status switch
    {
        StepStatus.Pending or StepStatus.Rejected => Result.Success(new StepStatusChange(StepStatus.InProgress, Changed: true)),
        StepStatus.InProgress => Result.Success(new StepStatusChange(StepStatus.InProgress, Changed: false)),
        _ => Conflict(status, "started"),
    };

    /// <summary>D17: completion is keyed by step, so a re-delivered completion is a no-op and never double-counts.</summary>
    public static Result<StepStatusChange> Complete(StepStatus status) => status switch
    {
        StepStatus.InProgress => Result.Success(new StepStatusChange(StepStatus.Completed, Changed: true)),
        StepStatus.Completed => Result.Success(new StepStatusChange(StepStatus.Completed, Changed: false)),
        _ => Conflict(status, "completed"),
    };

    /// <summary>Cascade or rollback reset: anything not already invalidated becomes invalidated.</summary>
    public static StepStatusChange Invalidate(StepStatus status) =>
        new(StepStatus.Invalidated, status != StepStatus.Invalidated);

    private static Result<StepStatusChange> Conflict(StepStatus status, string verb) =>
        Result.Failure<StepStatusChange>(new DomainError(ErrorCodes.Conflict, $"A {status.ToWire()} step can't be {verb}."));
}
