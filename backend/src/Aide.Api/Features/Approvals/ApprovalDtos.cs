using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Aide.Api.Features.Gates;
using Aide.Domain.Artifacts;
using Aide.Domain.Entities;
using Aide.Domain.Gates;

namespace Aide.Api.Features.Approvals;

/// <summary>
/// The state of a one-click approval link (FR-48). A 200 from <c>GetApprovalLink</c> carries <c>valid</c> (the link can
/// be confirmed) or <c>gate_closed</c> (the gate was decided or cancelled elsewhere; confirming answers
/// <c>GATE_NOT_PENDING</c>). <c>expired</c> and <c>used</c> name the states behind <c>LINK_EXPIRED</c> (410) and
/// <c>LINK_USED</c> (409), which are returned as ProblemDetails.
/// </summary>
public enum ApprovalLinkStatus
{
    [JsonStringEnumMemberName("valid")]
    Valid = 0,

    [JsonStringEnumMemberName("expired")]
    Expired = 1,

    [JsonStringEnumMemberName("used")]
    Used = 2,

    [JsonStringEnumMemberName("gate_closed")]
    GateClosed = 3,
}

/// <summary>The artifact a gate approves (spec and plan gates); <c>title</c> is its first heading, when it has one.</summary>
public sealed record ApprovalArtifactSummaryDto(ArtifactType Type, int Version, string? Title);

/// <summary>
/// What the approval confirmation page shows (FR-48): the gate, its project, the decision the link carries and, for spec
/// and plan gates, the gated artifact. One-click links exist only for spec_approval, plan_approval and pre_release gates.
/// </summary>
public sealed record ApprovalLinkViewDto(
    Guid GateId,
    Guid ProjectId,
    string ProjectName,
    GateType GateType,
    ApprovalLinkDecision Decision,
    ApprovalArtifactSummaryDto? ArtifactSummary,
    DateTimeOffset ExpiresAt,
    ApprovalLinkStatus Status);

/// <summary>Confirms the decision of a one-click approval link; <c>comment</c> is optional (feedback on reject).</summary>
public sealed record ConfirmApprovalLinkRequest
{
    [MaxLength(GateLimits.CommentMaxLength)]
    public string? Comment { get; init; }
}
