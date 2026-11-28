using System.Globalization;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;

namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>FR-343, §4.64: where one branch of a parallel group stands in the group's current entry.</summary>
public enum BranchState
{
    /// <summary>A stage of the branch is open (running, waiting or failed awaiting a retry), or none has opened yet.</summary>
    Running = 0,

    /// <summary>The branch's last stage completed.</summary>
    Completed = 1,

    /// <summary>A stage of the branch failed and the group continues without it (<c>on_branch_failure: continue</c>).</summary>
    Failed = 2,
}

/// <summary>FR-343: what follows a completed branch stage.</summary>
public abstract record BranchNext
{
    private BranchNext()
    {
    }

    /// <summary>Start the branch's next stage.</summary>
    public sealed record Start(string Stage) : BranchNext;

    /// <summary>§4.64: the group has failed (<c>on_branch_failure: fail</c>): the next stage opens but doesn't start.</summary>
    public sealed record Halt(string Stage) : BranchNext;

    /// <summary>The branch's last stage completed: check the join.</summary>
    public sealed record Ended : BranchNext;
}

/// <summary>FR-343, §4.64: the join of a parallel group.</summary>
public abstract record JoinState
{
    private JoinState()
    {
    }

    /// <summary>A branch is still running.</summary>
    public sealed record Pending : JoinState;

    /// <summary>Every branch ended (<c>join: all</c>); <paramref name="FailedBranches"/> are marked (<c>continue</c>).</summary>
    public sealed record Complete(IReadOnlyList<string> FailedBranches) : JoinState;
}

/// <summary>FR-343: the outcomes a branch stage execution closes with (besides the transition kind <c>advance</c>).</summary>
public static class BranchOutcomes
{
    /// <summary>The branch's last stage completed.</summary>
    public const string Completed = "completed";

    /// <summary>The stage failed and the group continues without the branch.</summary>
    public const string Failed = "failed";
}

/// <summary>FR-343, §4.64: pure rules for parallel groups in the run driver.</summary>
public static partial class RunDriverRules
{
    /// <summary>FR-343: the id stored in <see cref="StageExecution.Branch"/> for branch <paramref name="index"/> (0-based).</summary>
    public static string BranchId(int index) => index.ToString(CultureInfo.InvariantCulture);

    /// <summary>FR-343: entering a group starts every branch at once: each branch's id and first stage, in declared order.</summary>
    public static IReadOnlyList<(string Branch, string Stage)> EnterGroup(ParallelDefinition group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return [.. group.Branches.Select((b, i) => (BranchId(i), b[0].Id))];
    }

