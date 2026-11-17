using Aide.Infrastructure.Persistence;
using Tevrin.Engine.Runtime.Jobs;

namespace Aide.Api.Jobs;

/// <summary>
/// Cleans up after a job that ended without succeeding (e.g. fails agent runs the job left <c>running</c>, FR-25). Called
/// by the worker on both the handler path and the lease reaper, with the worker's <see cref="AideDbContext"/> before it
/// saves: changes are recorded in the same transaction as the job's final status. Must be idempotent.
/// </summary>
public interface IJobEndedHook
{
    Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken);
}

/// <summary>
/// Follow-up work of a job that succeeded (e.g. queueing the next execution step, §4.9). Called by the worker with its
/// <see cref="AideDbContext"/> in the same transaction that marks the job succeeded, so the job's row is already
/// <c>succeeded</c> when the hook queries jobs. Changes are saved by the worker. If saving them fails (e.g. a concurrent
/// writer queued the same execution job), the job is still marked succeeded without them. Must be idempotent.
/// </summary>
public interface IJobSucceededHook
{
    Task OnJobSucceededAsync(AideDbContext db, JobExecution job, CancellationToken cancellationToken);
}

public static class AideJobServiceCollectionExtensions
{
    /// <summary>The job queue (outbox enqueuer) and the background worker (§4.9).</summary>
    public static IServiceCollection AddAideJobs(this IServiceCollection services)
    {
        services.AddOptions<JobOptions>()
            .BindConfiguration(JobOptions.SectionName)
            .Validate(o => o.Concurrency >= 1, "Jobs:Concurrency must be at least 1.")
            .Validate(o => o.PollInterval > TimeSpan.Zero, "Jobs:PollInterval must be positive.")
            .Validate(o => o.LeaseDuration > TimeSpan.Zero, "Jobs:LeaseDuration must be positive.")
            .ValidateOnStart();
        services.AddScoped<JobEnqueuer>();
        services.AddSingleton<JobWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());
        return services;
    }
}
