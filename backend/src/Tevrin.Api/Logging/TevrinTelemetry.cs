using System.Diagnostics;

namespace Tevrin.Api.Logging;

/// <summary>
/// NFR-OBS: the app's own trace source and the correlation tags its spans carry. The same ids are log scope values
/// (<c>ProjectId</c>, <c>JobId</c>, <c>RunId</c>, …), so a JSON log line and its span can be joined on them.
/// Only ids go here — never names, emails, tokens or payloads (NFR-SEC).
/// </summary>
public static class TevrinTelemetry
{
    public const string SourceName = "Tevrin";

    public const string ProjectIdTag = "tevrin.project_id";
    public const string JobIdTag = "tevrin.job_id";
    public const string JobTypeTag = "tevrin.job_type";
    public const string JobAttemptTag = "tevrin.job_attempt";
    public const string RunIdTag = "tevrin.run_id";
    public const string AgentTag = "tevrin.agent";
    public const string UserIdTag = "tevrin.user_id";

    /// <summary>§8: the pipeline run an agent run belongs to, and its stage.</summary>
    public const string PipelineRunIdTag = "tevrin.pipeline_run_id";
    public const string StageTag = "tevrin.stage";

    /// <summary>How a job attempt ended: one of <see cref="Tevrin.Engine.Runtime.Jobs.JobOutcomes"/>.</summary>
    public const string JobOutcomeTag = "tevrin.job_outcome";

    /// <summary>The run's status when the runner returned or threw (<c>running</c> while a transient failure is retried,
    /// <c>not_started</c> when no run row exists, e.g. a refused budget).</summary>
    public const string RunStatusTag = "tevrin.run_status";

    /// <summary>The spec error code (or exception type name) of a failed span; also its status description.</summary>
    public const string ErrorCodeTag = "tevrin.error_code";

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
