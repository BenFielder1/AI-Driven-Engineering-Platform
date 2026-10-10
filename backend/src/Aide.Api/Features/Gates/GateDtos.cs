using System.ComponentModel.DataAnnotations;
using Aide.Api.Features.Activity;
using Aide.Domain.Artifacts;
using Aide.Domain.Gates;
using Aide.Domain.Review;

namespace Aide.Api.Features.Gates;

/// <summary>
/// An approval gate (§4.2, FR-12–FR-15). <c>artifactId</c>, <c>artifactType</c> and <c>artifactVersion</c> identify
/// the gated artifact (spec and plan gates). Escalation gates carry <c>escalationReason</c> and, once decided,
/// <c>decision</c> (and <c>targetVersion</c> for a rollback). <c>reason</c> is a human-readable context line.
/// <c>decidedBy</c>, <c>decidedAt</c> and <c>comment</c> are set once the gate is decided. <c>overdue</c> is true
/// 72 hours after creation while pending (FR-15); <c>remindedAt</c> is when its 24-hour reminder was sent. An escalation raised by a blocked execution step (FR-33) has
/// <c>escalationReason</c> <c>step_blocked</c> and names the step in <c>executionStepId</c> and <c>stepNumber</c>
/// (both <c>null</c> otherwise).
/// </summary>
public sealed record GateDto(
    Guid Id,
    GateType Type,
    GateStatus Status,
    Guid? ArtifactId,
    ArtifactType? ArtifactType,
    int? ArtifactVersion,
    EscalationReason? EscalationReason,
    string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    ActorDto? DecidedBy,
    string? Comment,
    EscalationDecision? Decision,
    int? TargetVersion,
    bool Overdue,
    DateTimeOffset? RemindedAt,
    Guid? ExecutionStepId,
    int? StepNumber);

/// <summary>
/// Approve or reject a pending gate (FR-13, FR-14). <c>comment</c> is optional (on reject it is the feedback for the
/// re-run agent). Escalation gates are approved with a <c>decision</c>; <c>rollback</c> also needs a
/// <c>targetVersion</c>. Other gates take neither (400 <c>INVALID_DECISION</c>), and escalation gates can't be rejected.
/// </summary>
public sealed record DecideGateRequest
{
    [MaxLength(GateLimits.CommentMaxLength)]
    public string? Comment { get; init; }

    public EscalationDecision? Decision { get; init; }

    [Range(1, int.MaxValue)]
    public int? TargetVersion { get; init; }
}

/// <summary>Request limits of the gates feature.</summary>
public static class GateLimits
{
    public const int CommentMaxLength = 4000;
}
