using Aide.Domain.Jobs;
using Aide.Domain.Review;

namespace Aide.UnitTests.Review;

/// <summary>FR-38 review status in the §6 project state.</summary>
public class ReviewStatusRulesTests
{
    [Theory]
    [InlineData(JobStatus.Queued, 0)]
    [InlineData(JobStatus.Running, 2)]
    public void FR38_ActiveReviewJob_IsRunning(JobStatus job, int cycle) =>
        Assert.Equal(ReviewRunStatus.Running, ReviewStatusRules.Status(job, cycle));

    [Fact]
    public void FR38_SucceededJob_WithCycle_IsCompleted() =>
        Assert.Equal(ReviewRunStatus.Completed, ReviewStatusRules.Status(JobStatus.Succeeded, 1));

    [Theory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Dead)]
    public void FR38_FailedJob_WithCycle_IsFailed(JobStatus job) =>
        Assert.Equal(ReviewRunStatus.Failed, ReviewStatusRules.Status(job, 1));

    [Theory]
    [InlineData(null, 0)]
    [InlineData(JobStatus.Cancelled, 1)]
    [InlineData(JobStatus.Retried, 1)]
    public void FR38_NoOrCancelledJob_IsIdle(JobStatus? job, int cycle) =>
        Assert.Equal(ReviewRunStatus.Idle, ReviewStatusRules.Status(job, cycle));

    [Theory]
    [InlineData(JobStatus.Succeeded)]
    [InlineData(JobStatus.Failed)]
    public void S4_8_RollbackClearedCycle_IsIdle(JobStatus job) =>
        Assert.Equal(ReviewRunStatus.Idle, ReviewStatusRules.Status(job, 0));
}
