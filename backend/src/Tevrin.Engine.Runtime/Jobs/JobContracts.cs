using Microsoft.Extensions.DependencyInjection;
using Tevrin.Engine.Domain.Jobs;

namespace Tevrin.Engine.Runtime.Jobs;

/// <summary>Job worker settings (section <c>Jobs</c>, §4.9, NFR-REL).</summary>
public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Run the background worker loop. Tests turn it off and drive <c>JobWorker.RunOnceAsync</c>.</summary>
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

/// <summary>
/// A handler for one job type. Only registered types are claimed by the worker, and only registered types can be enqueued
/// (FR-272, <see cref="JobTypeRegistry"/>). <see cref="OnePerProject"/>: at most one job of the type is queued or running
/// per project (§4.9, e.g. the SDLC's <c>execution</c>); <see cref="JobEnqueuer.EnqueueAsync"/> returns the active one
/// instead of adding another, and a dead letter isn't retried while one is active.
/// </summary>
public sealed record JobHandlerRegistration(string JobType, Func<IServiceProvider, IJobHandler> Factory, bool OnePerProject = false);

/// <summary>
/// FR-272: the job types with a registered handler (<see cref="JobHandlerRegistration"/>): the engine's
/// (<c>EngineJobTypes</c>) once it handles them, plus every type a pack registers. A type is one per project when its
/// registration says so.
/// </summary>
public sealed class JobTypeRegistry
{
    private readonly Dictionary<string, bool> _types = new(StringComparer.Ordinal);

    public JobTypeRegistry(IEnumerable<JobHandlerRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var registration in registrations)
        {
            _types[registration.JobType] = registration.OnePerProject || (_types.TryGetValue(registration.JobType, out var one) && one);
        }
    }

    /// <summary>Every registered job type.</summary>
    public IReadOnlyCollection<string> Types => _types.Keys;

    /// <summary>Whether a handler is registered for <paramref name="jobType"/> (ordinal).</summary>
    public bool IsRegistered(string jobType) => _types.ContainsKey(jobType);

    /// <summary>Whether <paramref name="jobType"/> allows at most one queued or running job per project (§4.9).</summary>
    public bool IsOnePerProject(string jobType) => _types.TryGetValue(jobType, out var one) && one;
}

public static class JobServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="THandler"/> (scoped) as the handler of <paramref name="jobType"/>, so the type can be
    /// enqueued and claimed (FR-272). <paramref name="onePerProject"/>: at most one job of the type queued or running per
    /// project (§4.9).
    /// </summary>
    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services, string jobType, bool onePerProject = false)
        where THandler : class, IJobHandler
    {
        ArgumentException.ThrowIfNullOrEmpty(jobType);
        services.AddScoped<THandler>();
        services.AddSingleton(new JobHandlerRegistration(jobType, sp => sp.GetRequiredService<THandler>(), onePerProject));
        return services;
    }

    /// <summary>
    /// The job queue (outbox <see cref="JobEnqueuer"/>, scoped) and the background <see cref="JobWorker"/> (§4.9). The host
    /// registers its <see cref="Persistence.IEngineDbContext"/>, <see cref="IJobPayloadSerializer"/>,
    /// <see cref="JobTelemetry"/>, any <see cref="IJobExceptionClassifier"/>s and the <see cref="JobOptions"/> binding.
    /// </summary>
    public static IServiceCollection AddTevrinJobQueue(this IServiceCollection services)
    {
        services.AddSingleton<JobTypeRegistry>();
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

    /// <summary>
    /// As <see cref="JobDeferredException(string, DateTimeOffset)"/>, replacing the job's payload with
    /// <paramref name="payload"/> (raw JSON) for the next run, e.g. the merge poll's ETag or when review started waiting for
    /// checks (§4.14) — so the state survives a restart.
    /// </summary>
    public JobDeferredException(string message, DateTimeOffset runAfter, string? payload)
        : this(message, runAfter)
    {
        Payload = payload;
    }

    public DateTimeOffset RunAfter { get; }

    /// <summary>The payload for the next run (raw JSON), or <c>null</c> to keep the current one.</summary>
    public string? Payload { get; }
}
