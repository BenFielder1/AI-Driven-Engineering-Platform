using Aide.Api.Features.Execution;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aide.Api.Features.Lifecycle;

/// <summary>Outcome of "advance to next phase": the applied transition, or the pending gate created instead.</summary>
public sealed record AdvanceResult(PhaseTransition? Transition, ApprovalGate? Gate);

/// <summary>
/// FR-11, FR-12: "advance to next phase". The domain state machine decides (entry conditions, gate per approval mode);
/// this service loads the facts and writes the outcome in one transaction guarded by the project's <c>xmin</c>.
/// </summary>
public sealed partial class LifecycleService(
    AideDbContext db,
    LifecycleSnapshotLoader snapshots,
    LifecycleWriter writer,
    WorkingCopyPreparer workingCopies,
    TimeProvider time,
    ILogger<LifecycleService> logger)
{
    public async Task<Result<AdvanceResult>> AdvanceAsync(Guid projectId, string? reason, string actor, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<AdvanceResult>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        bool? prepared = null;
        if (PhaseOrder.Next(project.CurrentPhase) == Phase.Execution)
        {
            // FR-35, §4.1: prepare the working copy and project branch before execution is entered (idempotent). The
            // outcome is the "working copy prepared" condition: a failure leaves it unmet, which the state machine reports.
            prepared = (await workingCopies.PrepareAsync(project.Id, cancellationToken)).IsSuccess;
        }

        var state = new LifecycleState(project.CurrentPhase, project.Status, project.ProjectVersion);
        var snapshot = await snapshots.LoadAsync(project, cancellationToken);
        if (prepared is { } ready)
        {
            snapshot = snapshot with { WorkingCopyPrepared = ready };
        }

        var cleanReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var outcome = LifecycleStateMachine.RequestAdvance(state, snapshot, cleanReason);
        if (outcome.IsFailure)
        {
            LogAdvanceRefused(logger, project.Id, outcome.Error!.Code);
            return Result.Failure<AdvanceResult>(outcome.Error!);
        }

        var now = time.GetUtcNow();
        if (outcome.Value.GateRequired is { } gateType)
        {
            var artifactId = await GatedArtifactIdAsync(project.Id, gateType, cancellationToken);
            var gate = writer.AddGate(project, gateType, artifactId, actor, now, cleanReason);
            var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
            if (saved.IsFailure)
            {
                return Result.Failure<AdvanceResult>(saved.Error!);
            }

            LogGateCreated(logger, project.Id, gateType);
            return Result.Success(new AdvanceResult(null, gate));
        }

        var transition = outcome.Value.Transition!;
        await writer.ApplyAsync(project, transition, actor, now, cancellationToken);
        var result = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (result.IsFailure)
        {
            return Result.Failure<AdvanceResult>(result.Error!);
        }

        LogAdvanced(logger, project.Id, transition.From, transition.To, transition.Version);
        return Result.Success(new AdvanceResult(transition, null));
    }

    /// <summary>The latest locked version of the artifact a gate approves (spec or plan), if any.</summary>
    internal async Task<Guid?> GatedArtifactIdAsync(Guid projectId, GateType type, CancellationToken cancellationToken)
    {
        if (GateRules.GatedArtifact(type) is not { } artifactType)
        {
            return null;
        }

        return await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Type == artifactType && a.Status == ArtifactStatus.Locked)
            .OrderByDescending(a => a.Version)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} advance refused ({ErrorCode})")]
    private static partial void LogAdvanceRefused(ILogger logger, Guid projectId, string errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} needs a {GateType} gate before advancing; gate created")]
    private static partial void LogGateCreated(ILogger logger, Guid projectId, GateType gateType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} moved {FromPhase} → {ToPhase} (version {ProjectVersion})")]
    private static partial void LogAdvanced(ILogger logger, Guid projectId, Phase fromPhase, Phase toPhase, int projectVersion);
}

/// <summary>Saves a lifecycle unit of work, mapping concurrency clashes to the spec's error codes.</summary>
internal static class LifecyclePersistence
{
    public const string PendingGateIndex = "ux_approval_gates_project_id_type_pending";

    /// <summary>
    /// One <c>SaveChanges</c> (one transaction). A stale project/gate <c>xmin</c> → <c>CONFLICT</c>; a second pending gate
    /// of the same type (partial unique index) → <c>GATE_PENDING</c>; any other unique clash (e.g. a second active
    /// execution job) → <c>CONFLICT</c>.
    /// </summary>
    public static async Task<Result> SaveAsync(AideDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(new DomainError(ErrorCodes.Conflict, "The project was changed by someone else. Reload and try again."));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            return Result.Failure(pg.ConstraintName == PendingGateIndex
                ? new DomainError(ErrorCodes.GatePending, "A gate of this type is already pending.")
                : new DomainError(ErrorCodes.Conflict, "The project was changed by someone else. Reload and try again."));
        }
    }
}
