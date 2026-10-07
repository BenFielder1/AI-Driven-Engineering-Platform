using Aide.Domain.Gates;

namespace Aide.Domain.Entities;

/// <summary>An approval gate (§4.2, FR-12–FR-15). At most one pending gate per (project, type).</summary>
public sealed class ApprovalGate
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public GateType Type { get; set; }

    public Guid? ArtifactId { get; set; }

    public GateStatus Status { get; set; } = GateStatus.Pending;

    /// <summary>Escalation gates only (FR-14, D3).</summary>
    public EscalationDecision? Decision { get; set; }

    /// <summary>Escalation <c>rollback</c> decision: the project version to roll back to.</summary>
    public int? TargetVersion { get; set; }

    public Guid? ReviewerId { get; set; }

    public string? Comment { get; set; }

    public bool Overdue { get; set; }

    public DateTimeOffset? RemindedAt { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public uint RowVersion { get; set; }
}
