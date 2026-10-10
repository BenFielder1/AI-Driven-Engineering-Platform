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

/// <summary>
/// The execution runner's overall state for a project (FR-31, FR-33, FR-37): <c>idle</c> (not started, or nothing to run),
/// <c>running</c>, <c>paused</c> (no new step starts; a running step may still finish), <c>completed</c> (every step of the
/// current plan completed) or <c>blocked</c> (a step is blocked awaiting an escalation decision).
/// </summary>
public enum ExecutionRunStatus
{
    Idle = 0,
    Running = 1,
    Paused = 2,
    Completed = 3,
    Blocked = 4,
}
