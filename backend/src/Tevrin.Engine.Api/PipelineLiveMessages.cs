namespace Tevrin.Engine.Api;

/// <summary>
/// FR-363 live (§6 hub events): a parallel-branch or fan-out-branch stage execution was entered or left. Ids and state
/// only, never content: the client refreshes the run's timeline, which the lanes read.
/// </summary>
/// <param name="ProjectId">The run's project (the hub group).</param>
/// <param name="RunId">The pipeline run.</param>
/// <param name="ExecutionId">The branch's stage execution.</param>
/// <param name="StageId">The stage the branch is in (a parallel branch's own stage, or the fan-out stage).</param>
/// <param name="Branch">The branch id (<c>0</c>, <c>1</c>… for a parallel group; an agent key or <c>#n</c> for a fan-out).</param>
/// <param name="ExitedAt">When the branch left the stage; <c>null</c> while it is open.</param>
/// <param name="Outcome">How it left (a transition kind, <c>completed</c> or <c>failed</c>); <c>null</c> while open.</param>
public sealed record PipelineStageBranchUpdatedMessage(
    Guid ProjectId,
    Guid RunId,
    Guid ExecutionId,
    string StageId,
    string Branch,
    DateTimeOffset? ExitedAt,
    string? Outcome);

/// <summary>
/// FR-363 live (§6 hub events): a map execution's progress changed (items opened or ended), counted from its item rows
/// (D-0.8.0-24). Throttled per map execution (at most one a second, then a trailing one with the latest counts).
/// </summary>
/// <param name="ProjectId">The run's project (the hub group).</param>
/// <param name="RunId">The pipeline run.</param>
/// <param name="StageId">The map stage (the stage list entry whose <c>map</c> this replaces).</param>
/// <param name="Progress">The map execution's total, completed and failed item counts.</param>
public sealed record PipelineMapProgressMessage(Guid ProjectId, Guid RunId, string StageId, PipelineMapProgressDto Progress);
