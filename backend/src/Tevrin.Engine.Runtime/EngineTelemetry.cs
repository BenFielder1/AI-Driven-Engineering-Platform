using System.Diagnostics;
using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Runtime;

/// <summary>
/// §8 observability: the engine's trace source and the tags its spans carry. The host adds <see cref="SourceName"/> to its
/// OpenTelemetry tracer (no exporter of its own). Only ids, keys and counts go here, never content (NFR-SEC).
/// </summary>
public static class EngineTelemetry
{
    public const string SourceName = "Tevrin.Engine";

    public const string PipelineRunIdTag = "tevrin.pipeline_run_id";
    public const string StageTag = "tevrin.stage";
    public const string IterationTag = "tevrin.iteration";
    public const string OutcomeTag = "tevrin.outcome";
    public const string AgentTag = "tevrin.agent";
    public const string AttemptTag = "tevrin.attempt";

    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>The error code (or exception type name) of a failed span; also its status description.</summary>
    public const string ErrorCodeTag = "tevrin.error_code";

    /// <summary>Marks <paramref name="activity"/> failed with <paramref name="errorCode"/>: an error code or an exception
    /// type name, never an exception message (NFR-SEC).</summary>
    public static void SetError(Activity? activity, string errorCode)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(ErrorCodeTag, errorCode);
        activity.SetStatus(ActivityStatusCode.Error, errorCode);
    }

    /// <summary>
    /// The span of a stage execution, emitted when the execution closes: it starts at <c>entered_at</c> and ends at
    /// <c>exited_at</c>, tagged with the run, stage, iteration and outcome. A stage can stay open for days across requests,
    /// so the span is recorded with its real times when it closes rather than held open. It is called once the closing save
    /// commits (<see cref="Persistence.StageExecutionTracingInterceptor"/>), and is a root span (its own trace) linked to the
    /// request or job span that closed it, since the stage started long before that span.
    /// </summary>
    public static void TraceStageExecution(StageExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.ExitedAt is not { } exited)
        {
            return;
        }

        var current = Activity.Current;
        Activity.Current = null; // a root span: with no current activity and no parent context, StartActivity has no parent
        try
        {
            using var activity = Source.StartActivity(
                $"stage {execution.StageId}",
                ActivityKind.Internal,
                parentContext: default,
                links: current is null ? null : [new ActivityLink(current.Context)],
                startTime: execution.EnteredAt);
            if (activity is null)
            {
                return;
            }

            activity.SetTag(PipelineRunIdTag, execution.PipelineRunId.ToString());
            activity.SetTag(StageTag, execution.StageId);
            activity.SetTag(IterationTag, execution.Iteration);
            activity.SetTag(OutcomeTag, execution.Outcome);
            activity.SetEndTime(exited.UtcDateTime);
        }
        finally
        {
            Activity.Current = current;
        }
    }

    /// <summary>The span of one agent stage attempt (closed by the caller), tagged with the run, stage, agent and attempt.</summary>
    public static Activity? StartAgentStage(Guid pipelineRunId, string stageId, string? agentKey, int attempt)
    {
        var activity = Source.StartActivity($"agent stage {stageId}", ActivityKind.Internal);
        activity?.SetTag(PipelineRunIdTag, pipelineRunId.ToString());
        activity?.SetTag(StageTag, stageId);
        activity?.SetTag(AgentTag, agentKey);
        activity?.SetTag(AttemptTag, attempt);
        return activity;
    }
}
