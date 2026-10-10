using System.Diagnostics;

namespace Aide.Api.Logging;

/// <summary>
/// NFR-OBS: the app's own trace source and the correlation tags its spans carry. The same ids are log scope values
/// (<c>ProjectId</c>, <c>JobId</c>, <c>RunId</c>, …), so a JSON log line and its span can be joined on them.
/// Only ids go here — never names, emails, tokens or payloads (NFR-SEC).
/// </summary>
public static class AideTelemetry
{
    public const string SourceName = "Aide";

    public const string ProjectIdTag = "aide.project_id";
    public const string JobIdTag = "aide.job_id";
    public const string JobTypeTag = "aide.job_type";
    public const string JobAttemptTag = "aide.job_attempt";
    public const string RunIdTag = "aide.run_id";
    public const string AgentTag = "aide.agent";
    public const string UserIdTag = "aide.user_id";

    /// <summary>How a job attempt ended: one of <see cref="JobOutcomes"/>.</summary>
    public const string JobOutcomeTag = "aide.job_outcome";

    /// <summary>The run's status when the runner returned or threw (<c>running</c> while a transient failure is retried,
    /// <c>not_started</c> when no run row exists, e.g. a refused budget).</summary>
    public const string RunStatusTag = "aide.run_status";

    /// <summary>The spec error code (or exception type name) of a failed span; also its status description.</summary>
    public const string ErrorCodeTag = "aide.error_code";

    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>
    /// Marks <paramref name="activity"/> as failed. <paramref name="errorCode"/> must be an error code or an exception type
    /// name — never an exception message, which can quote requests, payloads or keys (NFR-SEC).
    /// </summary>
    public static void SetError(Activity? activity, string errorCode)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(ErrorCodeTag, errorCode);
        activity.SetStatus(ActivityStatusCode.Error, errorCode);
    }
}

/// <summary>NFR-OBS: the values of <see cref="AideTelemetry.JobOutcomeTag"/>.</summary>
public static class JobOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Retry = "retry";
    public const string Failed = "failed";
    public const string Dead = "dead";
    public const string Cancelled = "cancelled";
}
