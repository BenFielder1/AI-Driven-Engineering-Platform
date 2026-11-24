using System.Text.Json;
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
        var stage = definition.FindStage(run.CurrentStage)
            ?? throw new JobFailedException($"Stage '{run.CurrentStage}' is not in the run's definition.", transient: false, EngineErrorCodes.DefinitionUnavailable);

        var facts = await EntryFactsAsync(run, stage, execution!, definition, cancellationToken);
        var step = RunDriverRules.Plan(stage, facts);
        LogStep(logger, run.Id, stage.Id, step.GetType().Name);
        switch (step)
        {
            case DriverStep.WaitOnInput:
                if (PipelineRunRules.InputMove(run.Status, waiting: true) is { } waiting)
                {
                    run.Status = waiting;
                    await db.SaveChangesAsync(cancellationToken);
                }

                return;

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
                return;

            case DriverStep.WaitOnGate:
            case DriverStep.StopManual:
                return;

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
                    return;
                }

                break;

            case DriverStep.RunHandler handler:
                var dispatch = await handlers.RunAsync(
                    new StageHandlerRequest(run.Id, run.ProjectId, run.DefinitionHash, handler.Handler)
                    {
                        Facts = FactBag.Empty.With(new StageJobFact(job)),
                    },
                    cancellationToken);
                var handled = dispatch.RequireRan(handler.Handler);
                handled.ThrowIfFailed(handler.Handler);
                if (handled.Outcome == StageHandlerOutcome.Waiting)
                {
                    return;
                }

                break;

            case DriverStep.PassApproval:
                break;

            default:
                throw new JobFailedException($"Stage {stage.Id} has a kind the run driver can't run yet.", transient: false, EngineErrorCodes.DefinitionUnavailable);
        }

        await MoveOnAsync(run, definition, cancellationToken);
    }

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
    public static Job QueueDriver(JobEnqueuer jobs, PipelineRun run, Guid stageExecutionId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(run);
        return jobs.Enqueue(EngineJobTypes.StageRun, new StageRunPayload(run.Id, stageExecutionId), run.ProjectId, now);
    }
}
