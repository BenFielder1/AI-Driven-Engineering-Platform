using Aide.Domain.Lifecycle;

namespace Aide.Domain.Entities;

/// <summary>An applied lifecycle transition (FR-11); the source for rollback phase restoration (§4.8).</summary>
public sealed class StateTransition
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    /// <summary>The release version the transition belongs to (§4.10).</summary>
    public Guid ReleaseVersionId { get; set; }

    /// <summary>FR-294: the pipeline run the transition belongs to (nullable until the contract step, D-0.7.0-28).</summary>
    public Guid? PipelineRunId { get; set; }

    /// <summary>§4.51: the generic transition kind (<c>PipelineTransitionKinds</c>); null on rows written before 0.7.0.</summary>
    public string? Kind { get; set; }

    /// <summary>Null for the creation record (→ refinement at checkpoint 1).</summary>
    public Phase? FromPhase { get; set; }

    public Phase ToPhase { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    /// <summary>The checkpoint after the transition.</summary>
    public int Checkpoint { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
