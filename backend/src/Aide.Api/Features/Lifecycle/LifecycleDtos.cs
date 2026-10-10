using System.ComponentModel.DataAnnotations;
using Aide.Api.Features.Gates;
using Aide.Api.Features.Projects;

namespace Aide.Api.Features.Lifecycle;

/// <summary>"Advance to next phase" (FR-12). <c>reason</c> is recorded on the state transition.</summary>
public sealed record AdvancePhaseRequest
{
    [MaxLength(LifecycleLimits.ReasonMaxLength)]
    public string? Reason { get; init; }
}

/// <summary>
/// Result of "advance to next phase" (FR-12). When the approval mode requires a gate for the next phase (§4.2),
/// <c>gate</c> is the pending gate that was created and <c>state</c> still shows the current phase; otherwise
/// <c>gate</c> is <c>null</c> and <c>state</c> shows the new phase.
/// </summary>
public sealed record AdvanceResultDto(ProjectStateDto State, GateDto? Gate);

/// <summary>Request limits of the lifecycle feature.</summary>
public static class LifecycleLimits
{
    public const int ReasonMaxLength = 1000;
}
