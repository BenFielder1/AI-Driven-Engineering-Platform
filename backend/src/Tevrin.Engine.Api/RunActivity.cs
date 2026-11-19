using System.Linq.Expressions;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;

namespace Tevrin.Engine.Api;

/// <summary>§4.57: the jobs that make a non-final run <c>running</c> (translated to SQL by EF).</summary>
public static class RunActivity
{
    /// <summary>Queued or running jobs of <paramref name="projectId"/> whose type isn't in <paramref name="ignoredJobTypes"/>.</summary>
    public static Expression<Func<Job, bool>> ActiveJobsOf(Guid projectId, IReadOnlyCollection<string> ignoredJobTypes)
    {
        ArgumentNullException.ThrowIfNull(ignoredJobTypes);
        var ignored = ignoredJobTypes.ToArray();
        return j => j.ProjectId == projectId
            && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
            && !ignored.Contains(j.Type);
    }
}
