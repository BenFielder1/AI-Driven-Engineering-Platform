using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Common;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Jobs;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Agents;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>
/// FR-345, FR-346, §4.65: fan-out and aggregation. A fan-out stage's own execution (no branch) runs the stage's entry
/// (requirements and gate) and then opens one execution per branch (<see cref="StageExecution.Branch"/> and
/// <see cref="StageExecution.Variant"/> = the variant label, the stage id unchanged), each with its own <c>stage.run</c>
/// job and agent run. Branches reuse the parallel groups' race-safe join: every branch move touches the run, so its
/// <c>xmin</c> lets one concurrent move win and the losers retry. When every branch has completed, the stage's transition
/// (veto, route, next) runs once, on the stage's own execution. A failed branch fails the stage (its job fails; fan-out has
/// no <c>on_branch_failure</c>).
/// </summary>
public sealed partial class EngineRunDriver
{
    private async Task DriveFanOutAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, StageExecution execution, PipelineDefinition definition, bool retry, CancellationToken cancellationToken)
    {
        if (execution.Branch is not null)
        {
            if (!FanOutRules.InFanOut(stage, execution))
            {
                LogStale(logger, run.Id, execution.Id, "the branch is not a fan-out branch of the run's current stage");
                return;
            }

            await RunFanOutBranchAsync(job, run, stage, execution, definition, cancellationToken);
            return;
        }

        var entry = await FanOutEntryAsync(run, stage, execution, cancellationToken);
        var now = time.GetUtcNow();
        if (entry.Count == 0)
        {
            var facts = await EntryFactsAsync(run, stage, execution, definition, cancellationToken);
            if (RunDriverRules.Plan(stage, facts) is not DriverStep.RunAgent)
            {
                // Waiting on input or a gate, or a failed requirement: handled as for any stage (never runs the agent).
                await RunStageAsync(job, run, stage, execution, definition, cancellationToken);
                return;
            }

            EngineRunMoves.Touch(db, run);
            var branches = FanOutRules.Branches(stage);
            foreach (var branch in branches)
            {
                var opened = await StageExecutionWriter.EnterBranchAsync(
                    db, run.Id, stage.Id, branch.Label, run.Checkpoint, now, cancellationToken, variant: branch.Label);
                EngineRunMoves.QueueDriver(jobs, run, opened.Id, now);
            }

            LogFanOutEntered(logger, run.Id, stage.Id, branches.Count);
            await SaveBranchMoveAsync(cancellationToken);
            return;
        }

        // A retry or a redelivery: queue every open branch without a queued or running job again, then check the join.
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

        if (!await TryJoinFanOutAsync(job, run, stage, execution, definition, entry, cancellationToken))
        {
            await SaveBranchMoveAsync(cancellationToken);
        }
    }

    /// <summary>FR-345: one branch: its agent (the binding's, or the stage's for <c>count</c>) under its variant, then the join.</summary>
    private async Task RunFanOutBranchAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, StageExecution execution, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        var branch = FanOutRules.BranchOf(stage, execution.Branch)!;
        var result = await agents.RunAsync(
            new AgentStageRequest(run.Id, stage.Id, JobTriggers.StageEntered)
            {
                AgentRunId = AgentRunRules.RunId(job.Id, AgentRunKey),
                JobAttempt = job.Attempt,
                AgentKey = branch.AgentKey,
                Variant = branch.Label,
            },
            cancellationToken);
        result.ThrowIfFailed();
        if (result.End != AgentStageEnd.Stored)
        {
            // Escalated: the escalation gate is pending; its decision re-queues the driver.
            return;
        }

        if (!await StillInGroupAsync(run, execution, cancellationToken))
        {
            return;
        }

        var fanOutEntry = await GroupEntryAsync(run, cancellationToken);
        var entry = fanOutEntry is null ? [] : await FanOutEntryAsync(run, stage, fanOutEntry, cancellationToken);
        EngineRunMoves.Touch(db, run);
        execution.ExitedAt = time.GetUtcNow();
        execution.Outcome = BranchOutcomes.Completed;
        if (fanOutEntry is not null && await TryJoinFanOutAsync(job, run, stage, fanOutEntry, definition, entry, cancellationToken))
        {
            return;
        }

        await SaveBranchMoveAsync(cancellationToken);
    }

    /// <summary>FR-345: when every branch has completed, the stage's transition runs (saved there; true). Else false.
    /// FR-349: a variants stage selects first (<see cref="SelectVariantAsync"/>).</summary>
    private async Task<bool> TryJoinFanOutAsync(
        JobExecution job,
        PipelineRun run,
        StageDefinition stage,
        StageExecution fanOutEntry,
        PipelineDefinition definition,
        IReadOnlyList<StageExecution> entry,
        CancellationToken cancellationToken)
    {
        if (!FanOutRules.Joined(stage, entry))
        {
            return false;
        }

        LogJoined(logger, run.Id, stage.Id, 0);
        if (stage.Variants is not null)
        {
            // FR-349: the variants' selection (a gate or a selector agent), then the promotion and the transition.
            return await SelectVariantAsync(job, run, stage, fanOutEntry, definition, cancellationToken);
        }

        try
        {
            await TransitionAsync(job, run, stage, fanOutEntry, definition, output: null, cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw Moved(ex);
        }

        return true;
    }

    /// <summary>The branch executions of the fan-out stage's current entry (stored, tracked, and added in this unit of work).</summary>
    private async Task<List<StageExecution>> FanOutEntryAsync(
        PipelineRun run, StageDefinition stage, StageExecution fanOutEntry, CancellationToken cancellationToken)
    {
        var enteredAt = fanOutEntry.EnteredAt;
        var runId = run.Id;
        var stageId = stage.Id;
        var stored = await db.StageExecutions
            .Where(e => e.PipelineRunId == runId && e.Branch != null && e.StageId == stageId && e.EnteredAt >= enteredAt)
            .ToListAsync(cancellationToken);
        var added = db.ChangeTracker.Entries<StageExecution>()
            .Where(e => e.State == EntityState.Added && e.Entity.PipelineRunId == runId && e.Entity.Branch != null)
            .Select(e => e.Entity);
        return [.. stored.Union(added).Where(e => FanOutRules.InFanOut(stage, e))];
    }

    /// <summary>
    /// FR-346: an aggregate stage's <c>disagreement</c> guard, run over every branch output once the stage's work is done.
    /// Its reasons flag the aggregate without failing it: they're stored on the stage's execution
    /// (<see cref="StageExecution.VetoFindings"/>, a JSON array of <c>"&lt;guard&gt;: &lt;reason&gt;"</c>), whose outcome stays
    /// the normal transition (a veto's is <c>vetoed</c> or a backward move), and the run goes on.
    /// </summary>
    private async Task FlagDisagreementAsync(
        PipelineRun run, StageDefinition stage, StageExecution execution, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        if (stage.Aggregate?.DisagreementGuard is not { } name)
        {
            return;
        }

        var guard = services.GetPipelineGuard(name)
            ?? throw new JobFailedException($"Guard '{name}' is not bound.", transient: false, EngineErrorCodes.DefinitionUnavailable);
        var facts = await AggregateFactsAsync(run, stage, definition, FactBag.Empty, cancellationToken);
        var result = await guard.EvaluateAsync(new GuardContext(run.Id, run.ProjectId, stage.Id, run.ApprovalMode, facts), cancellationToken);
        if (result.Ok)
        {
            return;
        }

        LogDisagreement(logger, run.Id, stage.Id, result.Reasons.Count);
        execution.VetoFindings = JsonSerializer.Serialize(result.Reasons.Select(r => $"{name}: {r}").ToList());
    }

    /// <summary>FR-346: <paramref name="facts"/> with the aggregate's typed list (<see cref="AggregateListFact"/>) when the stage aggregates.</summary>
    private async Task<FactBag> AggregateFactsAsync(
        PipelineRun run, StageDefinition stage, PipelineDefinition definition, FactBag facts, CancellationToken cancellationToken)
    {
        if (stage.Aggregate is null)
        {
            return facts;
        }

        var items = await AggregateInputs.ItemsAsync(new EngineAgentStageStore(db), run, definition, stage.Id, cancellationToken);
        return facts.With(new AggregateListFact(items));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} fanned out {StageId} ({BranchCount} branches)")]
    private static partial void LogFanOutEntered(ILogger logger, Guid pipelineRunId, string stageId, int branchCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline run {PipelineRunId} aggregate {StageId} flagged a disagreement ({FindingCount} findings)")]
    private static partial void LogDisagreement(ILogger logger, Guid pipelineRunId, string stageId, int findingCount);
}
