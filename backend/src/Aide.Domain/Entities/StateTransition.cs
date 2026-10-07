using Aide.Domain.Lifecycle;

namespace Aide.Domain.Entities;

/// <summary>An applied lifecycle transition (FR-11); the source for rollback phase restoration (§4.8).</summary>
public sealed class StateTransition
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    /// <summary>Null for the creation record (→ refinement at version 1).</summary>
    public Phase? FromPhase { get; set; }

    public Phase ToPhase { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    /// <summary>The project version after the transition.</summary>
    public int ProjectVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
