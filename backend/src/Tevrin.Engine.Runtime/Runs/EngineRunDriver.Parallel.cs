using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Persistence;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>
/// FR-343, §4.64: parallel groups. The run's current stage is the group; its own execution (no branch) enters every branch
/// at once (one execution per branch, <see cref="StageExecution.Branch"/> = the branch index, and one <c>stage.run</c> job
/// each). Each branch job runs its stage and queues the branch's next stage; the job that ends the last running branch
/// moves the run past the group. Every branch move touches the run, so its <c>xmin</c> makes concurrent moves (two branches
/// ending at once, a cancel, a rollback) fail all but one save; the loser retries and finds the join already done.
/// </summary>
public sealed partial class EngineRunDriver
{
    private static readonly JobStatus[] Active = [JobStatus.Queued, JobStatus.Running];

    /// <summary>
    /// The group's own execution: on first delivery, the group's entry (requirements and gate, §4.62 steps 2–3) and then
    /// every branch's first stage; afterwards (a retry, a gate or escalation decision re-queues it) every open branch
    /// execution without a queued or running job is queued again, and the join is checked.
    /// </summary>
    private async Task DriveGroupAsync(
        JobExecution job,
        PipelineRun run,
        StageExecution groupEntry,
        ParallelDefinition group,
        PipelineDefinition definition,
        bool retry,
        CancellationToken cancellationToken)
    {
        var groupStage = definition.FindStage(run.CurrentStage)!;
        var entry = await EntryExecutionsAsync(run, group, groupEntry, cancellationToken);
        var now = time.GetUtcNow();
        if (entry.Count == 0)
        {
            var facts = await EntryFactsAsync(run, groupStage, groupEntry, definition, cancellationToken);
            if (RunDriverRules.Plan(groupStage, facts) is not (DriverStep.StopManual or DriverStep.PassApproval))
            {
                // Waiting on input or a gate, or a failed requirement: handled as for any stage.
                await RunStageAsync(job, run, groupStage, groupEntry, definition, cancellationToken);
                return;
            }

            EngineRunMoves.Touch(db, run);
            foreach (var (branch, first) in RunDriverRules.EnterGroup(group))
            {
                var opened = await StageExecutionWriter.EnterBranchAsync(db, run.Id, first, branch, run.Checkpoint, now, cancellationToken);
                EngineRunMoves.QueueDriver(jobs, run, opened.Id, now);
            }

            LogGroupEntered(logger, run.Id, groupStage.Id, group.Branches.Count);
            await SaveBranchMoveAsync(cancellationToken);
            return;
        }

        EngineRunMoves.Touch(db, run);
        var busy = await BusyExecutionsAsync(run, job.Id, cancellationToken);
        var last = (await EngineRunMoves.DriverJobsAsync(
                db, run, [JobStatus.Succeeded, JobStatus.Failed, JobStatus.Dead, JobStatus.Cancelled, JobStatus.Retried], cancellationToken))
            .Select(j => (Execution: StageRunPayload.Parse(j.Payload)!.StageExecutionId, j.CreatedAt, j.Status))
            .GroupBy(j => j.Execution)
            .ToDictionary(g => g.Key, g => g.MaxBy(j => j.CreatedAt).Status);
        foreach (var open in entry.Where(e => e.ExitedAt is null && !busy.Contains(e.Id)
            && RunDriverRules.ResumeBranch(retry, last.TryGetValue(e.Id, out var status) ? status : null)))
        {
            EngineRunMoves.QueueDriver(jobs, run, open.Id, now);
        }

        if (!await TryJoinAsync(run, group, groupEntry, definition, entry, cancellationToken))
        {
            await SaveBranchMoveAsync(cancellationToken);
        }
    }

