using Tevrin.Engine.Api.Hosting;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Api;

/// <summary>FR-300, FR-303: timeline entries from their source rows.</summary>
public static class TimelineItems
{
    /// <summary>FR-303: the replay's cap on each agent run's input and output (as the Runs page's output cap).</summary>
    public const int MaxReplayTextLength = 200_000;

    public static TimelineKey KeyOf(PipelineTimelineItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new TimelineKey(item.At, item.Kind, item.Id);
    }

    public static PipelineTimelineItemDto Transition(PipelineTransitionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new PipelineTimelineItemDto
        {
            Id = row.Id,
            At = row.At,
            Kind = PipelineTimelineKind.Transition,
            FromStage = row.FromStage,
            ToStage = row.ToStage,
            TransitionKind = row.Kind,
            Checkpoint = row.Checkpoint,
            Reason = row.Reason,
        };
    }

    public static PipelineTimelineItemDto Stage(StageExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return new PipelineTimelineItemDto
        {
            Id = execution.Id,
            At = execution.EnteredAt,
            Kind = PipelineTimelineKind.StageEntered,
            StageId = execution.StageId,
            Iteration = execution.Iteration,
            Checkpoint = execution.Checkpoint,
            ExitedAt = execution.ExitedAt,
            Outcome = execution.Outcome,
        };
    }

    /// <summary>An agent run; with <paramref name="replay"/> its input and output text (FR-303).</summary>
    public static PipelineTimelineItemDto AgentRun(AgentRun run, bool replay)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (input, inputCut) = Cap(replay ? run.Input : null);
        var (output, outputCut) = Cap(replay ? PipelineDtoMapper.OutputText(run.Output) : null);
        return new PipelineTimelineItemDto
        {
            Id = run.Id,
            At = run.CreatedAt,
            Kind = PipelineTimelineKind.AgentRun,
            AgentRunId = run.Id,
            StageId = run.Stage,
            AgentKey = run.AgentKey ?? run.Agent,
            AgentRunStatus = run.Status,
            Model = run.Model,
            PromptVersion = run.PromptVersion,
            InputTokens = run.InputTokens,
            OutputTokens = run.OutputTokens,
            Cost = run.CostEstimate,
            DurationMs = run.DurationMs,
            LatencyMs = run.LatencyMs,
            Error = run.Error,
            Input = input,
            InputTruncated = replay ? run.InputTruncated || inputCut : null,
            Output = output,
            OutputTruncated = replay ? outputCut : null,
        };
    }

    private static (string? Text, bool Cut) Cap(string? text) =>
        text is { Length: > MaxReplayTextLength } ? (text[..MaxReplayTextLength], true) : (text, false);

    /// <summary>A gate's creation, or (<paramref name="decided"/>) its decision at <c>decidedAt</c>.</summary>
    public static PipelineTimelineItemDto Gate(ApprovalGate gate, bool decided)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return new PipelineTimelineItemDto
        {
            Id = gate.Id,
            At = decided ? gate.DecidedAt ?? gate.CreatedAt : gate.CreatedAt,
            Kind = decided ? PipelineTimelineKind.GateDecided : PipelineTimelineKind.GateCreated,
            GateId = gate.Id,
            GateType = gate.Type,
            GateStatus = decided ? gate.Status : Domain.Gates.GateStatus.Pending,
            ArtifactId = gate.ArtifactId,
            Decision = decided ? gate.Decision : null,
            Comment = decided ? gate.Comment : null,
        };
    }

    internal static PipelineTimelineItemDto Artifact(ArtifactRow row) => new()
    {
        Id = row.Id,
        At = row.CreatedAt,
        Kind = PipelineTimelineKind.ArtifactVersion,
        ArtifactId = row.Id,
        ArtifactKey = row.Type,
        ArtifactVersion = row.Version,
        ArtifactStatus = row.Status,
        ContentHash = row.ContentHash,
        AgentRunId = row.AgentRunId,
    };
}
