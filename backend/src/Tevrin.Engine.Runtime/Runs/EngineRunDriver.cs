using System.Text.Json;
using System.Text.Json.Serialization;
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
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Engine.Runtime.Stages;

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>FR-332: the payload of a <c>stage.run</c> job: the run and the stage execution (entry) it drives.</summary>
public sealed record StageRunPayload(Guid RunId, Guid StageExecutionId)
{
    /// <summary>FR-333, FR-343: queued by a retry. A parallel group's job then also resumes branches whose last job failed
    /// or was dead-lettered; otherwise (an input, a gate or an escalation decision) only the others. Omitted when false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Retry { get; init; }

    /// <summary>Reads a stored payload (camelCase JSON); <c>null</c> when it isn't one.</summary>
    public static StageRunPayload? Parse(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<StageRunPayload>(json, JsonSerializerOptions.Web);
            return payload is { RunId: var run, StageExecutionId: var execution } && run != Guid.Empty && execution != Guid.Empty ? payload : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// FR-332, §4.62: the run driver. One <c>stage.run</c> job moves an engine-controlled run (<see cref="RunControl.Engine"/>)
/// through the stage of its payload's execution: stop when the run is final or the execution finished (idempotent on
/// redelivery); wait on a missing input; fail the stage on any other unmet requirement; raise or wait on the entry gate;
/// run the stage by kind (agent → <see cref="AgentStageCoordinator"/>, handler → <see cref="StageHandlerRunner"/>, manual →
/// stop, or pass a gate-only stage once approved); then advance and queue itself for the next stage, or complete the run.
/// Host-controlled runs (the SDLC) are never driven. The decisions are <see cref="RunDriverRules"/>.
/// </summary>
public sealed partial class EngineRunDriver(
    IEngineDbContext db,
    PipelineDefinitionResolver definitions,
    PipelineRegistry registry,
    AgentStageCoordinator agents,
    StageHandlerRunner handlers,
    JobEnqueuer jobs,
    IServiceProvider services,
    TimeProvider time,
    ILogger<EngineRunDriver> logger)
{
    /// <summary>The run key of the agent run a <c>stage.run</c> job starts (its id is derived from the job id, FR-310).</summary>
    public const string AgentRunKey = "stage-run";

    public async Task DriveAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (StageRunPayload.Parse(job.Payload) is not { } payload)
        {
            throw new JobFailedException("The stage.run payload is not valid.", transient: false, EngineErrorCodes.Validation);
        }

        var run = await db.PipelineRuns.FirstOrDefaultAsync(r => r.Id == payload.RunId, cancellationToken);
        if (run is null)
        {
            LogStale(logger, payload.RunId, payload.StageExecutionId, "the run doesn't exist");
            return;
        }

        if (registry.RunControlOf(run.PipelineId) == RunControl.Host)
        {
            LogHostControlled(logger, run.Id, run.PipelineId);
            return;
        }

        var execution = await db.StageExecutions.FirstOrDefaultAsync(e => e.Id == payload.StageExecutionId, cancellationToken);
        if (RunDriverRules.IsStale(run, execution))
        {
            LogStale(logger, run.Id, payload.StageExecutionId, "the run is final or the stage execution is finished");
            return;
        }

        var resolved = await definitions.ResolveAsync(run.DefinitionHash, cancellationToken);
        if (resolved is not { Available: true })
        {
            throw new JobFailedException(
                $"Pipeline run {run.Id} is pinned to a definition this build can't run.", transient: false, EngineErrorCodes.DefinitionUnavailable);
        }

        var definition = resolved.Definition;
        var current = definition.FindStage(run.CurrentStage)
            ?? throw new JobFailedException($"Stage '{run.CurrentStage}' is not in the run's definition.", transient: false, EngineErrorCodes.DefinitionUnavailable);

        // FR-343: inside a parallel group the run's current stage is the group; each branch stage has its own execution
        // (Branch set) and its own stage.run job. The group's own execution enters (or resumes) the branches.
        var group = current.Parallel;
        if (current.FanOut is not null || current.Variants is not null)
        {
            // FR-345: a fan-out stage's own execution enters its branches; each branch execution runs one agent.
            await DriveFanOutAsync(job, run, current, execution!, definition, payload.Retry, cancellationToken);
            return;
        }

        if (execution!.Branch is not null && (group is null || !RunDriverRules.InGroup(group, execution)))
        {
            LogStale(logger, run.Id, payload.StageExecutionId, "the branch is not in the run's current group");
            return;
        }

        if (group is not null && execution.Branch is null)
        {
            await DriveGroupAsync(job, run, execution, group, definition, payload.Retry, cancellationToken);
            return;
        }

        var stage = execution.Branch is null ? current : definition.FindAnyStage(execution.StageId)!;
        string? output;
        try
        {
            (var done, output) = await RunStageAsync(job, run, stage, execution, definition, cancellationToken);
            if (!done)
            {
                return;
            }
        }
        catch (JobFailedException ex) when (execution.Branch is not null && !ex.Transient && RunDriverRules.BranchFailureEndsBranch(group!))
        {
            // Known limit: only a permanent failure ends the branch here. A transient one that uses up its retries is
            // dead-lettered by the worker, outside the driver, so under continue that branch stays open until a manual
            // retry (FR-333), and the join waits for it.
            // FR-343, §4.64 on_branch_failure: continue: the branch ends failed (marked) and the join is checked; the job
            // still records the failure.
            await BranchFailedAsync(run, execution, group!, definition, cancellationToken);
            throw;
        }

        if (execution.Branch is not null)
        {
            await BranchStageDoneAsync(job, run, execution, group!, definition, cancellationToken);
            return;
        }

        await FlagDisagreementAsync(run, stage, execution, definition, cancellationToken);
        await TransitionAsync(job, run, stage, execution, definition, output, cancellationToken);
    }

    /// <summary>
    /// §4.62 steps 2–4 for <paramref name="stage"/>: false when the job stops here (waiting on input, a gate, an
    /// escalation or a handler, or a manual stop); true, with an agent stage's output text, when the stage's work is done.
    /// </summary>
    private async Task<(bool Done, string? Output)> RunStageAsync(
        JobExecution job, PipelineRun run, StageDefinition stage, StageExecution execution, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        var facts = await EntryFactsAsync(run, stage, execution, definition, cancellationToken);
        var step = RunDriverRules.Plan(stage, facts);
        LogStep(logger, run.Id, stage.Id, step.GetType().Name);
        string? output = null;
        switch (step)
        {
            case DriverStep.WaitOnInput:
                if (PipelineRunRules.InputMove(run.Status, waiting: true) is { } waiting)
                {
                    run.Status = waiting;
                    await db.SaveChangesAsync(cancellationToken);
                }

                return (false, null);

            case DriverStep.FailStage fail:
                throw new JobFailedException(
                    $"Can't run {stage.Id}: {string.Join(" ", fail.Reasons)}", transient: false, EngineErrorCodes.PhasePreconditionFailed);

            case DriverStep.RaiseGate raise:
                // The run row is written with the gate, so a concurrent cancel or rollback (which change the run) makes this
                // save fail on the run's xmin instead of leaving a pending gate on a run that moved on.
                EngineRunMoves.Touch(db, run);
                db.ApprovalGates.Add(new ApprovalGate
                {
                    ProjectId = run.ProjectId,
                    ReleaseVersionId = run.SubjectId,
                    PipelineRunId = run.Id,
                    Type = raise.GateType,
                    Status = GateStatus.Pending,
                    CreatedAt = time.GetUtcNow(),
                });
                await db.SaveChangesAsync(cancellationToken);
                return (false, null);

            case DriverStep.WaitOnGate:
            case DriverStep.StopManual:
                return (false, null);

            case DriverStep.RunAgent:
                var result = await agents.RunAsync(
                    new AgentStageRequest(run.Id, stage.Id, JobTriggers.StageEntered)
                    {
                        AgentRunId = AgentRunRules.RunId(job.Id, AgentRunKey),
                        JobAttempt = job.Attempt,
                    },
                    cancellationToken);
                result.ThrowIfFailed();
                if (result.End != AgentStageEnd.Stored)
                {
                    // Escalated: the escalation gate is pending; its decision re-queues the driver.
                    return (false, null);
                }

                output = OutputText(result.Last);
                break;

            case DriverStep.RunHandler handler:
                var dispatch = await handlers.RunAsync(
                    new StageHandlerRequest(run.Id, run.ProjectId, run.DefinitionHash, handler.Handler)
                    {
                        Facts = await AggregateFactsAsync(run, stage, definition, FactBag.Empty.With(new StageJobFact(job)), cancellationToken),
                    },
                    cancellationToken);
                var handled = dispatch.RequireRan(handler.Handler);
                handled.ThrowIfFailed(handler.Handler);
                if (handled.Outcome == StageHandlerOutcome.Waiting)
                {
                    return (false, null);
                }

                break;

            case DriverStep.PassApproval:
                break;

            default:
                throw new JobFailedException($"Stage {stage.Id} has a kind the run driver can't run yet.", transient: false, EngineErrorCodes.DefinitionUnavailable);
        }

        return (true, output);
    }

    /// <summary>§4.62 steps 5–6 for a top-level stage whose work is done: the veto, then a route, else the next stage.</summary>
    private async Task TransitionAsync(
        JobExecution job,
        PipelineRun run,
        StageDefinition stage,
        StageExecution execution,
        PipelineDefinition definition,
        string? output,
        CancellationToken cancellationToken)
    {
        // §4.62 step 5: the veto, then the transition (a route, else next).
        if (stage.Veto.Count > 0)
        {
            var findings = await VetoFindingsAsync(job, run, stage, cancellationToken);
            if (findings.Count > 0)
            {
                await VetoAsync(run, execution, definition, findings, cancellationToken);
                return;
            }
        }

        switch (RunDriverRules.Route(stage, RunDriverRules.ReadRoute(output)))
        {
            case RouteChoice.Invalid invalid:
                throw new JobFailedException(invalid.Reason, transient: false, EngineErrorCodes.Validation);

            case RouteChoice.Back to:
                LogRoute(logger, run.Id, stage.Id, to.Stage);
                await BackAsync(run, definition, RunDriverRules.Back(definition, State(run), to.Stage, run.Revisions), cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return;
        }

        await MoveOnAsync(run, definition, cancellationToken);
    }

    private static RunState State(PipelineRun run) => new(run.CurrentStage, run.PackStatus, run.Checkpoint, run.Iterations);

    /// <summary>FR-347: the text an agent stage's last attempt produced: its stored artifact, else the run's output text.</summary>
    private static string? OutputText(AgentStageOutcome outcome)
    {
        if (outcome.Artifact is { } artifact)
        {
            return artifact.Content;
        }

        if (string.IsNullOrEmpty(outcome.AgentRun.Output))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(outcome.AgentRun.Output);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : outcome.AgentRun.Output;
        }
        catch (JsonException)
        {
            return outcome.AgentRun.Output;
        }
    }

    /// <summary>
    /// FR-344: every <c>veto</c> entry of <paramref name="stage"/>, in order: a guard's reasons when it isn't met, and a veto
    /// agent's findings (its own agent run, id derived from the job, so a redelivery reuses it). An unreadable veto agent
    /// output fails the stage (<c>VALIDATION</c>).
    /// </summary>
    private async Task<IReadOnlyList<string>> VetoFindingsAsync(JobExecution job, PipelineRun run, StageDefinition stage, CancellationToken cancellationToken)
    {
        var findings = new List<string>();
        foreach (var veto in stage.Veto)
        {
            if (veto.Guard is { } name)
            {
                var guard = services.GetPipelineGuard(name)
                    ?? throw new JobFailedException($"Guard '{name}' is not bound.", transient: false, EngineErrorCodes.DefinitionUnavailable);
                var result = await guard.EvaluateAsync(new GuardContext(run.Id, run.ProjectId, stage.Id, run.ApprovalMode, FactBag.Empty), cancellationToken);
                if (!result.Ok)
                {
                    findings.AddRange(result.Reasons.Count == 0 ? [$"{name} vetoed the stage."] : result.Reasons.Select(r => $"{name}: {r}"));
                }

                continue;
            }

            var agentKey = veto.Agent!;
            var vetoRun = await agents.RunAsync(
                new AgentStageRequest(run.Id, stage.Id, JobTriggers.StageEntered)
                {
                    AgentKey = agentKey,
                    AgentRunId = AgentRunRules.RunId(job.Id, VetoRunKey(agentKey)),
                    JobAttempt = job.Attempt,
                },
                cancellationToken);
            vetoRun.ThrowIfFailed();
            if (vetoRun.End != AgentStageEnd.Stored)
            {
                throw new JobFailedException($"The {agentKey} veto of {stage.Id} did not finish.", transient: false, EngineErrorCodes.Validation);
            }

            var said = RunDriverRules.ReadVetoFindings(OutputText(vetoRun.Last))
                ?? throw new JobFailedException(
                    $"The {agentKey} veto of {stage.Id} must answer {{ \"findings\": [...] }} or {{ \"approve\": true }}.",
                    transient: false,
                    EngineErrorCodes.Validation);
            findings.AddRange(said.Select(f => $"{agentKey}: {f}"));
        }

        return findings;
    }

    /// <summary>FR-344: the run key of a veto agent's run (FR-310).</summary>
    public static string VetoRunKey(string agentKey) => "veto-" + agentKey;

    /// <summary>FR-344: store the findings on the stage execution, then <c>on_veto</c>: back to its target, or end
    /// <c>completed</c> with outcome <c>vetoed</c> (one save).</summary>
    private async Task VetoAsync(
        PipelineRun run, StageExecution execution, PipelineDefinition definition, IReadOnlyList<string> findings, CancellationToken cancellationToken)
    {
        LogVetoed(logger, run.Id, run.CurrentStage, findings.Count);
        execution.VetoFindings = JsonSerializer.Serialize(findings);
        switch (RunDriverRules.OnVetoed(definition, State(run), run.Revisions))
        {
            case VetoNext.Move move:
                await BackAsync(run, definition, move.Back, cancellationToken);
                break;

            case VetoNext.EndVetoed end:
                var now = time.GetUtcNow();
                run.PackStatus = end.PackStatus;
                run.Status = GenericRunStatus.Completed;
                run.Outcome = RunDriverRules.VetoedOutcome;
                run.CompletedAt = now;
                await StageExecutionWriter.EndAsync(db, run.Id, GenericRunStatus.Completed, now, cancellationToken);
                LogCompleted(logger, run.Id);
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>FR-347, FR-344, FR-348: a backward move (in the caller's save).</summary>
    private Task BackAsync(PipelineRun run, PipelineDefinition definition, BackwardMove move, CancellationToken cancellationToken) =>
        EngineRunMoves.BackAsync(db, jobs, run, definition, move, PipelineTransitionKinds.Route, time.GetUtcNow(), cancellationToken);

    /// <summary>§4.62 steps 5–6: advance and queue the driver, or complete the run (one save; the run's xmin guards it).</summary>
    private async Task MoveOnAsync(PipelineRun run, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var state = new RunState(run.CurrentStage, run.PackStatus, run.Checkpoint, run.Iterations);
        switch (RunDriverRules.After(definition, state))
        {
            case DriverNext.Advance advance:
                await EngineRunMoves.EnterAsync(db, jobs, run, advance.State, PipelineTransitionKinds.Advance, now, cancellationToken);
                break;

            case DriverNext.Complete complete:
                run.PackStatus = complete.PackStatus;
                run.Status = GenericRunStatus.Completed;
                run.CompletedAt = now;
                await StageExecutionWriter.EndAsync(db, run.Id, GenericRunStatus.Completed, now, cancellationToken);
                LogCompleted(logger, run.Id);
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>§4.62 steps 2–3: inputs present, other requirements (built-ins and guards) and the entry gate.</summary>
    private async Task<StageEntryFacts> EntryFactsAsync(
        PipelineRun run, StageDefinition stage, StageExecution execution, PipelineDefinition definition, CancellationToken cancellationToken)
    {
        var rows = await db.Artifacts.AsNoTracking()
            .Where(AgentStageScope.InSubject(run))
            .Select(a => new { a.Type, a.Version, a.Status })
            .ToListAsync(cancellationToken);
        var present = rows.Where(a => a.Status == ArtifactStatus.Locked).Select(a => a.Type).ToHashSet(StringComparer.Ordinal);
        var locked = rows.GroupBy(a => a.Type, StringComparer.Ordinal)
            .Where(g => g.MaxBy(a => a.Version)!.Status == ArtifactStatus.Locked)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        // FR-352: a pinned project-scope artifact is a locked version the run has, whatever is added to its history later.
        present.UnionWith(run.PinnedArtifacts.Keys);
        locked.UnionWith(run.PinnedArtifacts.Keys);
        var gates = await db.ApprovalGates.AsNoTracking()
            .Where(g => g.PipelineRunId == run.Id)
            .Select(g => new { g.Type, g.Status, g.DecidedAt })
            .ToListAsync(cancellationToken);
        var pending = gates.Where(g => g.Status == GateStatus.Pending).Select(g => g.Type).ToList();

        var guards = new Dictionary<string, GuardResult>(StringComparer.Ordinal);
        foreach (var guard in stage.Requires.OfType<GuardRequirement>())
        {
            var bound = services.GetPipelineGuard(guard.Guard)
                ?? throw new JobFailedException($"Guard '{guard.Guard}' is not bound.", transient: false, EngineErrorCodes.DefinitionUnavailable);
            guards[guard.Guard] = await bound.EvaluateAsync(
                new GuardContext(run.Id, run.ProjectId, stage.Id, run.ApprovalMode, FactBag.Empty), cancellationToken);
        }

        var facts = new RunFacts
        {
            Mode = run.ApprovalMode,
            LockedArtifacts = locked,
            PendingGates = pending,
            PresentInputs = present,
            Guards = guards,
        };
        var others = stage.Requires.Where(r => r is not InputRequirement).ToList();
        var gateType = stage.Gate?.Type;
        return new StageEntryFacts
        {
            MissingInputs = RunDriverRules.MissingInputs(stage, present),
            UnmetReasons = PipelineStateMachine.UnmetRequirements(others, facts, definition),
            Gate = RunDriverRules.GateState(
                stage,
                run.ApprovalMode,
                approvedSinceEntry: gates.Any(g => g.Type == gateType && g.Status == GateStatus.Approved && g.DecidedAt >= execution.EnteredAt),
                pending: pending.Contains(gateType ?? string.Empty, StringComparer.Ordinal)),
        };
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "stage.run for run {PipelineRunId}, execution {StageExecutionId}: nothing to do ({Why})")]
    private static partial void LogStale(ILogger logger, Guid pipelineRunId, Guid stageExecutionId, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "stage.run refused for run {PipelineRunId}: pipeline {PipelineId} is controlled by its host")]
    private static partial void LogHostControlled(ILogger logger, Guid pipelineRunId, string pipelineId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} stage {StageId}: {Step}")]
    private static partial void LogStep(ILogger logger, Guid pipelineRunId, string stageId, string step);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} stage {StageId} routes back to {Target}")]
    private static partial void LogRoute(ILogger logger, Guid pipelineRunId, string stageId, string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} stage {StageId} vetoed ({FindingCount} findings)")]
    private static partial void LogVetoed(ILogger logger, Guid pipelineRunId, string stageId, int findingCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} completed")]
    private static partial void LogCompleted(ILogger logger, Guid pipelineRunId);
}

/// <summary>FR-332: the <c>stage.run</c> job handler (<see cref="EngineJobTypes.StageRun"/>).</summary>
public sealed class StageRunJobHandler(EngineRunDriver driver) : IJobHandler
{
    public Task HandleAsync(JobExecution job, CancellationToken cancellationToken) => driver.DriveAsync(job, cancellationToken);
}

/// <summary>FR-331–FR-335: moves of an engine-controlled run shared by the driver and the run-control endpoints.</summary>
public static class EngineRunMoves
{
    /// <summary>
    /// The run enters <paramref name="state"/>'s stage by a transition of <paramref name="kind"/>: its position is updated,
    /// the open stage execution closed and a new one opened, and <c>stage.run</c> queued for it, all in the caller's save.
    /// </summary>
    public static async Task<StageExecution> EnterAsync(
        IEngineDbContext db, JobEnqueuer jobs, PipelineRun run, RunState state, string kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(state);
        run.CurrentStage = state.Stage;
        run.PackStatus = state.PackStatus;
        run.Checkpoint = state.Checkpoint;
        run.Iterations = state.Iterations;
        var execution = await StageExecutionWriter.EnterAsync(db, run.Id, state.Stage, state.Checkpoint, kind, now, cancellationToken);
        QueueDriver(jobs, run, execution.Id, now);
        return execution;
    }

    /// <summary>
    /// FR-348, §4.66: applies a backward move in the caller's save. Allowed: the run's revisions are counted and it
    /// re-enters the target by <paramref name="kind"/>. Refused by <c>limits.revisions</c>: <c>then: escalate</c> raises
    /// a pending <c>escalation</c> gate on the run (approving it re-queues the driver on the same stage; rejecting it fails
    /// the run), <c>then: fail</c> fails the run.
    /// </summary>
    public static async Task BackAsync(
        IEngineDbContext db,
        JobEnqueuer jobs,
        PipelineRun run,
        PipelineDefinition definition,
        BackwardMove move,
        string kind,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(definition);
        switch (move)
        {
            case BackwardMove.Reenter reenter:
                run.Revisions = reenter.Revisions;
                await InvalidateDownstreamAsync(db, run, definition, reenter.State.Stage, cancellationToken);
                await EnterAsync(db, jobs, run, reenter.State, kind, now, cancellationToken);
                break;

            case BackwardMove.Refused { Then: InvalidOutcome.Escalate }:
                Touch(db, run);
                if (!await db.ApprovalGates.AnyAsync(
                    g => g.PipelineRunId == run.Id && g.Type == EngineGates.Escalation && g.Status == GateStatus.Pending, cancellationToken))
                {
                    db.ApprovalGates.Add(new ApprovalGate
                    {
                        ProjectId = run.ProjectId,
                        ReleaseVersionId = run.SubjectId,
                        PipelineRunId = run.Id,
                        Type = EngineGates.Escalation,
                        Status = GateStatus.Pending,
                        CreatedAt = now,
                    });
                }

                break;

            case BackwardMove.Refused:
                run.Status = GenericRunStatus.Failed;
                run.PackStatus = RunDriverRules.FinalStatus(definition, GenericRunStatus.Failed);
                run.CompletedAt = now;
                await StageExecutionWriter.EndAsync(db, run.Id, GenericRunStatus.Failed, now, cancellationToken);
                await CancelPendingAsync(db, run, now, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// FR-347, FR-344, FR-332: the cascade of a backward move to <paramref name="targetStage"/>: the run's artifacts that
    /// depend on the target's outputs (<see cref="RunDriverRules.InvalidatedOnBack"/>) and aren't already final
    /// (invalidated, superseded or rejected) are marked <c>invalidated</c>, in the caller's save. Pinned project-scope
    /// artifacts are never touched.
    /// </summary>
    public static async Task InvalidateDownstreamAsync(
        IEngineDbContext db, PipelineRun run, PipelineDefinition definition, string targetStage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(definition);
        var keys = RunDriverRules.InvalidatedOnBack(definition, targetStage)
            .Where(k => !run.PinnedArtifacts.ContainsKey(k))
            .ToList();
        if (keys.Count == 0)
        {
            return;
        }

        var stale = await db.Artifacts
            .Where(AgentStageScope.InSubject(run))
            .Where(a => keys.Contains(a.Type)
                && a.Status != ArtifactStatus.Invalidated && a.Status != ArtifactStatus.Superseded && a.Status != ArtifactStatus.Rejected)
            .ToListAsync(cancellationToken);
        foreach (var artifact in stale)
        {
            artifact.Status = ArtifactStatus.Invalidated;
        }
    }

    /// <summary>Cancels the run's pending gates and its queued <c>stage.run</c> jobs (stored or added in this save).</summary>
    public static async Task CancelPendingAsync(IEngineDbContext db, PipelineRun run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(run);
        foreach (var gate in await db.ApprovalGates.Where(g => g.PipelineRunId == run.Id && g.Status == GateStatus.Pending).ToListAsync(cancellationToken))
        {
            gate.Status = GateStatus.Cancelled;
            gate.DecidedAt = now;
        }

        foreach (var job in await DriverJobsAsync(db, run, [JobStatus.Queued], cancellationToken))
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = now;
        }
    }

    /// <summary>The run's <c>stage.run</c> jobs in <paramref name="statuses"/>, stored or added in this save.</summary>
    public static async Task<List<Job>> DriverJobsAsync(
        IEngineDbContext db, PipelineRun run, IReadOnlyCollection<JobStatus> statuses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(run);
        var projectId = run.ProjectId;
        var stored = await db.Jobs
            .Where(j => j.ProjectId == projectId && j.Type == EngineJobTypes.StageRun && statuses.Contains(j.Status))
            .ToListAsync(cancellationToken);
        var added = db.ChangeTracker.Entries<Job>()
            .Where(e => e.State == EntityState.Added && e.Entity.Type == EngineJobTypes.StageRun && statuses.Contains(e.Entity.Status))
            .Select(e => e.Entity);
        return [.. stored.Union(added).Where(j => StageRunPayload.Parse(j.Payload)?.RunId == run.Id)];
    }

    /// <summary>
    /// Marks <paramref name="run"/> (tracked) modified, so the caller's save updates its row and is guarded by its
    /// <c>xmin</c> token even when nothing else on the run changed: a concurrent move of the run makes one of the saves fail.
    /// </summary>
    public static void Touch(IEngineDbContext db, PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(run);
        var entry = db.ChangeTracker.Entries<PipelineRun>().FirstOrDefault(e => ReferenceEquals(e.Entity, run))
            ?? throw new InvalidOperationException("The run is not tracked.");
        entry.Property(r => r.Status).IsModified = true;
    }

    /// <summary>Queues <c>stage.run</c> for <paramref name="stageExecutionId"/> of <paramref name="run"/> (in the caller's save).</summary>
    public static Job QueueDriver(JobEnqueuer jobs, PipelineRun run, Guid stageExecutionId, DateTimeOffset now, bool retry = false)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(run);
        return jobs.Enqueue(EngineJobTypes.StageRun, new StageRunPayload(run.Id, stageExecutionId) { Retry = retry }, run.ProjectId, now);
    }
}
