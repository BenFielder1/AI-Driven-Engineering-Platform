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

    /// <summary>
    /// The span of a stage execution, emitted when the execution closes: it starts at <c>entered_at</c> and ends at
    /// <c>exited_at</c>, tagged with the run, stage, iteration and outcome. A stage can stay open for days across requests,
    /// so the span is recorded with its real times when it closes rather than held open.
    /// </summary>
    public static void TraceStageExecution(StageExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.ExitedAt is not { } exited)
        {
            return;
        }

        using var activity = Source.StartActivity(
            $"stage {execution.StageId}", ActivityKind.Internal, parentContext: default, startTime: execution.EnteredAt);
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
