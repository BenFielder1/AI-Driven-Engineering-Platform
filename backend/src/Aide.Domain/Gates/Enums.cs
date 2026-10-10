namespace Aide.Domain.Gates;

/// <summary>Project approval mode (§4.2).</summary>
public enum ApprovalMode
{
    Strict = 0,
    Advisory = 1,
    Autonomous = 2,
}

public enum GateType
{
    SpecApproval = 0,
    PlanApproval = 1,
    PreRelease = 2,
    Escalation = 3,
}

public enum GateStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3,
}

/// <summary>The decision recorded on an escalation gate (FR-14, D3).</summary>
public enum EscalationDecision
{
    Continue = 0,
    Rollback = 1,
    Accept = 2,
}
