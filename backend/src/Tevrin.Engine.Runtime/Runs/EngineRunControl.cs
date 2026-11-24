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
public sealed record EngineGateDecision(Guid? UserId, string? Comment);

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

    /// <summary>FR-333: the run ends <c>cancelled</c>; its pending gates and queued jobs are cancelled.</summary>
    public async Task<Result<PipelineRun>> CancelAsync(PipelineRun run, CancellationToken cancellationToken)
    {
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
            .Where(e => e.PipelineRunId == run.Id && e.Checkpoint == targetCheckpoint)
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

        EngineRunMoves.QueueDriver(jobs, run, execution.Id, time.GetUtcNow());
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

        if (await OpenExecutionAsync(run, cancellationToken) is { } execution
            && (await DriverJobsAsync(run, [JobStatus.Queued, JobStatus.Running], cancellationToken)).Count == 0)
        {
            EngineRunMoves.QueueDriver(jobs, run, execution.Id, now);
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

        var now = time.GetUtcNow();
        Decide(gate!, GateStatus.Approved, decision, now);
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

        var now = time.GetUtcNow();
        Decide(gate!, GateStatus.Rejected, decision, now);
        var state = new RunState(run.CurrentStage, run.PackStatus, run.Checkpoint, run.Iterations);
        var stage = definition!.FindStage(run.CurrentStage);
        var cap = stage?.OnReject?.MaxIterations is { } limit ? await CapAsync(run, limit, cancellationToken) : null;
        var outcome = gate!.Type == EngineGates.Escalation || stage?.Gate?.Type != gate.Type
            ? new GateRejection.FailRun($"The {gate.Type} gate was rejected.")
            : RunDriverRules.OnGateRejected(definition, state, gate.Type, cap);
        switch (outcome)
        {
            case GateRejection.Reenter reenter:
                await EngineRunMoves.EnterAsync(db, jobs, run, reenter.State, PipelineTransitionKinds.Loop, now, cancellationToken);
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

    private Task<StageExecution?> OpenExecutionAsync(PipelineRun run, CancellationToken cancellationToken) =>
        db.StageExecutions
            .Where(e => e.PipelineRunId == run.Id && e.ExitedAt == null)
            .OrderByDescending(e => e.EnteredAt).ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Cancels the run's pending gates and its queued <c>stage.run</c> jobs.</summary>
    private async Task CancelPendingAsync(PipelineRun run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var gate in await db.ApprovalGates.Where(g => g.PipelineRunId == run.Id && g.Status == GateStatus.Pending).ToListAsync(cancellationToken))
        {
            gate.Status = GateStatus.Cancelled;
            gate.DecidedAt = now;
        }

        foreach (var job in await DriverJobsAsync(run, [JobStatus.Queued], cancellationToken))
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = now;
        }
    }

    /// <summary>The run's tracked <c>stage.run</c> jobs in <paramref name="statuses"/> (saved and added in this unit of work).</summary>
    private async Task<List<Job>> DriverJobsAsync(PipelineRun run, IReadOnlyCollection<JobStatus> statuses, CancellationToken cancellationToken)
    {
        var projectId = run.ProjectId;
        var stored = await db.Jobs
            .Where(j => j.ProjectId == projectId && j.Type == EngineJobTypes.StageRun && statuses.Contains(j.Status))
            .ToListAsync(cancellationToken);
        var added = db.ChangeTracker.Entries<Job>()
            .Where(e => e.State == EntityState.Added && e.Entity.Type == EngineJobTypes.StageRun && statuses.Contains(e.Entity.Status))
            .Select(e => e.Entity);
        return [.. stored.Union(added).Where(j => StageRunPayload.Parse(j.Payload)?.RunId == run.Id)];
    }
}
