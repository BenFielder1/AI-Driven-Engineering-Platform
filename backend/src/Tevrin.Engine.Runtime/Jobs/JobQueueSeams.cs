using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Runtime.Jobs;

/// <summary>
/// D-0.7.0-20: tells the <see cref="JobWorker"/> that a handler failure is a wait, not a failure (e.g. a Git host's rate
/// limit): the job is re-queued for <see cref="JobDeferralHint.RetryAt"/> (clamped by <c>JobPolicy.DeferUntil</c>) without
/// counting the attempt. Registered by the host; the first classifier that returns a hint wins.
/// </summary>
public interface IJobExceptionClassifier
{
    /// <summary>The deferral for <paramref name="exception"/>, or <c>null</c> when this classifier doesn't recognise it.</summary>
    JobDeferralHint? Classify(Exception exception);
}

/// <summary>
/// A deferral: when to retry, the error code recorded on the span, the job's <c>last_error</c> for the clamped run time
/// (<see cref="Describe"/>) and the log entry written with the worker's logger (<see cref="Log"/>).
/// </summary>
public sealed record JobDeferralHint(
    DateTimeOffset RetryAt,
    string ErrorCode,
    Func<DateTimeOffset, string> Describe,
    Action<ILogger, DateTimeOffset> Log);

/// <summary>
/// NFR-OBS, D-0.7.0-20: where the <see cref="JobWorker"/> writes its spans and log scopes. The host registers one with its
/// own trace source, tag names and project scope key, so traces and logs keep their names. Only ids are tagged, never
/// payloads (NFR-SEC).
/// </summary>
public sealed class JobTelemetry(ActivitySource source)
{
    public ActivitySource Source { get; } = source;

    public required string JobIdTag { get; init; }

    public required string JobTypeTag { get; init; }

    public required string JobAttemptTag { get; init; }

    public required string ProjectIdTag { get; init; }

    /// <summary>How a job attempt ended: one of <see cref="JobOutcomes"/>.</summary>
    public required string JobOutcomeTag { get; init; }

    /// <summary>The error code (or exception type name) of a failed span; also its status description.</summary>
    public required string ErrorCodeTag { get; init; }

    /// <summary>The log scope key of the job's project id (present only for project jobs).</summary>
    public required string ProjectIdScopeKey { get; init; }

    /// <summary>
    /// Marks <paramref name="activity"/> as failed. <paramref name="errorCode"/> must be an error code or an exception type
    /// name — never an exception message, which can quote requests, payloads or keys (NFR-SEC).
    /// </summary>
    public void SetError(Activity? activity, string errorCode)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(ErrorCodeTag, errorCode);
        activity.SetStatus(ActivityStatusCode.Error, errorCode);
    }
}

/// <summary>NFR-OBS: the values of <see cref="JobTelemetry.JobOutcomeTag"/>.</summary>
public static class JobOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Retry = "retry";
    public const string Failed = "failed";
    public const string Dead = "dead";
    public const string Cancelled = "cancelled";
}

/// <summary>Serialises job payloads to the <c>jobs.payload</c> JSON (the host's JSON conventions, D-0.7.0-20).</summary>
public interface IJobPayloadSerializer
{
    string Serialize(object payload);
}

/// <summary>
/// Cleans up after a job that ended without succeeding (e.g. fails agent runs the job left <c>running</c>, FR-25). Called
/// by the worker on both the handler path and the lease reaper, with the worker's <see cref="IEngineDbContext"/> before it
/// saves: changes are recorded in the same transaction as the job's final status. Must be idempotent.
/// </summary>
public interface IJobEndedHook
{
    Task OnJobEndedAsync(IEngineDbContext engineDb, JobEnded job, CancellationToken cancellationToken);
}

/// <summary>
/// Follow-up work of a job that succeeded (e.g. queueing the next execution step, §4.9). Called by the worker with its
/// <see cref="IEngineDbContext"/> in the same transaction that marks the job succeeded, so the job's row is already
/// <c>succeeded</c> when the hook queries jobs. Changes are saved by the worker. If saving them fails (e.g. a concurrent
/// writer queued the same execution job), the job is still marked succeeded without them. Must be idempotent.
/// </summary>
public interface IJobSucceededHook
{
    Task OnJobSucceededAsync(IEngineDbContext engineDb, JobExecution job, CancellationToken cancellationToken);
}
