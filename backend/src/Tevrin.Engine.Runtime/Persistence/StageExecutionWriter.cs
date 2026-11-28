using Microsoft.EntityFrameworkCore;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Runtime.Persistence;

/// <summary>
/// FR-297, D-0.7.0-32: writes <c>stage_executions</c> in the unit of work of the transition that causes them (the caller
/// saves). Entering a stage closes the run's open execution with the transition kind and opens a new one; a run that ends
/// closes its open execution with its final status. Two saves can't both move a run: each changes the run row, whose
/// <c>xmin</c> token fails the second. A run with no open execution (it started before 0.7.0) simply gets its next entry.
/// </summary>
public static class StageExecutionWriter
{
    /// <summary>Records that run <paramref name="pipelineRunId"/> entered <paramref name="stageId"/> at checkpoint
    /// <paramref name="checkpoint"/>: the open execution (if any) is closed with <paramref name="leavingOutcome"/>, and the
    /// new one's iteration is the run's count of entries into the stage plus one.</summary>
    public static async Task<StageExecution> EnterAsync(
        IEngineDbContext db,
        Guid pipelineRunId,
        string stageId,
        int checkpoint,
        string leavingOutcome,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(stageId);
        ArgumentException.ThrowIfNullOrEmpty(leavingOutcome);
        var rows = await RowsAsync(db, pipelineRunId, cancellationToken);
        Close(rows, leavingOutcome, at);
        var execution = new StageExecution
        {
            PipelineRunId = pipelineRunId,
            StageId = stageId,
            // FR-355: a map's item executions aren't entries into the stage.
            Iteration = rows.Count(r => r.ParentExecutionId is null && string.Equals(r.StageId, stageId, StringComparison.Ordinal)) + 1,
            Checkpoint = checkpoint,
            EnteredAt = at,
        };
        db.StageExecutions.Add(execution);
        return execution;
    }

    /// <summary>
    /// FR-343: opens an execution of branch stage <paramref name="stageId"/> in branch <paramref name="branch"/> of the
    /// run's current parallel group (or, FR-345, a fan-out branch of the current stage, with its <paramref name="variant"/>
    /// label). Other open executions (the group's and sibling branches') stay open.
    /// </summary>
    public static async Task<StageExecution> EnterBranchAsync(
        IEngineDbContext db,
        Guid pipelineRunId,
        string stageId,
        string branch,
        int checkpoint,
        DateTimeOffset at,
        CancellationToken cancellationToken,
        string? variant = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(stageId);
        ArgumentException.ThrowIfNullOrEmpty(branch);
        var rows = await RowsAsync(db, pipelineRunId, cancellationToken);
        var execution = new StageExecution
        {
            PipelineRunId = pipelineRunId,
            StageId = stageId,
            Branch = branch,
            Variant = variant,
            Iteration = rows.Count(r => string.Equals(r.StageId, stageId, StringComparison.Ordinal)) + 1,
            Checkpoint = checkpoint,
            EnteredAt = at,
        };
        db.StageExecutions.Add(execution);
        return execution;
    }

    /// <summary>
    /// FR-355, D-0.8.0-24: opens one item execution per element of map stage execution <paramref name="mapEntry"/> (item
    /// indexes 0…<paramref name="count"/>-1, each pointing at the map's execution, in the map's stage and iteration), in
    /// index order. The map's own execution stays open.
    /// </summary>
    public static IReadOnlyList<StageExecution> EnterItems(IEngineDbContext db, StageExecution mapEntry, int count, int checkpoint, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(mapEntry);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var items = new List<StageExecution>(count);
        for (var index = 0; index < count; index++)
        {
            var item = new StageExecution
            {
                PipelineRunId = mapEntry.PipelineRunId,
                StageId = mapEntry.StageId,
                Iteration = mapEntry.Iteration,
                Checkpoint = checkpoint,
                EnteredAt = at,
                ItemIndex = index,
                ParentExecutionId = mapEntry.Id,
            };
            db.StageExecutions.Add(item);
            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// FR-354: opens follow-up execution <paramref name="id"/> of stage <paramref name="stageId"/>, labelled
    /// <paramref name="followUp"/>, on a completed run. Other executions are untouched.
    /// </summary>
    public static async Task<StageExecution> EnterFollowUpAsync(
        IEngineDbContext db,
        Guid id,
        Guid pipelineRunId,
        string stageId,
        string followUp,
        int checkpoint,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(stageId);
        ArgumentException.ThrowIfNullOrEmpty(followUp);
        var rows = await RowsAsync(db, pipelineRunId, cancellationToken);
        var execution = new StageExecution
        {
            Id = id,
            PipelineRunId = pipelineRunId,
            StageId = stageId,
            FollowUp = followUp,
            Iteration = rows.Count(r => string.Equals(r.StageId, stageId, StringComparison.Ordinal)) + 1,
            Checkpoint = checkpoint,
            EnteredAt = at,
        };
        db.StageExecutions.Add(execution);
        return execution;
    }

    /// <summary>Closes run <paramref name="pipelineRunId"/>'s open execution, if any, with the run's final
    /// <paramref name="status"/> as its outcome.</summary>
    public static async Task EndAsync(
        IEngineDbContext db, Guid pipelineRunId, GenericRunStatus status, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        Close((await RowsAsync(db, pipelineRunId, cancellationToken)).Where(r => r.FollowUp is null), status.ToWire(), at);
    }

    /// <summary>
    /// Closes the open executions of runs that have ended (outside the unit of work, idempotent): a run closed by a set-based
    /// statement (a superseded version's run) ends its execution at its completion time with its final status.
    /// </summary>
    public static Task<int> CloseEndedAsync(IEngineDbContext db, Guid? projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var all = projectId is null;
        var only = projectId ?? Guid.Empty;
        return db.Database.ExecuteSqlAsync(
            $"""
            UPDATE stage_executions s
            SET exited_at = GREATEST(s.entered_at, COALESCE(r.completed_at, {now})), outcome = r.status
            FROM pipeline_runs r
            WHERE s.pipeline_run_id = r.id AND s.exited_at IS NULL AND s.followup IS NULL
              AND r.status IN ('completed', 'failed', 'cancelled')
              AND ({all} OR r.project_id = {only})
            """,
            cancellationToken);
    }

    /// <summary>The run's executions: stored ones (tracked) and those added in this unit of work.</summary>
    private static async Task<List<StageExecution>> RowsAsync(IEngineDbContext db, Guid pipelineRunId, CancellationToken cancellationToken)
    {
        var stored = await db.StageExecutions.Where(e => e.PipelineRunId == pipelineRunId).ToListAsync(cancellationToken);
        var added = db.ChangeTracker.Entries<StageExecution>()
            .Where(e => e.State == EntityState.Added && e.Entity.PipelineRunId == pipelineRunId)
            .Select(e => e.Entity);
        return [.. stored.Union(added)];
    }

    private static void Close(IEnumerable<StageExecution> rows, string outcome, DateTimeOffset at)
    {
        foreach (var open in rows.Where(r => r.ExitedAt is null))
        {
            open.ExitedAt = at < open.EnteredAt ? open.EnteredAt : at;
            open.Outcome = outcome;
        }
    }
}
