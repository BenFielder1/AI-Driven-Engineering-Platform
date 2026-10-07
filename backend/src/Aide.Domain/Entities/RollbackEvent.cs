namespace Aide.Domain.Entities;

/// <summary>A recorded rollback (FR-44, §4.8).</summary>
public sealed class RollbackEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public int FromVersion { get; set; }

    public int TargetVersion { get; set; }

    public string Reason { get; set; } = string.Empty;

    public IReadOnlyList<Guid> InvalidatedArtifactIds { get; set; } = [];

    public IReadOnlyList<Guid> ResetStepIds { get; set; } = [];

    public string TriggeredBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
