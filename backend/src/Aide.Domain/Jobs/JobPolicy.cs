using Aide.Domain.Common;

namespace Aide.Domain.Jobs;

/// <summary>Retry with exponential backoff: delay before retry n (1-based) = BaseDelay × Factor^(n−1).</summary>
public sealed record RetryPolicy(int MaxRetries, TimeSpan BaseDelay, int Factor)
{
    /// <summary>§4.9 / FR-25: up to 3 retries (4 attempts in total) at 2 s, 8 s, 32 s.</summary>
    public static RetryPolicy Default { get; } = new(3, TimeSpan.FromSeconds(2), 4);

    public int MaxAttempts => MaxRetries + 1;

    /// <summary>True when another attempt is allowed after <paramref name="attemptsMade"/> failed attempts.</summary>
    public bool ShouldRetry(int attemptsMade) => attemptsMade < MaxAttempts;

    public TimeSpan DelayBeforeRetry(int retryNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retryNumber, 1);
        return BaseDelay * Math.Pow(Factor, retryNumber - 1);
    }
}

/// <summary>The parts of a job row the retry rule needs. <see cref="Payload"/> is the raw jsonb text.</summary>
public sealed record JobSnapshot(Guid Id, string Type, string Payload, Guid? ProjectId, JobStatus Status);

/// <summary>A job to insert.</summary>
public sealed record NewJob(string Type, string Payload, Guid? ProjectId, JobStatus Status, int Attempts, DateTimeOffset RunAfter);

/// <summary>FR-51 outcome: the dead job's new status, and the replacement job to insert.</summary>
public sealed record DeadLetterRetry(JobStatus DeadJobStatus, NewJob Replacement);

/// <summary>What happens to a job after a failed attempt.</summary>
public sealed record JobFailureOutcome(JobStatus Status, DateTimeOffset? RunAfter)
{
    public bool IsDeadLetter => Status == JobStatus.Dead;
}

/// <summary>§4.9 and NFR-REL: job retries, dead letters, lease expiry and cancellation.</summary>
public static class JobPolicy
{
    /// <summary>A <c>running</c> job whose worker hasn't finished within this lease is presumed dead.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// After a failed attempt (<paramref name="attemptsMade"/> includes it): transient failures are re-queued
    /// with backoff until the retries are used up, then the job is dead-lettered. Non-transient failures
    /// (e.g. <c>MISSING_INPUT</c>) fail at once; retrying can't fix them.
    /// </summary>
    public static JobFailureOutcome OnFailure(int attemptsMade, bool transient, DateTimeOffset now, RetryPolicy? policy = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsMade, 1);
        policy ??= RetryPolicy.Default;

        if (!transient)
        {
            return new JobFailureOutcome(JobStatus.Failed, null);
        }

        return policy.ShouldRetry(attemptsMade)
            ? new JobFailureOutcome(JobStatus.Queued, now + policy.DelayBeforeRetry(attemptsMade))
            : new JobFailureOutcome(JobStatus.Dead, null);
    }

    /// <summary>The longest a job waits for an external rate limit to reset before it runs again (§4.14, NFR-REL).</summary>
    public static readonly TimeSpan MaxDeferral = TimeSpan.FromHours(1);

    /// <summary>
    /// §4.14, NFR-REL: an attempt stopped by an external rate limit (e.g. GitHub's) is not a failure: the job is re-queued
    /// without counting the attempt, to run when the limit resets — at least a second from now and at most
    /// <see cref="MaxDeferral"/> (a reset further out is checked again then).
    /// </summary>
    public static DateTimeOffset DeferUntil(DateTimeOffset now, DateTimeOffset retryAt)
    {
        var earliest = now + TimeSpan.FromSeconds(1);
        var latest = now + MaxDeferral;
        return retryAt < earliest ? earliest : retryAt > latest ? latest : retryAt;
    }

    public static bool IsLeaseExpired(JobStatus status, DateTimeOffset? lockedAt, DateTimeOffset now) =>
        status == JobStatus.Running && lockedAt is { } locked && now - locked >= LeaseDuration;

    /// <summary>
    /// NFR-REL: a job whose lease expired counts as a failed (transient) attempt, so it is re-queued — and a
    /// job that keeps killing its worker still ends up as a dead letter.
    /// </summary>
    public static JobFailureOutcome OnLeaseExpired(int attemptsMade, DateTimeOffset now, RetryPolicy? policy = null) =>
        OnFailure(Math.Max(1, attemptsMade), transient: true, now, policy);

    /// <summary>
    /// FR-51: retrying a dead letter marks it <c>retried</c> and creates a new queued job with the same type,
    /// payload and project, a fresh attempt count, runnable now. The dead row stays as history.
    /// <paramref name="executionJobActive"/>: for an execution job, whether the project already has a queued or running
    /// execution job (§4.9 allows one); then the retry is refused with <c>CONFLICT</c> and nothing changes.
    /// </summary>
    public static Result<DeadLetterRetry> RetryDeadLetter(JobSnapshot deadJob, DateTimeOffset now, bool executionJobActive = false)
    {
        ArgumentNullException.ThrowIfNull(deadJob);
        if (deadJob.Status != JobStatus.Dead)
        {
            return Result.Failure<DeadLetterRetry>(new DomainError(ErrorCodes.Conflict, $"Only dead jobs can be retried; this one is {deadJob.Status.ToWire()}."));
        }

        if (executionJobActive && deadJob.Type == JobTypes.Execution)
        {
            return Result.Failure<DeadLetterRetry>(new DomainError(
                ErrorCodes.Conflict,
                "An execution job is already queued or running for this project; retry this dead letter after it finishes."));
        }

        return Result.Success(new DeadLetterRetry(
            JobStatus.Retried,
            new NewJob(deadJob.Type, deadJob.Payload, deadJob.ProjectId, JobStatus.Queued, Attempts: 0, RunAfter: now)));
    }

    /// <summary>Rollback cancels queued jobs; running and finished jobs are left alone.</summary>
    public static JobStatus Cancel(JobStatus status) => status == JobStatus.Queued ? JobStatus.Cancelled : status;
}