    /// <summary>§4.64: a branch stage's work is done: start the branch's next stage (held back when the group has failed),
    /// or end the branch and check the join.</summary>
    private async Task BranchStageDoneAsync(
        JobExecution job, PipelineRun run, StageExecution execution, ParallelDefinition group, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        if (!await StillInGroupAsync(run, execution, cancellationToken))
        {
            return;
        }

        var now = time.GetUtcNow();
        var groupEntry = await GroupEntryAsync(run, cancellationToken);
        var entry = groupEntry is null ? [] : await EntryExecutionsAsync(run, group, groupEntry, cancellationToken);
        // Known window (accepted): a sibling's failure is read from its job row, which the worker writes after that job's
        // handler returns. A failure recorded in the same instant as this check isn't seen, so this branch's next stage
        // may still start; it then finishes, and the group still waits on the failed branch.
        var groupFailed = group.OnBranchFailure == BranchFailurePolicy.Fail
            && await AnySiblingFailedAsync(run, execution, entry, job.Id, cancellationToken);
        EngineRunMoves.Touch(db, run);
        execution.ExitedAt = now;
        switch (RunDriverRules.AfterBranchStage(group, execution.StageId, groupFailed))
        {
            case BranchNext.Start start:
                execution.Outcome = PipelineTransitionKinds.Advance;
                var next = await StageExecutionWriter.EnterBranchAsync(db, run.Id, start.Stage, execution.Branch!, run.Checkpoint, now, cancellationToken);
                EngineRunMoves.QueueDriver(jobs, run, next.Id, now);
                break;

            case BranchNext.Halt halt:
                // §4.64: the group failed; the next stage opens without a job, so a retry (FR-333) can resume it.
                execution.Outcome = PipelineTransitionKinds.Advance;
                await StageExecutionWriter.EnterBranchAsync(db, run.Id, halt.Stage, execution.Branch!, run.Checkpoint, now, cancellationToken);
                LogBranchHeld(logger, run.Id, execution.Branch!, halt.Stage);
                break;

            case BranchNext.Ended:
                execution.Outcome = BranchOutcomes.Completed;
                if (groupEntry is not null && await TryJoinAsync(run, group, groupEntry, definition, entry, cancellationToken))
                {
                    return;
                }

                break;
        }

        await SaveBranchMoveAsync(cancellationToken);
    }

    /// <summary>FR-343 <c>on_branch_failure: continue</c>: the branch's stage failed permanently: the branch ends failed
    /// (its execution closes <c>failed</c>) and the join is checked.</summary>
    private async Task BranchFailedAsync(
        PipelineRun run, StageExecution execution, ParallelDefinition group, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        if (!await StillInGroupAsync(run, execution, cancellationToken))
        {
            return;
        }

        var groupEntry = await GroupEntryAsync(run, cancellationToken);
        var entry = groupEntry is null ? [] : await EntryExecutionsAsync(run, group, groupEntry, cancellationToken);
        EngineRunMoves.Touch(db, run);
        execution.ExitedAt = time.GetUtcNow();
        execution.Outcome = BranchOutcomes.Failed;
        LogBranchFailed(logger, run.Id, execution.Branch!, execution.StageId);
        if (groupEntry is not null && await TryJoinAsync(run, group, groupEntry, definition, entry, cancellationToken))
        {
            return;
        }

        await SaveBranchMoveAsync(cancellationToken);
    }