    /// <summary>The branch index and position of <paramref name="stageId"/> in <paramref name="group"/>, or <c>null</c>.</summary>
    public static (int Branch, int Position)? LocateInGroup(ParallelDefinition group, string? stageId)
    {
        ArgumentNullException.ThrowIfNull(group);
        for (var b = 0; b < group.Branches.Count; b++)
        {
            for (var p = 0; p < group.Branches[b].Count; p++)
            {
                if (string.Equals(group.Branches[b][p].Id, stageId, StringComparison.Ordinal))
                {
                    return (b, p);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// FR-343: whether <paramref name="stageExecution"/> is a live branch execution of the group <paramref name="group"/> the
    /// run is in: its branch id matches the branch its stage belongs to.
    /// </summary>
    public static bool InGroup(ParallelDefinition group, StageExecution stageExecution)
    {
        ArgumentNullException.ThrowIfNull(stageExecution);
        return LocateInGroup(group, stageExecution.StageId) is { } at
            && string.Equals(BranchId(at.Branch), stageExecution.Branch, StringComparison.Ordinal);
    }

    /// <summary>
    /// §4.64: after a branch stage completes: the next stage of the branch, held back when the group has failed
    /// (<paramref name="groupFailed"/> under <c>on_branch_failure: fail</c>), or the end of the branch.
    /// </summary>
    public static BranchNext AfterBranchStage(ParallelDefinition group, string stageId, bool groupFailed)
    {
        ArgumentNullException.ThrowIfNull(group);
        var at = LocateInGroup(group, stageId)
            ?? throw new ArgumentOutOfRangeException(nameof(stageId), stageId, "The stage is not in the group.");
        var branch = group.Branches[at.Branch];
        if (at.Position == branch.Count - 1)
        {
            return new BranchNext.Ended();
        }

        var next = branch[at.Position + 1].Id;
        return groupFailed && group.OnBranchFailure == BranchFailurePolicy.Fail ? new BranchNext.Halt(next) : new BranchNext.Start(next);
    }

    /// <summary>
    /// FR-343: the branch executions of the group's current entry (entered at or after <paramref name="groupEntry"/>),
    /// grouped by branch, and each branch's state: its furthest execution open → running; closed <c>completed</c> on the
    /// branch's last stage → completed; closed <c>failed</c> → failed; no execution → running.
    /// </summary>
    public static IReadOnlyList<BranchState> BranchStates(
        ParallelDefinition group, StageExecution groupEntry, IEnumerable<StageExecution> executions)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(groupEntry);
        ArgumentNullException.ThrowIfNull(executions);
        var entry = executions
            .Where(e => e.PipelineRunId == groupEntry.PipelineRunId && e.Branch is not null && e.EnteredAt >= groupEntry.EnteredAt && InGroup(group, e))
            .ToList();
        var states = new List<BranchState>();
        for (var b = 0; b < group.Branches.Count; b++)
        {
            var id = BranchId(b);
            var furthest = entry.Where(e => e.Branch == id)
                .OrderByDescending(e => LocateInGroup(group, e.StageId)!.Value.Position)
                .ThenByDescending(e => e.EnteredAt)
                .FirstOrDefault();
            var last = group.Branches[b][^1].Id;
            states.Add(furthest switch
            {
                { ExitedAt: not null, Outcome: BranchOutcomes.Failed } => BranchState.Failed,
                { ExitedAt: not null, Outcome: BranchOutcomes.Completed } when furthest.StageId == last => BranchState.Completed,
                _ => BranchState.Running,
            });
        }

        return states;
    }

    /// <summary>
    /// §4.64 (<c>join: all</c>): the group is complete when every branch has ended: completed, or failed under
    /// <c>on_branch_failure: continue</c> (marked). Under <c>fail</c> a failed branch never ends the group: it waits on a
    /// retry (FR-333) and no further branch stages start.
    /// </summary>
    public static JoinState Join(ParallelDefinition group, IReadOnlyList<BranchState> branches)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(branches);
        if (branches.Count != group.Branches.Count)
        {
            throw new ArgumentException("One state per branch is required.", nameof(branches));
        }

        var ended = branches.All(s => s == BranchState.Completed
            || (s == BranchState.Failed && group.OnBranchFailure == BranchFailurePolicy.Continue));
        return ended
            ? new JoinState.Complete([.. branches.Select((s, i) => (s, i)).Where(x => x.s == BranchState.Failed).Select(x => BranchId(x.i))])
            : new JoinState.Pending();
    }

    /// <summary>
    /// FR-335, FR-343: whether a submitted input queues the driver for the run's open execution. Outside a group, only when
    /// no <c>stage.run</c> job of the run is queued or running. In a group a sibling branch is usually running, so the
    /// group's own job is queued unless one already is (it resumes the waiting branch and skips busy ones).
    /// </summary>
    public static bool ResumeOnInput(bool inGroup, bool anyActive, bool groupJobQueued) => inGroup ? !groupJobQueued : !anyActive;

    /// <summary>
    /// FR-333, FR-343: whether a group's job resumes an open branch execution that has no queued or running job: always on
    /// a retry; otherwise (an input, gate or escalation decision) only when its last job neither failed nor died.
    /// </summary>
    public static bool ResumeBranch(bool retry, JobStatus? lastJob) =>
        retry || lastJob is not (JobStatus.Failed or JobStatus.Dead);

    /// <summary>FR-343: whether a permanent failure of a branch stage ends the branch (<c>continue</c>) rather than holding
    /// the group for a retry (<c>fail</c>).</summary>
    public static bool BranchFailureEndsBranch(ParallelDefinition group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.OnBranchFailure == BranchFailurePolicy.Continue;
    }
}
