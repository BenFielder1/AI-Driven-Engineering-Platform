using Aide.Domain.Jobs;
using Aide.Infrastructure.Persistence;

namespace Aide.Api.Jobs;

/// <summary>Job worker settings (section <c>Jobs</c>, §4.9, NFR-REL).</summary>
public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Run the background worker loop. Tests turn it off and drive <see cref="JobWorker.RunOnceAsync"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long the worker waits before polling again when there was nothing to do.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Jobs one worker runs at once; a free slot is filled on the next poll or as soon as a running job finishes.</summary>
    public int Concurrency { get; set; } = 4;

    /// <summary>
    /// A running job whose lease hasn't been renewed within this time is re-queued (NFR-REL: 5 minutes). The worker renews
    /// the lease of each job it runs every third of this, so only a dead or stalled worker loses its jobs.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = JobPolicy.LeaseDuration;
}

/// <summary>A claimed job, as handed to its <see cref="IJobHandler"/>. <see cref="Attempt"/> is 1-based.</summary>
public sealed record JobExecution(Guid Id, string Type, string Payload, Guid? ProjectId, int Attempt);

/// <summary>
/// Runs one job type. Handlers are idempotent (§4.9): a job can be delivered again after a lease expiry. Throw
/// <see cref="JobFailedException"/> with <c>transient: false</c> for failures a retry can't fix (e.g.
/// <c>MISSING_INPUT</c>); any other exception is a transient failure and is retried with backoff.
/// </summary>
public interface IJobHandler
{
    Task HandleAsync(JobExecution job, CancellationToken cancellationToken);
}

/// <summary>A job that ended without succeeding: dead-lettered (retries used up, or the lease expired on the last
/// attempt) or failed permanently. <see cref="Error"/> is the job's last error.</summary>
public sealed record JobEnded(Guid JobId, string JobType, Guid? ProjectId, bool DeadLettered, string? Error);

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

/// <summary>A handled job failure; <see cref="Transient"/> decides between retry and immediate failure (JobPolicy).</summary>
public sealed class JobFailedException : Exception
{
    public JobFailedException()
    {
    }

    public JobFailedException(string message)
        : base(message)
    {
    }

    public JobFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public JobFailedException(string message, bool transient, string? errorCode = null)
        : base(message)
    {
        Transient = transient;
        ErrorCode = errorCode;
    }

    public bool Transient { get; } = true;

    public string? ErrorCode { get; }
}

/// <summary>A handler for one job type. Only registered types are claimed by the worker.</summary>
public sealed record JobHandlerRegistration(string JobType, Func<IServiceProvider, IJobHandler> Factory);

public static class JobServiceCollectionExtensions
{
    /// <summary>Registers <typeparamref name="THandler"/> (scoped) as the handler of <paramref name="jobType"/>.</summary>
    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services, string jobType)
        where THandler : class, IJobHandler
    {
        ArgumentException.ThrowIfNullOrEmpty(jobType);
        services.AddScoped<THandler>();
        services.AddSingleton(new JobHandlerRegistration(jobType, sp => sp.GetRequiredService<THandler>()));
        return services;
    }

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

/// <summary>NFR-OBS: the log scope keys every log entry written while a job runs carries (plus <c>ProjectId</c> for
/// project jobs).</summary>
public static class JobScopeKeys
{
    public const string JobId = "JobId";
    public const string JobType = "JobType";
    public const string Attempt = "Attempt";
}

/// <summary>
/// A handler isn't ready to run yet (e.g. FR-70: refinement waiting for the codebase summary): the worker re-queues the
/// job to run at <see cref="RunAfter"/> without counting the attempt — a wait, never a blocking sleep and never a failure.
/// </summary>
public sealed class JobDeferredException : Exception
{
    public JobDeferredException()
    {
    }

    public JobDeferredException(string message)
        : base(message)
    {
    }

    public JobDeferredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public JobDeferredException(string message, DateTimeOffset runAfter)
        : base(message)
    {
        RunAfter = runAfter;
    }

    public DateTimeOffset RunAfter { get; }
}
