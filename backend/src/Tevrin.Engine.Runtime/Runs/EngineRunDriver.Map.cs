using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Artifacts;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Gates;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Stages;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>
/// FR-355, §4.68, D-0.8.0-24: map-reduce. A map stage's own execution runs the stage's entry (requirements and gate), reads
/// the source array (more than <c>max_items</c> elements fail the stage with <c>MAP_TOO_MANY_ITEMS</c> before any item
/// runs), opens every item execution up front and queues the first <c>max_parallel</c> of them, each its own
/// <c>stage.run</c> job and agent run (or handler call) given only its element and the stage's inputs. Each item that ends
/// (completed or permanently failed) queues the next idle items in index order, so at most <c>max_parallel</c> run at once.
/// Failures beyond the limit fail the stage: nothing more is queued and the run waits for a retry, which reopens the
/// failed items. The item that ends the last one queues the map's own execution again, which runs <c>reduce</c> (if any)
/// over every item output and then the stage's transition. Every item move touches the run, so its <c>xmin</c> lets one
/// of two concurrent moves win and the other retry (as parallel branches do).
/// </summary>
public sealed partial class EngineRunDriver
{
    /// <summary>The run key of a map stage's reduce agent run (its id is derived from the map's job, FR-310).</summary>
    public const string ReduceRunKey = "reduce";

    /// <summary>One <c>stage.run</c> job of a map execution: the execution it drives, its id, status and creation time.</summary>
    private sealed record MapJob(Guid Execution, Guid JobId, JobStatus Status, DateTimeOffset CreatedAt)
    {
        public bool Active => Status is JobStatus.Queued or JobStatus.Running;
    }

