namespace Tevrin.Engine.Domain.Gates;

/// <summary>What the caller does after a gate decision.</summary>
public enum GateFollowUp
{
    /// <summary>Nothing beyond recording the decision (e.g. a rejected gate with no agent to re-run).</summary>
    None = 0,

    /// <summary>Apply the gated forward transition (an approval gate approved).</summary>
    ApplyTransition = 1,

    /// <summary>Re-run <see cref="GateResolution.RerunAgent"/> with the comment as feedback.</summary>
    RerunAgent = 2,

    /// <summary>Escalation <c>continue</c>: allow one more cycle and re-enter execution.</summary>
    StartReviewCycle = 3,

    /// <summary>Escalation <c>rollback</c>: roll back to <see cref="GateResolution.TargetVersion"/>.</summary>
    Rollback = 4,

    /// <summary>Escalation <c>accept</c>: mark release-eligible (the pack creates its release gate).</summary>
    MarkReleaseEligible = 5,
}

/// <summary>A pending gate as the artifact-approval rules see it.</summary>
public sealed record PendingGateRef(Guid Id, string Type, Guid? ArtifactId);

/// <summary>The outcome of deciding a gate.</summary>
public sealed record GateResolution(
    GateStatus NewStatus,
    GateFollowUp FollowUp,
    string? RerunAgent = null,
    EscalationDecision? Decision = null,
    int? TargetVersion = null);
