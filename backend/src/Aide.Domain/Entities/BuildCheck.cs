using Aide.Domain.Execution;

namespace Aide.Domain.Entities;

/// <summary>
/// One build check of a release version's head commit before Review (FR-254–FR-258, §4.47). Attempt 0 checks the commit
/// after the plan's own steps, attempt k the commit after the k-th build fix. Unique per (version, review cycle, attempt).
/// The fix steps planned from a failed check are the execution steps whose <see cref="ExecutionStep.BuildCheckId"/> is
/// this check's id.
/// </summary>
public sealed class BuildCheck
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public Guid ReleaseVersionId { get; set; }

    /// <summary>The project's review cycle the check belongs to; the attempt count resets each cycle.</summary>
    public int ReviewCycle { get; set; }

    /// <summary>0 for the plan's commit, k after the k-th fix.</summary>
    public int Attempt { get; set; }

    /// <summary>The commit the check waits on; results for other SHAs are ignored.</summary>
    public string HeadSha { get; set; } = string.Empty;

    public BuildCheckStatus Status { get; set; } = BuildCheckStatus.Waiting;

    /// <summary>The preview rollout and required CI checks read for the head SHA, with their results.</summary>
    public IReadOnlyList<BuildCheckSource> Sources { get; set; } = [];

    /// <summary>The failures found (empty unless the check failed or was exhausted).</summary>
    public IReadOnlyList<BuildCheckFailure> Failures { get; set; } = [];

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// When a rollback or an invalidation of the version's work superseded the check (§4.47): it stays as history but no
    /// longer counts for the gate, the attempt numbers or the fix budget, so re-execution starts again at attempt 0.
    /// <c>null</c> while it is in force.
    /// </summary>
    public DateTimeOffset? SupersededAt { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint RowVersion { get; set; }
}
