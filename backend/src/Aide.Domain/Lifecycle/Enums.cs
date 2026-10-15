namespace Aide.Domain.Lifecycle;

/// <summary>The six lifecycle phases, in order (§4.1). Stored as snake_case strings.</summary>
public enum Phase
{
    Refinement = 0,
    Specification = 1,
    Planning = 2,
    Execution = 3,
    Review = 4,
    Release = 5,
}

/// <summary>Project status (§4.1: status follows phase; <c>released</c> and <c>rolled_back</c> are special).</summary>
public enum ProjectStatus
{
    Active = 0,
    Building = 1,
    Reviewing = 2,
    Releasing = 3,
    Released = 4,
    RolledBack = 5,
}

/// <summary>The kinds of lifecycle move the state machine accepts.</summary>
public enum TransitionKind
{
    /// <summary>Forward to the next phase.</summary>
    Advance = 0,

    /// <summary><c>review → execution</c> for another review cycle.</summary>
    ReviewCycle = 1,

    /// <summary>To an earlier (or the same) phase at an earlier checkpoint of the version in development (§4.8, §4.10).</summary>
    Rollback = 2,

    /// <summary>Back to the phase producing the first invalidated artifact after a cascade (§4.4).</summary>
    InvalidationFallback = 3,

    /// <summary><c>release → release</c>, no change.</summary>
    ReleaseNoop = 4,

    /// <summary>The release job finished: status <c>released</c>. Since v2 the checkpoint is not incremented (§4.10, D-V2-2).</summary>
    ReleaseCompleted = 5,

    /// <summary>
    /// §4.10, FR-60: <c>release → refinement</c> to start a new release version, at checkpoint 1. Only from status
    /// <c>released</c> with no version in development; the one exception to "forward goes to the next phase only".
    /// </summary>
    NewVersion = 6,
}
