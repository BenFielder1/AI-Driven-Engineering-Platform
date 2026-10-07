namespace Aide.Domain.Execution;

public enum StepStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
    Rejected = 3,
    Blocked = 4,
    Invalidated = 5,
}

/// <summary>Declared action for a file in a plan step (FR-29).</summary>
public enum FileAction
{
    Create = 0,
    Modify = 1,
}

/// <summary>The six Execution Guardian checks (§4.6).</summary>
public enum GuardianCheck
{
    SingleStep = 0,
    FileLimit = 1,
    DependencyOrder = 2,
    PlanAlignment = 3,
    ScopeCreep = 4,
    SecretScan = 5,
}

public enum ViolationSeverity
{
    Error = 0,
    Warning = 1,
}
