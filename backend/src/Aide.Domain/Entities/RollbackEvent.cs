using Aide.Domain.Lifecycle;

namespace Aide.Domain.Entities;

/// <summary>A recorded rollback (FR-44, §4.8).</summary>
public sealed class RollbackEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public int FromVersion { get; set; }

    public int TargetVersion { get; set; }

    /// <summary>The phase the project was restored to (the rollback transition's target phase).</summary>
    public Phase RestoredPhase { get; set; }

    public string Reason { get; set; } = string.Empty;

    public IReadOnlyList<Guid> InvalidatedArtifactIds { get; set; } = [];

    /// <summary>Steps invalidated because their plan was invalidated.</summary>
    public IReadOnlyList<Guid> ResetStepIds { get; set; } = [];

    /// <summary>Blocked or rejected steps of surviving plans put back to <c>pending</c>.</summary>
    public IReadOnlyList<Guid> RequeuedStepIds { get; set; } = [];

    /// <summary>Pending approval gates cancelled by the rollback.</summary>
    public IReadOnlyList<Guid> CancelledGateIds { get; set; } = [];

    /// <summary>Queued jobs cancelled by the rollback.</summary>
    public IReadOnlyList<Guid> CancelledJobIds { get; set; } = [];

    public string TriggeredBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