    private async Task DriveMapAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, StageExecution execution, PipelineDefinition definition, bool retry, CancellationToken cancellationToken)
    {
        var map = stage.Map!;
        if (execution.ParentExecutionId is not null)
        {
            await RunMapItemAsync(job, run, stage, map, execution, definition, cancellationToken);
            return;
        }

        var items = await MapItemRowsAsync(execution.Id, cancellationToken);
        if (items.Count == 0)
        {
            await EnterMapAsync(job, run, stage, map, execution, definition, cancellationToken);
            return;
        }

        if (retry)
        {
            // FR-333: a retry runs the failed items again (their failed agent runs stay in the history).
            foreach (var failed in items.Where(i => MapRules.StatusOf(i) == MapItemStatus.Failed))
            {
                failed.ExitedAt = null;
                failed.Outcome = null;
            }
        }

        // A retry, a redelivery, or the last item's hand-over: queue what fits, or reduce and move on.
        var next = await MapNextAsync(run, map, items, job.Id, retry, fromItemEnd: false, cancellationToken);
        if (next is MapNext.Finish)
        {
            await FinishMapAsync(job, run, stage, execution, definition, cancellationToken);
            return;
        }

        EngineRunMoves.Touch(db, run);
        switch (next)
        {
            case MapNext.Start queue:
                QueueMapItems(run, items, queue.Items);
                break;
            case MapNext.Failed failed:
                LogMapFailed(logger, run.Id, stage.Id, failed.Reason);
                break;
        }

        await SaveBranchMoveAsync(cancellationToken);
    }

    /// <summary>
    /// One item: its worker on its element (an agent run, or the handler), then its end. A permanent failure ends the item
    /// failed (counted against the limit) and still fails its job; a transient one leaves it open for the queue's retry.
    /// </summary>
    private async Task RunMapItemAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, MapDefinition map, StageExecution item, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        if (!await MapEntryOpenAsync(item, cancellationToken))
        {
            LogStale(logger, run.Id, item.Id, "the item's map stage execution is closed");
            return;
        }

        try
        {
            if (!await RunMapWorkerAsync(job, run, stage, map, item, definition, cancellationToken))
            {
                return;
            }
        }
        catch (JobFailedException ex) when (!ex.Transient)
        {
            await MapItemEndedAsync(job, run, stage, map, item, failed: true, cancellationToken);
            throw;
        }

        await MapItemEndedAsync(job, run, stage, map, item, failed: false, cancellationToken);
    }

    /// <summary>Whether the item's map execution is still open (a cancel, rollback or move closes it with its items).</summary>
    private async Task<bool> MapEntryOpenAsync(StageExecution item, CancellationToken cancellationToken)
    {
        var parentId = item.ParentExecutionId;
        return await db.StageExecutions.AnyAsync(e => e.Id == parentId && e.ExitedAt == null, cancellationToken);
    }

    /// <summary>
    /// FR-355: the item's worker, given only its element and the stage's inputs: the map's agent (its own agent run, id
    /// derived from the job, so a redelivery reuses it; the element as <c>{{MAP_ITEM}}</c>, the index as
    /// <c>{{MAP_INDEX}}</c>), or the map's handler (with <see cref="MapItemFact"/>; its output stored as the item's). False
    /// when an agent escalated (the item stays open; the gate's decision resumes it).
    /// </summary>
    private async Task<bool> RunMapWorkerAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, MapDefinition map, StageExecution item, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        var index = item.ItemIndex!.Value;
        var elements = await MapSourceAsync(run, stage, map, cancellationToken);
        if (index >= elements.Count)
        {
            throw new JobFailedException(
                $"Map item {index} of {stage.Id} is no longer in its source ({elements.Count} items).", transient: false, EngineErrorCodes.Validation);
        }

        var element = elements[index];
        if (map.Agent is { } agentKey)
        {
            var result = await agents.RunAsync(
                new AgentStageRequest(run.Id, stage.Id, JobTriggers.StageEntered)
                {
                    AgentKey = agentKey,
                    AgentRunId = AgentRunRules.RunId(job.Id, AgentRunKey),
                    JobAttempt = job.Attempt,
                    ItemIndex = index,
                    ItemsSince = item.EnteredAt,
                    PromptValues = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        [MapRules.ItemPlaceholder] = element,
                        [MapRules.IndexPlaceholder] = index.ToString(CultureInfo.InvariantCulture),
                    },
                },
                cancellationToken);
            result.ThrowIfFailed();
            return result.End == AgentStageEnd.Stored;
        }

        var handler = map.Handler!;
        var dispatch = await handlers.RunAsync(
            new StageHandlerRequest(run.Id, run.ProjectId, run.DefinitionHash, handler)
            {
                Facts = FactBag.Empty.With(new StageJobFact(job)).With(new MapItemFact(index, element)),
            },
            cancellationToken);
        var handled = dispatch.RequireRan(handler);
        handled.ThrowIfFailed(handler);
        if (handled.Outcome == StageHandlerOutcome.Waiting)
        {
            throw new JobFailedException($"Map item handler '{handler}' can't wait; an item's work finishes in its job.", transient: false, EngineErrorCodes.Validation);
        }

        if (handled.Output is { } output)
        {
            await StoreMapOutputAsync(run, stage, definition, output, index, item.EnteredAt, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// The item ended: it closes (completed or failed), then the map's next step: queue the next idle items, hand the
    /// finished map to its own execution's job (once), or, when the failures fail the stage, nothing more.
    /// </summary>
    private async Task MapItemEndedAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, MapDefinition map, StageExecution item, bool failed, CancellationToken cancellationToken)
    {
        // The run and the item are reloaded, so the save is guarded by the run's current xmin and a cancel, rollback or
        // move meanwhile makes this end stale.
        if (!await StillInGroupAsync(run, item, cancellationToken) || !await MapEntryOpenAsync(item, cancellationToken))
        {
            return;
        }

        var parentId = item.ParentExecutionId!.Value;
        EngineRunMoves.Touch(db, run);
        item.ExitedAt = time.GetUtcNow();
        item.Outcome = failed ? BranchOutcomes.Failed : BranchOutcomes.Completed;
        var items = await MapItemRowsAsync(parentId, cancellationToken);
        switch (await MapNextAsync(run, map, items, job.Id, retry: false, fromItemEnd: true, cancellationToken))
        {
            case MapNext.Start queue:
                QueueMapItems(run, items, queue.Items);
                break;

            case MapNext.Finish:
                if (!(await MapJobsAsync(run, [parentId], cancellationToken)).Any(j => j.Active && j.JobId != job.Id))
                {
                    EngineRunMoves.QueueDriver(jobs, run, parentId, time.GetUtcNow());
                }

                break;

            case MapNext.Failed stageFailed:
                LogMapFailed(logger, run.Id, stage.Id, stageFailed.Reason);
                break;
        }

        await SaveBranchMoveAsync(cancellationToken);
    }

    /// <summary>
    /// The map's first delivery: the entry (waiting on input or a gate, or a failed requirement, as for any stage), then
    /// the source array; every item opens and the first <c>max_parallel</c> are queued. No elements: the reduce gets an
    /// empty list and the stage moves on.
    /// </summary>
    private async Task EnterMapAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, MapDefinition map, StageExecution execution, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        var facts = await EntryFactsAsync(run, stage, execution, definition, cancellationToken);
        if (RunDriverRules.Plan(stage, facts) is not DriverStep.RunMap)
        {
            await RunStageAsync(job, run, stage, execution, definition, cancellationToken);
            return;
        }

        var elements = await MapSourceAsync(run, stage, map, cancellationToken);
        if (MapRules.TooMany(map, elements.Count))
        {
            throw new JobFailedException(
                $"Map stage {stage.Id} has {elements.Count} items; max_items is {map.MaxItems}.", transient: false, EngineErrorCodes.MapTooManyItems);
        }

        if (elements.Count == 0)
        {
            LogMapEntered(logger, run.Id, stage.Id, 0);
            await FinishMapAsync(job, run, stage, execution, definition, cancellationToken);
            return;
        }

        EngineRunMoves.Touch(db, run);
        var items = StageExecutionWriter.EnterItems(db, execution, elements.Count, run.Checkpoint, time.GetUtcNow());
        QueueMapItems(run, items, [.. items.Take(map.MaxParallel).Select(i => i.ItemIndex!.Value)]);
        LogMapEntered(logger, run.Id, stage.Id, items.Count);
        await SaveBranchMoveAsync(cancellationToken);
    }

    private void QueueMapItems(PipelineRun run, IReadOnlyList<StageExecution> items, IReadOnlyList<int> indexes)
    {
        var now = time.GetUtcNow();
        foreach (var index in indexes)
        {
            EngineRunMoves.QueueDriver(jobs, run, items.Single(i => i.ItemIndex == index).Id, now);
        }
    }

    /// <summary>
    /// Every item has finished within the limit: the <c>reduce</c> (if declared) gets every completed item's output in
    /// index order (FR-346 list: <c>{{&lt;OUTPUT&gt;_ALL}}</c> for an agent, <see cref="AggregateListFact"/> for a handler) and
    /// its output is the stage's plain output version; then the stage's transition (veto, route, next) on the map's
    /// execution. The reduce agent run's id is derived from this job, so a redelivery reuses it.
    /// </summary>
    private async Task FinishMapAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, StageExecution mapEntry, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        LogMapDone(logger, run.Id, stage.Id);
        string? output = null;
        if (stage.Reduce is { } reduce)
        {
            var list = await ReduceListAsync(run, stage, mapEntry, cancellationToken);
            if (reduce.Agent is { } agentKey)
            {
                var result = await agents.RunAsync(
                    new AgentStageRequest(run.Id, stage.Id, JobTriggers.StageEntered)
                    {
                        AgentKey = agentKey,
                        AgentRunId = AgentRunRules.RunId(job.Id, ReduceRunKey),
                        JobAttempt = job.Attempt,
                        Reduce = true,
                        PromptValues = new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            [FanOutRules.AllPlaceholder(stage.Output!)] = FanOutRules.RenderList(list),
                        },
                    },
                    cancellationToken);
                result.ThrowIfFailed();
                if (result.End != AgentStageEnd.Stored)
                {
                    // Escalated: the escalation gate's decision re-queues the map's execution, which reduces again.
                    return;
                }

                output = OutputText(result.Last);
            }
            else
            {
                var handler = reduce.Handler!;
                var dispatch = await handlers.RunAsync(
                    new StageHandlerRequest(run.Id, run.ProjectId, run.DefinitionHash, handler)
                    {
                        Facts = FactBag.Empty.With(new StageJobFact(job)).With(new AggregateListFact(list)),
                    },
                    cancellationToken);
                var handled = dispatch.RequireRan(handler);
                handled.ThrowIfFailed(handler);
                if (handled.Outcome == StageHandlerOutcome.Waiting)
                {
                    return;
                }

                output = handled.Output;
                if (output is not null)
                {
                    await StoreMapOutputAsync(run, stage, definition, output, itemIndex: null, mapEntry.EnteredAt, cancellationToken);
                }
            }
        }

        await WriteFeedbackAsync(run, stage, mapEntry, definition, output, cancellationToken);
        try
        {
            await TransitionAsync(job, run, stage, mapEntry, definition, output, cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw Moved(ex);
        }
    }

    /// <summary>FR-355, FR-346: the completed items' outputs (newest version per item, intact and not invalidated), in index
    /// order, labelled <c>item-&lt;index&gt;</c>.</summary>
    private async Task<IReadOnlyList<AggregateItem>> ReduceListAsync(PipelineRun run, StageDefinition stage, StageExecution mapEntry, CancellationToken cancellationToken)
    {
        if (stage.Output is not { } key)
        {
            return [];
        }

        var completed = (await MapItemRowsAsync(mapEntry.Id, cancellationToken))
            .Where(i => MapRules.StatusOf(i) == MapItemStatus.Completed)
            .Select(i => i.ItemIndex!.Value)
            .ToHashSet();
        return [.. (await new EngineAgentStageStore(db).RunItemsAsync(run, key, mapEntry.EnteredAt, cancellationToken))
            .Where(a => completed.Contains(a.ItemIndex!.Value) && a.Status is not (ArtifactStatus.Invalidated or ArtifactStatus.Rejected))
            .Select(a => new AggregateItem(MapRules.ItemLabel(a.ItemIndex!.Value), a.Content))];
    }

    /// <summary>
    /// FR-355: a map or reduce handler's output as a version of the stage's output: an item's numbered per item index in
    /// the run, the reduce's as the next plain version. A redelivery that already stored it (since the item, or the map,
    /// was entered) stores nothing more.
    /// </summary>
    private async Task StoreMapOutputAsync(
        PipelineRun run, StageDefinition stage, PipelineDefinition definition, string content, int? itemIndex, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (definition.FindArtifact(stage.Output) is not { } artifactDefinition)
        {
            return;
        }

        var key = artifactDefinition.Key;
        var runId = run.Id;
        if (await db.Artifacts.AnyAsync(
            a => a.PipelineRunId == runId && a.Type == key && a.Variant == null && a.ItemIndex == itemIndex && a.CreatedAt >= since, cancellationToken))
        {
            return;
        }

        var store = new EngineAgentStageStore(db);
        int version;
        int? parentVersion;
        if (itemIndex is { } index)
        {
            var latest = await store.MaxItemVersionAsync(run, key, index, cancellationToken);
            (version, parentVersion) = ((latest ?? 0) + 1, latest);
        }
        else
        {
            var plan = ArtifactRules.NewDraft(await store.VersionsAsync(run.ProjectId, key, cancellationToken));
            foreach (var draft in (await store.SubjectVersionsAsync(run, key, plan.SupersededVersions, cancellationToken))
                .Where(d => ArtifactRules.CanTransition(d.Status, ArtifactStatus.Superseded)))
            {
                draft.Status = ArtifactStatus.Superseded;
            }

            (version, parentVersion) = (plan.Version, plan.ParentVersion);
        }

        var now = time.GetUtcNow();
        var autoLock = artifactDefinition.Approval == ArtifactApproval.AutoLock;
        db.Artifacts.Add(new Artifact
        {
            ProjectId = run.ProjectId,
            Type = key,
            Version = version,
            ParentVersion = parentVersion,
            Content = content,
            ContentHash = ContentHash.Compute(content),
            Status = autoLock ? ArtifactStatus.Locked : ArtifactStatus.Draft,
            ApprovedAt = autoLock ? now : null,
            CheckpointAtCreation = run.Checkpoint,
            ReleaseVersionId = AgentStageScope.ReleaseVersionOf(run),
            PipelineRunId = run.Id,
            ItemIndex = itemIndex,
            CreatedAt = now,
        });
    }

    /// <summary>The item executions of map execution <paramref name="mapEntryId"/> (tracked), in index order.</summary>
    private async Task<List<StageExecution>> MapItemRowsAsync(Guid mapEntryId, CancellationToken cancellationToken) =>
        await db.StageExecutions
            .Where(e => e.ParentExecutionId == mapEntryId)
            .OrderBy(e => e.ItemIndex)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// §4.68: the map's next step from its item rows and the jobs of its open items: busy ones (a queued or running job
    /// other than <paramref name="jobId"/>) and each one's last job. <see cref="MapRules.Resumable"/>: an item that has run
    /// before (escalated, or failed and left open) is resumed only by the map's own job, never by a sibling's end, and not
    /// while the run has a pending gate.
    /// </summary>
    private async Task<MapNext> MapNextAsync(
        PipelineRun run, MapDefinition map, IReadOnlyList<StageExecution> items, Guid jobId, bool retry, bool fromItemEnd, CancellationToken cancellationToken)
    {
        var open = items.Where(i => MapRules.StatusOf(i) == MapItemStatus.Open).Select(i => i.Id).ToList();
        var itemJobs = await MapJobsAsync(run, open, cancellationToken);
        var busy = itemJobs.Where(j => j.Active && j.JobId != jobId).Select(j => j.Execution).ToHashSet();
        var last = itemJobs.Where(j => j.JobId != jobId)
            .GroupBy(j => j.Execution)
            .ToDictionary(g => g.Key, g => g.MaxBy(j => j.CreatedAt)!.Status);
        var runId = run.Id;
        var gatePending = !fromItemEnd && last.Count > 0
            && await db.ApprovalGates.AnyAsync(g => g.PipelineRunId == runId && g.Status == GateStatus.Pending, cancellationToken);
        var states = items
            .Select(i => new MapItemState(
                i.ItemIndex!.Value,
                MapRules.StatusOf(i),
                busy.Contains(i.Id),
                MapRules.Resumable(last.TryGetValue(i.Id, out var status) ? status : null, fromItemEnd, gatePending, retry)))
            .ToList();
        return MapRules.Next(map, states);
    }

    /// <summary>
    /// The <c>stage.run</c> jobs of <paramref name="executionIds"/> (this run's map executions), filtered in SQL on the
    /// payload's execution id, so the read grows with the map's open items, not with the project's job history.
    /// </summary>
    private async Task<List<MapJob>> MapJobsAsync(PipelineRun run, List<Guid> executionIds, CancellationToken cancellationToken)
    {
        if (executionIds.Count == 0)
        {
            return [];
        }

        var projectId = run.ProjectId;
        var type = EngineJobTypes.StageRun;
        var ids = executionIds.Select(id => id.ToString("D", CultureInfo.InvariantCulture)).ToArray();
        var rows = await db.Jobs
            .FromSql($"""
                SELECT * FROM jobs
                WHERE project_id = {projectId} AND type = {type} AND payload ->> 'stageExecutionId' = ANY({ids})
                """)
            .AsNoTracking()
            .Select(j => new { j.Id, j.Payload, j.Status, j.CreatedAt })
            .ToListAsync(cancellationToken);
        return [.. rows
            .Select(r => StageRunPayload.Parse(r.Payload) is { } payload && payload.RunId == run.Id
                ? new MapJob(payload.StageExecutionId, r.Id, r.Status, r.CreatedAt)
                : null)
            .OfType<MapJob>()];
    }

    /// <summary>FR-355: the source array's elements (JSON text each), from the run's current version of the source artifact.</summary>
    private async Task<IReadOnlyList<string>> MapSourceAsync(PipelineRun run, StageDefinition stage, MapDefinition map, CancellationToken cancellationToken)
    {
        var store = new EngineAgentStageStore(db);
        string[] key = [map.SourceArtifact];
        var source = (await store.LatestLockedAsync(run, key, cancellationToken))
            .Concat(await store.RunLatestAsync(run, key, cancellationToken))
            .FirstOrDefault(a => ArtifactRules.IntegrityOf(a.Status, a.Content, a.ContentHash) == ArtifactIntegrity.Ok)
            ?? throw new JobFailedException(
                $"Map stage {stage.Id} can't read its source: artifact '{map.SourceArtifact}' is missing.", transient: false, EngineErrorCodes.PhasePreconditionFailed);
        var read = MapRules.ReadItems(source.Content, map.ItemPath);
        return read.Problem is { } problem
            ? throw new JobFailedException($"Map stage {stage.Id} can't read its source: {problem}.", transient: false, EngineErrorCodes.Validation)
            : read.Items;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} entered map stage {StageId} ({ItemCount} items)")]
    private static partial void LogMapEntered(ILogger logger, Guid pipelineRunId, string stageId, int itemCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline run {PipelineRunId} map stage {StageId} failed: {Reason}")]
    private static partial void LogMapFailed(ILogger logger, Guid pipelineRunId, string stageId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} map stage {StageId}: every item has finished")]
    private static partial void LogMapDone(ILogger logger, Guid pipelineRunId, string stageId);
}
