using Aide.Domain.Jobs;

namespace Aide.Domain.Review;

/// <summary>Derives <see cref="ReviewRunStatus"/> from the project's latest review job (FR-38, §6 project state).</summary>
public static class ReviewStatusRules
{
    /// <summary>
    /// A queued or running review job is <c>running</c>. Otherwise, while a review cycle is recorded
    /// (<paramref name="reviewCycle"/> &gt; 0; a rollback resets it), a succeeded job is <c>completed</c> and a failed or
    /// dead-lettered one is <c>failed</c>. No job, a cancelled job, or no recorded cycle is <c>idle</c>.
    /// </summary>
    public static ReviewRunStatus Status(JobStatus? latestReviewJob, int reviewCycle) => latestReviewJob switch
    {
        JobStatus.Queued or JobStatus.Running => ReviewRunStatus.Running,
        _ when reviewCycle <= 0 => ReviewRunStatus.Idle,
        JobStatus.Succeeded => ReviewRunStatus.Completed,
        JobStatus.Failed or JobStatus.Dead => ReviewRunStatus.Failed,
        _ => ReviewRunStatus.Idle,
    };
}
