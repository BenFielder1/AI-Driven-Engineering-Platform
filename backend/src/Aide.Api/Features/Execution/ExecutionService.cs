using Aide.Api.Features.Lifecycle;
using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Execution;

/// <summary>Payload of an execution job (FR-31): the project and what queued it.</summary>
public sealed record ExecutionJobPayload(Guid ProjectId, string Trigger)
{
    public const string Start = "start";
    public const string Resume = "resume";

    /// <summary>Queued by the previous execution job when it finished (§4.9).</summary>
    public const string Continue = "continue";

    /// <summary>Queued by an escalation <c>continue</c> decision on a blocked step (FR-14, FR-33).</summary>
    public const string EscalationContinue = "escalation_continue";
}

/// <summary>
/// FR-31, FR-37: starting, pausing and resuming execution. <see cref="ExecutionRules"/> decides; this service prepares the
/// working copy (FR-35), then writes the pause flag, the execution job (outbox, at most one per project, §4.9) and the
/// event in one <c>SaveChanges</c> guarded by the project's <c>xmin</c>.
/// </summary>
public sealed partial class ExecutionService(
    AideDbContext db,
    WorkingCopyPreparer preparer,
    JobEnqueuer jobs,
    TimeProvider time,
    ILogger<ExecutionService> logger)
{
    /// <summary>
    /// Start: refused unless runnable (<c>PHASE_PRECONDITION_FAILED</c>) and within budget (<c>BUDGET_EXCEEDED</c>).
    /// Clears the pause flag and queues the execution job; already running → nothing new is queued (idempotent).
    /// </summary>
    public async Task<Result<ExecutionStateDto>> StartAsync(Guid projectId, string actor, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(projectId, cancellationToken);
        if (loaded is not { } found)
        {
            return NotFound();
        }

        var (project, facts, jobActive) = found;

        var allowed = ExecutionRules.CheckStart(facts);
        if (allowed.IsFailure)
        {
            LogRefused(logger, projectId, "start", allowed.Error!.Code);
            return Result.Failure<ExecutionStateDto>(allowed.Error!);
        }

        if (jobActive && !project.ExecutionPaused)
        {
            return Result.Success(await ExecutionQueries.StateAsync(db, project, cancellationToken));
        }

        var prepared = await preparer.PrepareAsync(projectId, cancellationToken);
        if (prepared.IsFailure)
        {
            return Result.Failure<ExecutionStateDto>(prepared.Error!);
        }

        var now = time.GetUtcNow();
        project.ExecutionPaused = false;
        project.UpdatedAt = now;
        await jobs.EnqueueAsync(JobTypes.Execution, new ExecutionJobPayload(projectId, ExecutionJobPayload.Start), projectId, cancellationToken, now);
        db.AddEvent(EventTypes.ExecutionStarted, projectId, new { branch = prepared.Value.Branch }, actor, now);
        return await SaveAsync(project, "start", cancellationToken);
    }

    /// <summary>Pause (FR-37): sets the flag; a running step finishes, no new step starts. Idempotent.</summary>
    public async Task<Result<ExecutionStateDto>> PauseAsync(Guid projectId, string actor, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }

        var allowed = ExecutionRules.CheckPause(project.CurrentPhase);
        if (allowed.IsFailure)
        {
            LogRefused(logger, projectId, "pause", allowed.Error!.Code);
            return Result.Failure<ExecutionStateDto>(allowed.Error!);
        }

        if (project.ExecutionPaused)
        {
            return Result.Success(await ExecutionQueries.StateAsync(db, project, cancellationToken));
        }

        var now = time.GetUtcNow();
        project.ExecutionPaused = true;
        project.UpdatedAt = now;
        db.AddEvent(EventTypes.ExecutionPaused, projectId, new { paused = true }, actor, now);
        return await SaveAsync(project, "pause", cancellationToken);
    }

    /// <summary>
    /// Resume (FR-37): clears the flag and, when work remains, prepares the working copy and queues the execution job
    /// (<c>BUDGET_EXCEEDED</c> if the budget is used up).
    /// </summary>
    public async Task<Result<ExecutionStateDto>> ResumeAsync(Guid projectId, string actor, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(projectId, cancellationToken);
        if (loaded is not { } found)
        {
            return NotFound();
        }

        var (project, facts, _) = found;

        var allowed = ExecutionRules.CheckResume(facts);
        if (allowed.IsFailure)
        {
            LogRefused(logger, projectId, "resume", allowed.Error!.Code);
            return Result.Failure<ExecutionStateDto>(allowed.Error!);
        }

        var now = time.GetUtcNow();
        if (ExecutionRules.HasWork(facts.Steps))
        {
            var prepared = await preparer.PrepareAsync(projectId, cancellationToken);
            if (prepared.IsFailure)
            {
                return Result.Failure<ExecutionStateDto>(prepared.Error!);
            }

            await jobs.EnqueueAsync(JobTypes.Execution, new ExecutionJobPayload(projectId, ExecutionJobPayload.Resume), projectId, cancellationToken, now);
        }

        if (project.ExecutionPaused)
        {
            project.ExecutionPaused = false;
            project.UpdatedAt = now;
            db.AddEvent(EventTypes.ExecutionResumed, projectId, new { paused = false }, actor, now);
        }

        return await SaveAsync(project, "resume", cancellationToken);
    }

    private async Task<(Project Project, ExecutionFacts Facts, bool JobActive)?> LoadAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return null;
        }

        var planId = await ExecutionQueries.CurrentPlanIdAsync(db, projectId, cancellationToken);
        var steps = planId is { } id ? await ExecutionQueries.CurrentStepsAsync(db, id, track: false, cancellationToken) : [];
        var facts = new ExecutionFacts(project.CurrentPhase, planId is not null, [.. steps.Select(ExecutionQueries.ToState)], project.TokenBudget, project.TokensUsed);
        return (project, facts, await ExecutionQueries.JobActiveAsync(db, projectId, cancellationToken));
    }

    private async Task<Result<ExecutionStateDto>> SaveAsync(Project project, string action, CancellationToken cancellationToken)
    {
        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<ExecutionStateDto>(saved.Error!);
        }

        LogControlled(logger, project.Id, action, project.ExecutionPaused);
        return Result.Success(await ExecutionQueries.StateAsync(db, project, cancellationToken));
    }

    private static Result<ExecutionStateDto> NotFound() =>
        Result.Failure<ExecutionStateDto>(new DomainError(ErrorCodes.NotFound, "The project was not found."));

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: execution {Action} refused ({ErrorCode})")]
    private static partial void LogRefused(ILogger logger, Guid projectId, string action, string errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: execution {Action} (paused: {Paused})")]
    private static partial void LogControlled(ILogger logger, Guid projectId, string action, bool paused);
}