    /// <summary>§4.64: when every branch has ended, the run moves past the group (saved here; true). Else false.</summary>
    private async Task<bool> TryJoinAsync(
        PipelineRun run,
        ParallelDefinition group,
        StageExecution groupEntry,
        PipelineDefinition definition,
        IReadOnlyList<StageExecution> entry,
        CancellationToken cancellationToken)
    {
        if (RunDriverRules.Join(group, RunDriverRules.BranchStates(group, groupEntry, entry)) is not JoinState.Complete complete)
        {
            return false;
        }

        LogJoined(logger, run.Id, run.CurrentStage, complete.FailedBranches.Count);
        try
        {
            await MoveOnAsync(run, definition, cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw Moved(ex);
        }

        return true;
    }

    /// <summary>
    /// Reloads the run (and the branch's execution) before a branch move, so the move's save is guarded by the run's
    /// current <c>xmin</c> rather than the one read when the job started (a sibling branch has usually moved since), and
    /// checks the run is still in the group: a cancel, rollback or concurrent failure meanwhile makes the move stale.
    /// </summary>
    private async Task<bool> StillInGroupAsync(PipelineRun run, StageExecution execution, CancellationToken cancellationToken)
    {
        var groupId = run.CurrentStage;
        await db.ChangeTracker.Entries<PipelineRun>().Single(e => ReferenceEquals(e.Entity, run)).ReloadAsync(cancellationToken);
        await db.ChangeTracker.Entries<StageExecution>().Single(e => ReferenceEquals(e.Entity, execution)).ReloadAsync(cancellationToken);
        if (RunDriverRules.IsStale(run, execution) || !string.Equals(run.CurrentStage, groupId, StringComparison.Ordinal))
        {
            LogStale(logger, run.Id, execution.Id, "the run left the group while the branch stage ran");
            return false;
        }

        return true;
    }

    /// <summary>The run's open execution of its current group (no branch), or <c>null</c>.</summary>
    private Task<StageExecution?> GroupEntryAsync(PipelineRun run, CancellationToken cancellationToken) =>
        db.StageExecutions
            .Where(e => e.PipelineRunId == run.Id && e.Branch == null && e.ExitedAt == null && e.StageId == run.CurrentStage)
            .OrderByDescending(e => e.EnteredAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The branch executions of the group's current entry (stored, tracked, and added in this unit of work).</summary>
    private async Task<List<StageExecution>> EntryExecutionsAsync(
        PipelineRun run, ParallelDefinition group, StageExecution groupEntry, CancellationToken cancellationToken)
    {
        var enteredAt = groupEntry.EnteredAt;
        var stored = await db.StageExecutions
            .Where(e => e.PipelineRunId == run.Id && e.Branch != null && e.EnteredAt >= enteredAt)
            .ToListAsync(cancellationToken);
        var added = db.ChangeTracker.Entries<StageExecution>()
            .Where(e => e.State == EntityState.Added && e.Entity.PipelineRunId == run.Id && e.Entity.Branch != null)
            .Select(e => e.Entity);
        return [.. stored.Union(added).Where(e => RunDriverRules.InGroup(group, e))];
    }

    /// <summary>The executions with a queued or running <c>stage.run</c> job other than <paramref name="jobId"/>.</summary>
    private async Task<HashSet<Guid>> BusyExecutionsAsync(PipelineRun run, Guid jobId, CancellationToken cancellationToken) =>
        [.. (await EngineRunMoves.DriverJobsAsync(db, run, Active, cancellationToken))
            .Where(j => j.Id != jobId)
            .Select(j => StageRunPayload.Parse(j.Payload)!.StageExecutionId)];

    /// <summary>§4.64 <c>fail</c>: whether a sibling branch's open stage has a failed (or dead) job and none queued or running.</summary>
    private async Task<bool> AnySiblingFailedAsync(
        PipelineRun run, StageExecution execution, IReadOnlyList<StageExecution> entry, Guid jobId, CancellationToken cancellationToken)
    {
        var open = entry.Where(e => e.ExitedAt is null && e.Branch != execution.Branch).Select(e => e.Id).ToHashSet();
        if (open.Count == 0)
        {
            return false;
        }

        var failed = (await EngineRunMoves.DriverJobsAsync(db, run, [JobStatus.Failed, JobStatus.Dead], cancellationToken))
            .Select(j => StageRunPayload.Parse(j.Payload)!.StageExecutionId)
            .Where(open.Contains)
            .ToHashSet();
        if (failed.Count == 0)
        {
            return false;
        }

        var busy = await BusyExecutionsAsync(run, jobId, cancellationToken);
        return failed.Any(id => !busy.Contains(id));
    }

    /// <summary>Saves a branch move; a concurrent move of the run (another branch, a cancel, a rollback) retries the job.</summary>
    private async Task SaveBranchMoveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw Moved(ex);
        }
    }

    private static JobFailedException Moved(DbUpdateException ex) =>
        new("The run moved while a parallel branch was saving; the job retries.", ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} entered parallel group {StageId} ({BranchCount} branches)")]
    private static partial void LogGroupEntered(ILogger logger, Guid pipelineRunId, string stageId, int branchCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} branch {Branch}: {StageId} held back, the group has failed")]
    private static partial void LogBranchHeld(ILogger logger, Guid pipelineRunId, string branch, string stageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline run {PipelineRunId} branch {Branch} failed at {StageId}; the group continues")]
    private static partial void LogBranchFailed(ILogger logger, Guid pipelineRunId, string branch, string stageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} joined parallel group {StageId} ({FailedCount} failed branches)")]
    private static partial void LogJoined(ILogger logger, Guid pipelineRunId, string stageId, int failedCount);
}
