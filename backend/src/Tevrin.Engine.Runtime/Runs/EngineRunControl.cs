using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

namespace Tevrin.Engine.Runtime.Runs;

/// <summary>A gate decision on an engine-controlled run (FR-332).</summary>
public sealed record EngineGateDecision(Guid? UserId, string? Comment)
{
    /// <summary>FR-349: the chosen variant (<c>v1…vn</c>) when approving a <c>variant_select</c> gate; ignored by other gates.</summary>
    public string? Choice { get; init; }
}

/// <summary>
/// FR-332, FR-333, FR-335: cancel, roll back, retry, submit an input and decide gates of engine-controlled runs. Every
/// method takes a tracked run the caller has checked access to, refuses a host-controlled run
/// (<c>RUN_CONTROLLED_BY_HOST</c>) and a final one (<c>RUN_NOT_ACTIVE</c>), and saves its changes in one unit of work
/// (the run's xmin token makes a concurrent move fail with <c>CONFLICT</c>).
/// </summary>
public sealed class EngineRunControl(
    IEngineDbContext db,
    PipelineRegistry registry,
    PipelineDefinitionResolver definitions,
    ArtifactSchemas schemas,
    JobEnqueuer jobs,
    IEnumerable<IRunSettingsProvider> settings,
    TimeProvider time)
{
    /// <summary>PostgreSQL's unique_violation SQLSTATE.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>The tracked run, or <c>null</c>.</summary>
    public Task<PipelineRun?> FindAsync(Guid runId, CancellationToken cancellationToken) =>
        db.PipelineRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

    /// <summary>Whether <paramref name="run"/> is controlled by its host (the SDLC).</summary>
    public bool IsHostControlled(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return registry.RunControlOf(run.PipelineId) == RunControl.Host;
    }

    /// <summary>
    /// FR-333: the run ends <c>cancelled</c>; its pending gates and queued jobs are cancelled. FR-354: a <c>completed</c> run
    /// with queued follow-ups has those cancelled and stays <c>completed</c>; without any it is refused as before
    /// (<c>RUN_NOT_ACTIVE</c>).
    /// </summary>
    public async Task<Result<PipelineRun>> CancelAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Status == GenericRunStatus.Completed && !IsHostControlled(run))
        {
            var followUps = (await EngineRunMoves.DriverJobsAsync(db, run, [JobStatus.Queued], cancellationToken))
                .Where(j => StageRunPayload.Parse(j.Payload)?.FollowUp is not null)
                .ToList();
            if (followUps.Count > 0)
            {
                var at = time.GetUtcNow();
                foreach (var job in followUps)
                {
                    job.Status = JobStatus.Cancelled;
                    job.CompletedAt = at;
                }

                await db.SaveChangesAsync(cancellationToken);
                return Result.Success(run);
            }
        }

        var (definition, refused) = await ControllableAsync(run, cancellationToken);
        if (refused is not null)
        {
            return Result.Failure<PipelineRun>(refused);
        }

        var now = time.GetUtcNow();
        run.Status = GenericRunStatus.Cancelled;
        run.PackStatus = RunDriverRules.FinalStatus(definition!, GenericRunStatus.Cancelled);
        run.CompletedAt = now;
        await StageExecutionWriter.EndAsync(db, run.Id, GenericRunStatus.Cancelled, now, cancellationToken);
        await CancelPendingAsync(run, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(run);
    }

    /// <summary>
    /// FR-333: back to <paramref name="targetCheckpoint"/> by the engine rollback rules: the stage the run was in at that
    /// checkpoint (from its stage executions), the definition's rollback status, artifacts created after it invalidated
    /// (history kept), pending gates and queued jobs cancelled, then the driver queued. An unknown or invalid target gives
    /// <c>INVALID_TARGET_VERSION</c>.
    /// </summary>
    public async Task<Result<PipelineRun>> RollbackAsync(PipelineRun run, int targetCheckpoint, string? reason, CancellationToken cancellationToken)
    {
        var (definition, refused) = await ControllableAsync(run, cancellationToken);
        if (refused is not null)
        {
            return Result.Failure<PipelineRun>(refused);
        }

        var targetStage = await db.StageExecutions.AsNoTracking()
            .Where(e => e.PipelineRunId == run.Id && e.Checkpoint == targetCheckpoint && e.Branch == null)
            .OrderByDescending(e => e.EnteredAt).ThenByDescending(e => e.Id)
            .Select(e => e.StageId)
            .FirstOrDefaultAsync(cancellationToken);
        if (targetStage is null || targetCheckpoint < RunState.InitialCheckpoint || targetCheckpoint >= run.Checkpoint)
        {
            var invalid = new DomainError(EngineErrorCodes.InvalidTargetVersion, TransitionMessages.RollbackCheckpoint(run.Checkpoint))
            {
                Field = "targetCheckpoint",
            };
            return Result.Failure<PipelineRun>(invalid);
        }

        var state = new RunState(run.CurrentStage, run.PackStatus, run.Checkpoint, run.Iterations);
        var applied = PipelineStateMachine.Apply(state, Transition.Rollback(targetStage, targetCheckpoint, reason: reason), RunFacts.Empty, definition!);
        if (applied.IsFailure)
        {
            return Result.Failure<PipelineRun>(applied.Error! with { Code = EngineErrorCodes.InvalidTargetVersion });
        }

        var now = time.GetUtcNow();
        var later = await db.Artifacts
            .Where(AgentStageScope.InSubject(run))
            .Where(a => a.CheckpointAtCreation > targetCheckpoint
                && a.Status != ArtifactStatus.Invalidated && a.Status != ArtifactStatus.Superseded && a.Status != ArtifactStatus.Rejected)
            .ToListAsync(cancellationToken);
        foreach (var artifact in later)
        {
            artifact.Status = ArtifactStatus.Invalidated;
        }

        await CancelPendingAsync(run, now, cancellationToken);
        run.Status = GenericRunStatus.Active;
        await EngineRunMoves.EnterAsync(db, jobs, run, applied.Value.NewState, PipelineTransitionKinds.Rollback, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(run);
    }

    /// <summary>
    /// FR-333: re-queues the driver for the run's current stage, when no <c>stage.run</c> job of the run is queued or
    /// running (else <c>CONFLICT</c>): the stage's last job failed or was dead-lettered.
    /// </summary>
    public async Task<Result<PipelineRun>> RetryAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        var (_, refused) = await ControllableAsync(run, cancellationToken);
        if (refused is not null)
        {
            return Result.Failure<PipelineRun>(refused);
        }

        if ((await DriverJobsAsync(run, [JobStatus.Queued, JobStatus.Running], cancellationToken)).Count > 0)
        {
            return Result.Failure<PipelineRun>(new DomainError(EngineErrorCodes.Conflict, "The run's current stage is still running or queued."));
        }

        // FR-333 retries a failed stage; a run waiting on a gate (an entry gate or an escalation) is decided, not retried.
        if (await db.ApprovalGates.AsNoTracking().AnyAsync(g => g.PipelineRunId == run.Id && g.Status == GateStatus.Pending, cancellationToken))
        {
            return Result.Failure<PipelineRun>(new DomainError(EngineErrorCodes.Conflict, "The run is waiting on a gate; decide the gate instead."));
        }

        if (await OpenExecutionAsync(run, cancellationToken) is not { } execution)
        {
            return Result.Failure<PipelineRun>(new DomainError(EngineErrorCodes.Conflict, "The run has no stage to retry."));
        }

        EngineRunMoves.QueueDriver(jobs, run, execution.Id, time.GetUtcNow(), retry: true);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(run);
    }

    /// <summary>
    /// FR-335: a new locked version of input <paramref name="key"/> (an artifact declared <c>input: true</c>; else
    /// <c>NOT_FOUND</c>), checked against its schema (<c>INPUT_INVALID</c>). A run waiting on input resumes and the driver
    /// is queued. Returns the new version.
    /// </summary>
    public async Task<Result<Artifact>> SubmitInputAsync(
        PipelineRun run, string key, JsonElement value, Guid? userId, CancellationToken cancellationToken)
    {
        var (definition, refused) = await ControllableAsync(run, cancellationToken);
        if (refused is not null)
        {
            return Result.Failure<Artifact>(refused);
        }

        if (definition!.FindArtifact(key) is not { Input: true } artifact)
        {
            return Result.Failure<Artifact>(new DomainError(EngineErrorCodes.NotFound, $"'{key}' is not an input of this run's pipeline."));
        }

        var packId = registry.TryGet(run.PipelineId, out var loaded) ? loaded.PackId : (await definitions.ResolveAsync(run.DefinitionHash, cancellationToken))!.PackId;
        var content = RunInputs.Content(artifact, value, packId, schemas);
        if (content.IsFailure)
        {
            return Result.Failure<Artifact>(content.Error!);
        }

        var latest = await db.Artifacts.AsNoTracking()
            .Where(AgentStageScope.InSubject(run))
            .Where(a => a.Type == key)
            .MaxAsync(a => (int?)a.Version, cancellationToken);
        var now = time.GetUtcNow();
        var version = RunInputs.Version(run, key, (latest ?? 0) + 1, latest, content.Value, userId, now);
        db.Artifacts.Add(version);

        // The run row is always written (xmin), so a driver deciding waiting_on_input at the same time fails its save and
        // retries, then finds the input. A waiting run resumes; the driver is queued unless one is already queued or running.
        EngineRunMoves.Touch(db, run);
        if (PipelineRunRules.InputMove(run.Status, waiting: false) is { } resumed)
        {
            run.Status = resumed;
        }

        // FR-343: in a parallel group a sibling branch's job is usually running; the group's own job is queued anyway (it
        // skips busy branches and resumes the one waiting on this input).
        if (await OpenExecutionAsync(run, cancellationToken) is { } execution)
        {
            var active = (await DriverJobsAsync(run, [JobStatus.Queued, JobStatus.Running], cancellationToken))
                .Select(j => StageRunPayload.Parse(j.Payload))
                .ToList();
            if (RunDriverRules.ResumeOnInput(
                inGroup: definition.FindStage(run.CurrentStage)?.Parallel is not null,
                anyActive: active.Count > 0,
                groupJobQueued: active.Any(a => a?.StageExecutionId == execution.Id)))
            {
                EngineRunMoves.QueueDriver(jobs, run, execution.Id, now);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is DbException { SqlState: UniqueViolation })
        {
            // A concurrent submit took the version number, or the run moved meanwhile: the caller may try again.
            return Result.Failure<Artifact>(new DomainError(EngineErrorCodes.Conflict, "The run changed while the input was being saved. Try again."));
        }

        return Result.Success(version);
    }

    /// <summary>
    /// FR-332: approve a pending gate of an engine-controlled run. The gate's <c>approves</c> artifact (its latest draft in
    /// the run) is locked, and the driver is queued for the run's current stage.
    /// </summary>
    public async Task<Result<ApprovalGate>> ApproveGateAsync(PipelineRun run, Guid gateId, EngineGateDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var (definition, gate, refused) = await PendingGateAsync(run, gateId, cancellationToken);
        if (refused is not null)
        {
            return Result.Failure<ApprovalGate>(refused);
        }

        if (EngineGates.IsMidStage(gate!.Type))
        {
            // FR-359, FR-360: only the waiting job runs the tool or sends the external action (once); nothing else moves.
            return await DecideMidStageAsync(run, gate, GateStatus.Approved, decision, cancellationToken);
        }

        if (gate.Type == EngineGates.VariantSelect)
        {
            // FR-349, §4.67: the decider's choice must be one of the variants the gate offers; it's recorded on the payload,
            // where the driver reads it to promote that variant.
            if (decision.Choice is not { } choice || !VariantRules.PayloadLabels(gate.Payload).Contains(choice, StringComparer.Ordinal))
            {
                return Result.Failure<ApprovalGate>(new DomainError(
                    EngineErrorCodes.VariantChoiceInvalid,
                    $"choice must be one of {string.Join(", ", VariantRules.PayloadLabels(gate.Payload))}.")
                { Field = "choice" });
            }

            gate.Payload = VariantRules.WithChoice(gate.Payload, choice);
        }

        var now = time.GetUtcNow();
        Decide(gate, GateStatus.Approved, decision, now);
        if (definition!.Stages.FirstOrDefault(s => s.Gate?.Type == gate!.Type)?.Gate?.Approves is { } approves)
        {
            var draft = await db.Artifacts
                .Where(AgentStageScope.InSubject(run))
                .Where(a => a.Type == approves && a.Status == ArtifactStatus.Draft)
                .OrderByDescending(a => a.Version)
                .FirstOrDefaultAsync(cancellationToken);
            if (draft is not null)
            {
                draft.Status = ArtifactStatus.Locked;
                draft.ApprovedBy = decision.UserId;
                draft.ApprovedAt = now;
            }
        }

        if (await OpenExecutionAsync(run, cancellationToken) is { } execution)
        {
            EngineRunMoves.QueueDriver(jobs, run, execution.Id, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(gate!);
    }

    /// <summary>
    /// FR-332: reject a pending gate of an engine-controlled run. An entry gate follows its stage's <c>on_reject</c> (the run
    /// re-enters the target, within its cap, and the driver is queued); without one, or for an escalation, the run fails.
    /// </summary>
    public async Task<Result<ApprovalGate>> RejectGateAsync(PipelineRun run, Guid gateId, EngineGateDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var (definition, gate, refused) = await PendingGateAsync(run, gateId, cancellationToken);
        if (refused is not null)
        {
            return Result.Failure<ApprovalGate>(refused);
        }

        if (EngineGates.IsMidStage(gate!.Type))
        {
            // FR-359: a rejected tool call is a tool result (the denial), not a rejected stage: the run goes on. FR-360: the
            // waiting job applies a rejected external handler's on_reject, or fails its stage (EXTERNAL_ACTION_REJECTED).
            return await DecideMidStageAsync(run, gate, GateStatus.Rejected, decision, cancellationToken);
        }

        var now = time.GetUtcNow();
        Decide(gate, GateStatus.Rejected, decision, now);
        var state = new RunState(run.CurrentStage, run.PackStatus, run.Checkpoint, run.Iterations);
        var stage = definition!.FindStage(run.CurrentStage);
        var cap = stage?.OnReject?.MaxIterations is { } limit ? await CapAsync(run, limit, cancellationToken) : null;
        var outcome = gate!.Type == EngineGates.Escalation || stage?.Gate?.Type != gate.Type
            ? new GateRejection.FailRun($"The {gate.Type} gate was rejected.")
            : RunDriverRules.OnGateRejected(definition, state, gate.Type, cap);
        switch (outcome)
        {
            case GateRejection.Reenter reenter:
                // FR-348: on_reject is a backward transition, counted against limits.revisions.
                var move = RunDriverRules.Revise(definition, reenter.State, run.Revisions);
                await EngineRunMoves.BackAsync(db, jobs, run, definition, move, PipelineTransitionKinds.Loop, now, cancellationToken);
                break;

            case GateRejection.FailRun:
                run.Status = GenericRunStatus.Failed;
                run.PackStatus = RunDriverRules.FinalStatus(definition, GenericRunStatus.Failed);
                run.CompletedAt = now;
                await StageExecutionWriter.EndAsync(db, run.Id, GenericRunStatus.Failed, now, cancellationToken);
                await CancelPendingAsync(run, now, cancellationToken);
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(gate);
    }

    /// <summary>
    /// FR-359, §4.73: records the decision on a <c>tool_approval</c> gate and wakes the run's waiting <c>stage.run</c> job
    /// (it was deferred to the gate's timer), whose next delivery runs the tool once or sends the denial and resumes the
    /// loop. FR-360: the same for an <c>external_action</c> gate, whose waiting job sends the approved payload once, or
    /// applies the rejection. No new driver job is queued: only the waiting job may run the call. The run's row is written
    /// too, so a concurrent move of the run fails this save with <c>CONFLICT</c>, as the gate's own token does a concurrent
    /// decision or timer.
    /// </summary>
    private async Task<Result<ApprovalGate>> DecideMidStageAsync(
        PipelineRun run, ApprovalGate gate, GateStatus status, EngineGateDecision decision, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        Decide(gate, status, decision, now);
        EngineRunMoves.Touch(db, run);
        foreach (var job in await DriverJobsAsync(run, [JobStatus.Queued], cancellationToken))
        {
            if (job.RunAfter > now)
            {
                job.RunAfter = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(gate);
    }

    private async Task<int?> CapAsync(PipelineRun run, IterationCap cap, CancellationToken cancellationToken)
    {
        if (cap.Value is { } value)
        {
            return value;
        }

        var provider = settings.FirstOrDefault(s => s.PipelineId == run.PipelineId);
        return provider is null || cap.Setting is null ? null : await provider.GetSettingAsync(run.ProjectId, cap.Setting, cancellationToken);
    }

    private static void Decide(ApprovalGate gate, GateStatus status, EngineGateDecision decision, DateTimeOffset now)
    {
        gate.Status = status;
        gate.ReviewerId = decision.UserId;
        gate.Comment = decision.Comment;
        gate.DecidedAt = now;
    }

    private async Task<(PipelineDefinition? Definition, ApprovalGate? Gate, DomainError? Refused)> PendingGateAsync(
        PipelineRun run, Guid gateId, CancellationToken cancellationToken)
    {
        var (definition, refused) = await ControllableAsync(run, cancellationToken);
        if (refused is not null)
        {
            return (null, null, refused);
        }

        var gate = await db.ApprovalGates.FirstOrDefaultAsync(g => g.Id == gateId && g.PipelineRunId == run.Id, cancellationToken);
        if (gate is null)
        {
            return (null, null, new DomainError(EngineErrorCodes.NotFound, "The gate was not found in this run."));
        }

        return gate.Status != GateStatus.Pending
            ? (null, null, new DomainError(EngineErrorCodes.GateNotPending, "This gate has already been decided."))
            : (definition, gate, null);
    }

    /// <summary>The run's definition when the engine may move it; else why not (host control, final, unavailable).</summary>
    private async Task<(PipelineDefinition? Definition, DomainError? Refused)> ControllableAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (IsHostControlled(run))
        {
            return (null, RunControlErrors.ControlledByHost());
        }

        if (PipelineRunRules.IsFinal(run.Status))
        {
            return (null, RunControlErrors.NotActive());
        }

        var resolved = await definitions.ResolveAsync(run.DefinitionHash, cancellationToken);
        return resolved is { Available: true }
            ? (resolved.Definition, null)
            : (null, new DomainError(EngineErrorCodes.DefinitionUnavailable, "This run uses a pipeline definition this version of Tevrin can't run."));
    }

    /// <summary>The run's open execution of its current stage; inside a parallel group (FR-343), the group's own execution
    /// (not a branch's), whose job resumes every open branch; in a map stage (FR-355), the map's own execution (not an
    /// item's), whose job resumes the items.</summary>
    private Task<StageExecution?> OpenExecutionAsync(PipelineRun run, CancellationToken cancellationToken) =>
        db.StageExecutions
            .Where(e => e.PipelineRunId == run.Id && e.ExitedAt == null && e.Branch == null && e.ParentExecutionId == null)
            .OrderByDescending(e => e.EnteredAt).ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Cancels the run's pending gates and its queued <c>stage.run</c> jobs.</summary>
    private Task CancelPendingAsync(PipelineRun run, DateTimeOffset now, CancellationToken cancellationToken) =>
        EngineRunMoves.CancelPendingAsync(db, run, now, cancellationToken);

    /// <summary>The run's tracked <c>stage.run</c> jobs in <paramref name="statuses"/> (saved and added in this unit of work).</summary>
    private Task<List<Job>> DriverJobsAsync(PipelineRun run, IReadOnlyCollection<JobStatus> statuses, CancellationToken cancellationToken) =>
        EngineRunMoves.DriverJobsAsync(db, run, statuses, cancellationToken);
}
