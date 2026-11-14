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

/// <summary>
/// The state of one execution run (FR-57): the steps of one plan version. <c>pending</c> (no step started),
/// <c>running</c>, <c>paused</c>, <c>completed</c> (every step completed), <c>blocked</c> (a step awaits an escalation
/// decision) or <c>superseded</c> (the plan was invalidated; its run stays listed under "Superseded").
/// </summary>
public enum PlanRunStatus
{
    Pending = 0,
    Running = 1,
    Paused = 2,
    Completed = 3,
    Blocked = 4,
    Superseded = 5,
}

/// <summary>Where an execution step came from (FR-256): the approved plan (<c>plan</c>) or the build-fix agent after a
/// failed build check (<c>build_fix</c>). Steps stored before 0.6.1 part 2 read as <c>plan</c>.</summary>
public enum StepOrigin
{
    Plan = 0,
    BuildFix = 1,
}

/// <summary>The state of one build check attempt (§4.47, FR-254–FR-258). Wire values: <c>waiting</c>, <c>passed</c>,
/// <c>failed</c>, <c>skipped</c>, <c>exhausted</c>, <c>cancelled</c>.</summary>
public enum BuildCheckStatus
{
    Waiting = 0,
    Passed = 1,
    Failed = 2,
    Skipped = 3,
    Exhausted = 4,
    Cancelled = 5,
}

/// <summary>The kind of a build check source (§5): the version's preview rollout (<c>preview</c>) or a required CI check
/// on the head commit (<c>check</c>).</summary>
public enum BuildCheckSourceKind
{
    Preview = 0,
    Check = 1,
}
