using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Api;

/// <summary>FR-343, FR-363: a parallel group's branch: its id (as stage executions' <c>branch</c>) and its stages in order.</summary>
public sealed record PipelineBranchDefinitionDto(string Id, IReadOnlyList<PipelineStageDefinitionDto> Stages);

/// <summary>FR-345, FR-363: a fan-out: one branch per agent key, or <c>count</c> branches (<c>#1</c>…) of the stage's agent.</summary>
public sealed record PipelineFanOutDefinitionDto(IReadOnlyList<string> Agents, int? Count);

/// <summary>FR-349, FR-363: output variants <c>v1</c>…<c>v{count}</c>, chosen by <c>selectAgent</c> or (when it is
/// <c>null</c>) a <c>variant_select</c> gate.</summary>
public sealed record PipelineVariantsDefinitionDto(int Count, string? SelectAgent, IReadOnlyList<string> Labels);

/// <summary>FR-355, FR-363: a map over <c>over</c> by an agent or handler, its limits, and its reduce step.</summary>
public sealed record PipelineMapDefinitionDto(
    string Over,
    string? Agent,
    string? Handler,
    int MaxParallel,
    int MaxItems,
    MapItemFailure OnItemFailure,
    string MaxFailed,
    string? ReduceAgent,
    string? ReduceHandler);

/// <summary>FR-356, FR-363: a turn-based conversation: its participants (agent and role) in turn order and its turn cap.</summary>
public sealed record PipelineConversationDefinitionDto(IReadOnlyList<PipelineConversationParticipantDto> Participants, int MaxTurns);

/// <summary>FR-356: one participant of a conversation.</summary>
public sealed record PipelineConversationParticipantDto(string Agent, string Role);

/// <summary>FR-336, FR-337, FR-362: a trigger the definition declares (<c>manual</c>, <c>schedule</c> or <c>webhook</c>)
/// and, for a webhook, the input artifact its body becomes.</summary>
public sealed record PipelineTriggerDefinitionDto(TriggerKind Kind, string? Input);

/// <summary>FR-354, FR-363: a follow-up stage and the durations after completion it runs at (e.g. <c>7d</c>).</summary>
public sealed record PipelineFollowUpDefinitionDto(string Stage, IReadOnlyList<string> After);

/// <summary>
/// FR-354, FR-363: one follow-up of a run: its stage and label (<c>followup:&lt;duration&gt;</c>), when it is due
/// (<c>null</c> until the run completes, which schedules it) and, once it has run, its stage execution's times and outcome.
/// </summary>
public sealed record PipelineRunFollowUpDto(
    string Stage,
    string Label,
    DateTimeOffset? DueAt,
    Guid? StageExecutionId,
    DateTimeOffset? EnteredAt,
    DateTimeOffset? ExitedAt,
    string? Outcome);

/// <summary>FR-366: a cron and timezone's next fire times (UTC), before the schedule is saved.</summary>
public sealed record PipelineSchedulePreviewDto(string Cron, string Timezone, IReadOnlyList<DateTimeOffset> NextFireTimes);

/// <summary>FR-363: maps the 0.8.0 constructs of a stage definition to their DTOs.</summary>
internal static class PipelineConstructDtos
{
    public static PipelineFanOutDefinitionDto? FanOut(StageDefinition stage) =>
        stage.FanOut is { } f ? new PipelineFanOutDefinitionDto(f.Agents, f.Count) : null;

    public static PipelineVariantsDefinitionDto? Variants(StageDefinition stage) =>
        stage.Variants is { } v ? new PipelineVariantsDefinitionDto(v.Count, v.SelectAgent, VariantRules.Labels(stage)) : null;

    public static PipelineMapDefinitionDto? Map(StageDefinition stage) =>
        stage.Map is { } m
            ? new PipelineMapDefinitionDto(m.Over, m.Agent, m.Handler, m.MaxParallel, m.MaxItems, m.OnItemFailure, m.MaxFailed.ToWire(), stage.Reduce?.Agent, stage.Reduce?.Handler)
            : null;

    public static PipelineConversationDefinitionDto? Conversation(StageDefinition stage) =>
        stage.Conversation is { } c
            ? new PipelineConversationDefinitionDto([.. c.Participants.Select(p => new PipelineConversationParticipantDto(p.Agent, p.Role))], c.TurnLimit)
            : null;
}
