using Aide.Domain.Common;
using Aide.Domain.Jobs;

namespace Aide.UnitTests.Jobs;

/// <summary>§4.9, FR-25, FR-51, NFR-REL.</summary>
public class JobPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 8)]
    [InlineData(3, 32)]
    public void S4_9_Exponential_backoff_values(int retry, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), RetryPolicy.Default.DelayBeforeRetry(retry));

    [Fact]
    public void FR25_Provider_failing_four_times_makes_four_attempts_then_dead_letters()
    {
        var attempts = 0;
        JobFailureOutcome outcome;
        do
        {
            attempts++;
            outcome = JobPolicy.OnFailure(attempts, transient: true, Now);
        }
        while (outcome.Status == JobStatus.Queued);

        Assert.Equal(4, attempts);
        Assert.True(outcome.IsDeadLetter);
        Assert.Null(outcome.RunAfter);
    }

    [Fact]
    public void S4_9_Retry_is_scheduled_after_the_backoff()
    {
        var outcome = JobPolicy.OnFailure(2, transient: true, Now);

        Assert.Equal(new JobFailureOutcome(JobStatus.Queued, Now.AddSeconds(8)), outcome);
    }

    [Fact]
    public void Non_transient_failures_fail_without_retry() =>
        Assert.Equal(new JobFailureOutcome(JobStatus.Failed, null), JobPolicy.OnFailure(1, transient: false, Now));

    [Theory]
    [InlineData(JobStatus.Running, 5, true)]
    [InlineData(JobStatus.Running, 6, true)]
    [InlineData(JobStatus.Running, 4, false)]
    [InlineData(JobStatus.Queued, 10, false)]
    [InlineData(JobStatus.Succeeded, 10, false)]
    public void NFR_REL_Running_jobs_are_requeued_after_5_minute_lease(JobStatus status, int minutesAgo, bool expired) =>
        Assert.Equal(expired, JobPolicy.IsLeaseExpired(status, Now.AddMinutes(-minutesAgo), Now));

    [Fact]
    public void NFR_REL_Expired_lease_requeues_and_eventually_dead_letters()
    {
        Assert.Equal(JobStatus.Queued, JobPolicy.OnLeaseExpired(1, Now).Status);
        Assert.Equal(JobStatus.Dead, JobPolicy.OnLeaseExpired(4, Now).Status);
    }

    [Fact]
    public void FR51_Retrying_a_dead_letter_marks_it_retried_and_creates_a_new_queued_job()
    {
        var projectId = Guid.NewGuid();
        var dead = new JobSnapshot(Guid.NewGuid(), "agent_run", "{\"agent\":\"spec_generator\"}", projectId, JobStatus.Dead);

        var result = JobPolicy.RetryDeadLetter(dead, Now);

        Assert.Equal(JobStatus.Retried, result.Value.DeadJobStatus);
        Assert.Equal(new NewJob("agent_run", dead.Payload, projectId, JobStatus.Queued, 0, Now), result.Value.Replacement);
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Succeeded)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Retried)]
    public void FR51_Only_dead_jobs_can_be_retried(JobStatus status) =>
        Assert.Equal(ErrorCodes.Conflict, JobPolicy.RetryDeadLetter(new JobSnapshot(Guid.NewGuid(), "t", "{}", null, status), Now).Error!.Code);

    [Theory]
    [InlineData(JobStatus.Queued, JobStatus.Cancelled)]
    [InlineData(JobStatus.Running, JobStatus.Running)]
    [InlineData(JobStatus.Succeeded, JobStatus.Succeeded)]
    [InlineData(JobStatus.Dead, JobStatus.Dead)]
    public void FR44_Rollback_cancels_only_queued_jobs(JobStatus status, JobStatus expected) =>
        Assert.Equal(expected, JobPolicy.Cancel(status));
}
