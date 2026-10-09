using System.Globalization;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Agents;

/// <summary>
/// Follow-up when every step of the current plan is completed (FR-36), called in the same unit of work as the last step's
/// completion and its <c>EXECUTION_COMPLETED</c> event (the caller saves): <see cref="Features.Git.GitCompletionHook"/>
/// queues the pull request job, which opens the PR and advances the project to review.
/// </summary>
public interface IExecutionCompletionHook
{
    Task OnExecutionCompletedAsync(AideDbContext db, Project project, Guid planArtifactId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// One step attempt of the current plan (FR-31–FR-35, §4.9). Each execution job runs at most one attempt; when it
/// succeeds, <see cref="ExecutionContinuationHook"/> queues the next job in the same transaction, unless execution is
/// paused (FR-37) or nothing is runnable.
/// <list type="number">
/// <item>Pick the step: one left <c>in_progress</c> (an interrupted attempt continues, even when paused: the running step
/// finishes), else — unless paused, blocked or out of budget — the next step (<see cref="StepRules.NextStep"/>).</item>
/// <item>Prepare the working copy (FR-35) and look for the step's commit, then start a picked step as a new attempt
/// (<c>STEP_STARTED</c>), so a preparation failure never leaves a step in progress. If the step's commit already exists on
/// the branch (a redelivery after the commit, D17), complete the step from it without calling the agent, with the Guardian's
/// warnings for the committed files.</item>
/// <item>Run the Code Generator with the project request, spec, plan with step statuses, project rules, the declared files'
/// current content and, on a retry, the previous violations as feedback. The run id derives from the job and attempt, so a
/// redelivered job reuses a succeeded run instead of calling the provider again.</item>
/// <item>The Guardian checks the reply (§4.6, all checks). Accepted: write and commit the files (FR-34), publish the branch
/// (a failed push is recorded as <c>GIT_PUSH_FAILED</c>, never undoes the step), record branch, SHA and manifest,
/// <c>STEP_COMPLETED</c> (warnings kept on the step); the last step also writes
/// <c>EXECUTION_COMPLETED</c> and calls <see cref="IExecutionCompletionHook"/>. Rejected: <c>STEP_REJECTED</c> with every
/// violation; the fourth rejection blocks the step (<c>STEP_BLOCKED</c>) and raises an escalation gate (FR-33).</item>
/// </list>
/// </summary>
public sealed partial class ExecutionJobHandler(
    AideDbContext db,
    AgentRunner runner,
    AgentJobInputs inputs,
    WorkingCopyPreparer preparer,
    IWorkingCopies workingCopies,
    LifecycleWriter writer,
    IExecutionCompletionHook completion,
    TimeProvider time,
    ILogger<ExecutionJobHandler> logger) : IJobHandler
{
    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = Payload(job);
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == payload.ProjectId, cancellationToken);
        if (project is null || project.CurrentPhase != Phase.Execution)
        {
            LogSkipped(logger, "the project is gone or not in execution");
            return;
        }

        if (await ExecutionQueries.CurrentPlanIdAsync(db, project.Id, cancellationToken) is not { } planId)
        {
            LogSkipped(logger, "there is no current plan");
            return;
        }

        var steps = await ExecutionQueries.CurrentStepsAsync(db, planId, track: true, cancellationToken);
        var step = steps.FirstOrDefault(s => s.Status == StepStatus.InProgress);
        var resuming = step is not null;
        step ??= await NextStepAsync(project, steps, cancellationToken);
        if (step is null)
        {
            await CompleteIfAllDoneAsync(project.Id, planId, steps, cancellationToken);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["StepId"] = step.Id, ["StepNumber"] = step.StepNumber });

        // Prepare the working copy and look for the step's commit before the step is marked in progress: a preparation
        // failure then retries (or dead-letters) the job without leaving a step in progress.
        var prepared = await preparer.PrepareAsync(project.Id, cancellationToken);
        if (prepared.IsFailure)
        {
            throw new JobFailedException(prepared.Error!.Message, transient: true, prepared.Error.Code);
        }

        var working = prepared.Value;
        var existing = await Git(() => workingCopies.FindStepCommitAsync(project.Id, working.Branch, step.Id, cancellationToken));
        if (!resuming && !await BeginAttemptAsync(project, step, cancellationToken))
        {
            return;
        }

        if (existing is not null)
        {
            // D17 redelivery: the warnings are those of the committed files, not of an earlier rejected attempt.
            LogAlreadyCommitted(logger, existing.Sha);
            var committed = existing.Files.Select(f => new ProducedFile(f.Path, f.Content)).ToList();
            var check = ExecutionGuardian.Validate(new GuardianInput(step.StepNumber, step.StepNumber, [.. steps.Select(ExecutionQueries.ToState)], committed, project.MaxFilesPerStep));
            await CompleteAsync(project.Id, planId, step, steps, existing, [.. check.Warnings], working, cancellationToken);
            return;
        }

        var outcome = await runner.RunAsync(await RequestAsync(job, project, planId, step, steps, cancellationToken), cancellationToken);
        var reply = CodeGeneration.Parse(outcome.Text);
        var result = reply.IsSuccess
            ? ExecutionGuardian.Validate(new GuardianInput(step.StepNumber, reply.Value.StepNumber, [.. steps.Select(ExecutionQueries.ToState)], reply.Value.Files, project.MaxFilesPerStep))
            : new GuardianResult([CodeGeneration.UnreadableViolation(reply.Error!)]);
        if (!result.Accepted)
        {
            await RejectAsync(project.Id, step, result, cancellationToken);
            return;
        }

        var message = CodeGeneration.CommitMessage(step.StepNumber, step.Title, reply.Value.Files.Select(f => f.Path), step.Id);
        var normalised = reply.Value.Files.Select(f => new ProducedFile(PathRules.Normalize(f.Path), f.Content)).ToList();
        var commit = await Git(() => workingCopies.CommitStepAsync(project.Id, working.Branch, step.Id, normalised, message, cancellationToken));
        await CompleteAsync(project.Id, planId, step, steps, commit!, [.. result.Warnings], working, cancellationToken);
    }

    /// <summary>The next step to start, or <c>null</c> when paused, blocked, out of budget or nothing is runnable.</summary>
    private async Task<ExecutionStep?> NextStepAsync(Project project, List<ExecutionStep> steps, CancellationToken cancellationToken)
    {
        if (project.ExecutionPaused)
        {
            LogSkipped(logger, "execution is paused");
            return null;
        }

        var states = steps.Select(ExecutionQueries.ToState).ToList();
        if (!ExecutionRules.HasWork(states) || StepRules.NextStep(states) is not { } number)
        {
            LogSkipped(logger, "no step is runnable");
            return null;
        }

        var now = time.GetUtcNow();
        if (TokenBudget.IsExhausted(project.TokenBudget, project.TokensUsed))
        {
            // FR-26: refuse before the step starts, so it isn't left in progress.
            db.AddEvent(
                EventTypes.BudgetExceeded,
                project.Id,
                new { agent = AgentKind.CodeGenerator.ToWire(), tokenBudget = project.TokenBudget, tokensUsed = project.TokensUsed, stepNumber = number },
                SystemActor.Name,
                now);
            await db.SaveChangesAsync(cancellationToken);
            LogSkipped(logger, "the token budget is used up");
            return null;
        }

        return steps.Single(s => s.StepNumber == number);
    }

    /// <summary>
    /// Nothing to run and every step of the current plan is completed (e.g. execution re-entered for a plan whose steps all
    /// ran): run the completion follow-up (FR-36, the pull request job that advances to review) so the project is never left
    /// in execution with no job. The pull request job is queued at most once and skips a superseded plan.
    /// </summary>
    private async Task CompleteIfAllDoneAsync(Guid projectId, Guid planId, List<ExecutionStep> steps, CancellationToken cancellationToken)
    {
        if (!ExecutionRules.AllCompleted([.. steps.Select(s => s.Status)]))
        {
            return;
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null || project.CurrentPhase != Phase.Execution)
        {
            return;
        }

        await completion.OnExecutionCompletedAsync(db, project, planId, time.GetUtcNow(), cancellationToken);
        await SaveStepAsync(cancellationToken);
        LogExecutionCompleted(logger, steps.Count);
    }

    /// <summary>Starts a new attempt of <paramref name="step"/> (<c>STEP_STARTED</c>); <c>false</c> if it can't start.</summary>
    private async Task<bool> BeginAttemptAsync(Project project, ExecutionStep step, CancellationToken cancellationToken)
    {
        var begun = StepRules.BeginAttempt(step.Status, step.Attempt);
        if (begun.IsFailure)
        {
            return false;
        }

        var now = time.GetUtcNow();
        step.Status = StepStatus.InProgress;
        step.Attempt = begun.Value.Attempt;
        step.StartedAt = now;
        step.CompletedAt = null;
        db.AddEvent(
            EventTypes.StepStarted,
            project.Id,
            new { stepId = step.Id, stepNumber = step.StepNumber, title = step.Title, attempt = step.Attempt, planArtifactId = step.PlanArtifactId },
            SystemActor.Name,
            now);
        await SaveStepAsync(cancellationToken);
        LogStepStarted(logger, step.StepNumber, step.Attempt);
        return true;
    }

    private async Task<AgentRunRequest> RequestAsync(
        JobExecution job, Project project, Guid planId, ExecutionStep step, List<ExecutionStep> steps, CancellationToken cancellationToken)
    {
        var projectRequest = await inputs.CurrentAsync(project.Id, ArtifactType.ProjectRequest, cancellationToken);
        var spec = await inputs.CurrentAsync(project.Id, ArtifactType.TechnicalSpecification, cancellationToken);
        var plan = await db.Artifacts.AsNoTracking().FirstAsync(a => a.Id == planId, cancellationToken);
        var files = await Git(() => workingCopies.ReadAsync(project.Id, step.DeclaredFiles.Select(f => f.Path), cancellationToken));
        var rules = await Git(() => workingCopies.ReadRulesAsync(project.Id, cancellationToken));
        var planView = CodeGeneration.PlanWithStatuses(steps.Select(s => new PlanStepView(s.StepNumber, s.Title, s.Task, s.Status, s.DeclaredFiles, s.Dependencies)));
        return new AgentRunRequest
        {
            ProjectId = project.Id,
            Agent = AgentKind.CodeGenerator,
            Job = job,
            StepId = step.Id,
            RunKey = $"step-{step.Id:N}-attempt-{step.Attempt.ToString(CultureInfo.InvariantCulture)}",
            Inputs = new Dictionary<string, string?>
            {
                ["PROJECT_REQUEST"] = projectRequest?.Content,
                ["TECHNICAL_SPECIFICATION"] = spec?.Content,
                ["IMPLEMENTATION_PLAN"] = planView,
                ["PROJECT_RULES"] = CodeGeneration.ProjectRules(project.MaxFilesPerStep, rules),
                ["MAX_FILES_PER_STEP"] = project.MaxFilesPerStep.ToString(CultureInfo.InvariantCulture),
                ["STEP_NUMBER"] = step.StepNumber.ToString(CultureInfo.InvariantCulture),
                ["STEP_TITLE"] = step.Title,
                ["STEP_TASK"] = step.Task,
                ["DECLARED_FILES"] = CodeGeneration.DeclaredFiles(step.DeclaredFiles),
                ["STEP_USER_INSTRUCTIONS"] = step.UserInstructions,
                ["EXISTING_CODE"] = CodeGeneration.ExistingCode(files!),
            },
            InputArtifacts = [.. new[] { projectRequest, spec, plan }.OfType<Artifact>().Select(AgentJobInputs.Ref)],
            UserFeedback = CodeGeneration.RetryFeedback(step.Violations),
        };
    }

    private async Task CompleteAsync(
        Guid projectId,
        Guid planId,
        ExecutionStep step,
        List<ExecutionStep> steps,
        StepCommitResult commit,
        List<GuardianViolation> warnings,
        PreparedWorkingCopy working,
        CancellationToken cancellationToken)
    {
        // FR-34: push after the commit (GitHub). A failed push doesn't undo the step: the commit is on the project branch,
        // the next step's push or the pull request job pushes it again, and GIT_PUSH_FAILED tells the users why.
        var published = await working.Provider.PublishBranchAsync(working.Connection, working.Branch, working.Token, cancellationToken);
        var completed = StepRules.Complete(step.Status);
        if (completed.IsFailure || !completed.Value.Changed)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (!published.Ok)
        {
            LogPushFailed(logger, step.StepNumber, published.Transient);
            db.AddEvent(
                EventTypes.GitPushFailed,
                projectId,
                new { stepId = step.Id, stepNumber = step.StepNumber, branch = working.Branch, reason = published.Reason, transient = published.Transient },
                SystemActor.Name,
                now);
        }

        step.Status = StepStatus.Completed;
        step.CommitSha = commit.Sha;
        step.BranchName = working.Branch;
        step.FileManifest = CodeGeneration.Manifest(commit.Files.Select(f => new ProducedFile(f.Path, f.Content)), step.DeclaredFiles);
        step.Violations = warnings.Count == 0 ? null : warnings;
        step.CompletedAt = now;
        db.AddEvent(
            EventTypes.StepCompleted,
            projectId,
            new
            {
                stepId = step.Id,
                stepNumber = step.StepNumber,
                attempt = step.Attempt,
                branch = working.Branch,
                commitSha = commit.Sha,
                pushed = published.Ok,
                fileCount = step.FileManifest.Count,
                warningCount = warnings.Count,
            },
            SystemActor.Name,
            now);

        if (ExecutionRules.AllCompleted([.. steps.Select(s => s.Status)]))
        {
            db.AddEvent(
                EventTypes.ExecutionCompleted,
                projectId,
                new { planArtifactId = planId, stepCount = steps.Count, branch = working.Branch, commitSha = commit.Sha },
                SystemActor.Name,
                now);
            var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
            await completion.OnExecutionCompletedAsync(db, project, planId, now, cancellationToken);
            LogExecutionCompleted(logger, steps.Count);
        }

        await SaveStepAsync(cancellationToken);
        LogStepCompleted(logger, step.StepNumber, commit.Sha, warnings.Count);
    }

    private async Task RejectAsync(Guid projectId, ExecutionStep step, GuardianResult result, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var outcome = StepRules.OnRejected(step.Attempt);
        step.Status = outcome.NewStatus;
        step.Violations = result.Violations;
        db.AddEvent(
            EventTypes.StepRejected,
            projectId,
            new
            {
                stepId = step.Id,
                stepNumber = step.StepNumber,
                attempt = step.Attempt,
                retry = outcome.Retry,
                codes = result.Errors.Select(v => v.Code).Distinct(StringComparer.Ordinal).ToList(),
                errorCount = result.Errors.Count(),
                warningCount = result.Warnings.Count(),
            },
            SystemActor.Name,
            now);

        if (outcome.Escalate)
        {
            db.AddEvent(
                EventTypes.StepBlocked,
                projectId,
                new { stepId = step.Id, stepNumber = step.StepNumber, attempt = step.Attempt },
                SystemActor.Name,
                now);
            var escalationPending = await db.ApprovalGates.AnyAsync(
                g => g.ProjectId == projectId && g.Type == GateType.Escalation && g.Status == GateStatus.Pending,
                cancellationToken);
            if (!escalationPending)
            {
                // Read now: the run's token count changed the project row since the job started.
                var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
                writer.AddGate(
                    project,
                    GateType.Escalation,
                    null,
                    SystemActor.Name,
                    now,
                    $"Step {step.StepNumber.ToString(CultureInfo.InvariantCulture)} was rejected {step.Attempt.ToString(CultureInfo.InvariantCulture)} times and is blocked.",
                    step);
            }
        }

        await SaveStepAsync(cancellationToken);
        LogStepRejected(logger, step.StepNumber, step.Attempt, outcome.NewStatus, result.Errors.Count());
    }

    /// <summary>A concurrent change (rollback, cascade, a pause racing the project row) retries the job, which re-reads.</summary>
    private async Task SaveStepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw new JobFailedException($"The step collided with a concurrent change: {ex.GetType().Name}.", transient: true, ErrorCodes.Conflict);
        }
    }

    /// <summary>Git and working-copy failures retry the job (a lock or I/O hiccup); an unsafe working copy fails it.</summary>
    private static async Task<T?> Git<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (GitCommandException ex)
        {
            throw new JobFailedException(ex.Message, transient: true);
        }
        catch (WorkingCopyException ex)
        {
            throw new JobFailedException(ex.Message, transient: false);
        }
        catch (IOException ex)
        {
            throw new JobFailedException($"Working copy I/O failed: {ex.GetType().Name}.", transient: true);
        }
    }

    private static ExecutionJobPayload Payload(JobExecution job)
    {
        try
        {
            return AideJson.Deserialize<ExecutionJobPayload>(job.Payload);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new JobFailedException($"The execution job payload can't be read: {ex.Message}", transient: false, ErrorCodes.Validation);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Execution job did nothing: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Step {StepNumber} started (attempt {Attempt})")]
    private static partial void LogStepStarted(ILogger logger, int stepNumber, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Step {StepNumber} completed as commit {Sha} ({WarningCount} warnings)")]
    private static partial void LogStepCompleted(ILogger logger, int stepNumber, string sha, int warningCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Step {StepNumber} attempt {Attempt} rejected by the Guardian with {ErrorCount} errors; now {Status}")]
    private static partial void LogStepRejected(ILogger logger, int stepNumber, int attempt, StepStatus status, int errorCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Step {StepNumber} is committed but the branch could not be pushed (transient {Transient}); it is pushed again later")]
    private static partial void LogPushFailed(ILogger logger, int stepNumber, bool transient);

    [LoggerMessage(Level = LogLevel.Information, Message = "The step's commit {Sha} already exists; completing the step from it")]
    private static partial void LogAlreadyCommitted(ILogger logger, string sha);

    [LoggerMessage(Level = LogLevel.Information, Message = "Execution completed: all {StepCount} steps of the current plan are completed")]
    private static partial void LogExecutionCompleted(ILogger logger, int stepCount);
}

/// <summary>
/// §4.9: when an execution job succeeds, queue the next one in the same transaction — unless the project left execution,
/// is paused (FR-37) or has nothing runnable (all completed, or a step is blocked).
/// </summary>
public sealed class ExecutionContinuationHook(JobEnqueuer jobs) : IJobSucceededHook
{
    public async Task OnJobSucceededAsync(AideDbContext db, JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        if (job.Type != JobTypes.Execution || job.ProjectId is not { } projectId)
        {
            return;
        }

        var project = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.CurrentPhase, p.ExecutionPaused })
            .FirstOrDefaultAsync(cancellationToken);
        if (project is null || project.CurrentPhase != Phase.Execution
            || await ExecutionQueries.CurrentPlanIdAsync(db, projectId, cancellationToken) is not { } planId)
        {
            return;
        }

        var steps = await ExecutionQueries.CurrentStepsAsync(db, planId, track: false, cancellationToken);
        if (ExecutionRules.ShouldContinue([.. steps.Select(ExecutionQueries.ToState)], project.ExecutionPaused))
        {
            await jobs.EnqueueAsync(JobTypes.Execution, new ExecutionJobPayload(projectId, ExecutionJobPayload.Continue), projectId, cancellationToken);
        }
    }
}

/// <summary>
/// An execution job that ends without succeeding (dead-lettered by the handler or the lease reaper, or failed permanently)
/// must not leave its step <c>in_progress</c>: that would stop execution and refuse every rollback (<c>ROLLBACK_BLOCKED</c>)
/// forever. The step's attempt is over, so it becomes <c>rejected</c> with its attempt count kept
/// (<see cref="StepRules.AbandonAttempt"/>) and a <c>STEP_REJECTED</c> event (<c>interrupted: true</c>); starting execution
/// again retries it. Nothing changes while another execution job of the project is still running (§4.9: one at a time).
/// </summary>
public sealed partial class ExecutionJobEndedHook(TimeProvider time, ILogger<ExecutionJobEndedHook> logger) : IJobEndedHook
{
    public async Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobType != JobTypes.Execution || job.ProjectId is not { } projectId)
        {
            return;
        }

        var otherRunning = await db.Jobs.AsNoTracking().AnyAsync(
            j => j.ProjectId == projectId && j.Type == JobTypes.Execution && j.Status == JobStatus.Running && j.Id != job.JobId,
            cancellationToken);
        if (otherRunning)
        {
            return;
        }

        var steps = await db.ExecutionSteps
            .Where(s => s.ProjectId == projectId && s.Status == StepStatus.InProgress)
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        foreach (var step in steps)
        {
            var change = StepRules.AbandonAttempt(step.Status);
            if (!change.Changed)
            {
                continue;
            }

            step.Status = change.NewStatus;
            db.AddEvent(
                EventTypes.StepRejected,
                projectId,
                new
                {
                    stepId = step.Id,
                    stepNumber = step.StepNumber,
                    attempt = step.Attempt,
                    retry = true,
                    interrupted = true,
                    deadLettered = job.DeadLettered,
                    codes = Array.Empty<string>(),
                    errorCount = 0,
                    warningCount = 0,
                },
                SystemActor.Name,
                now);
            LogAbandoned(logger, step.StepNumber, step.Attempt, job.JobId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Step {StepNumber} attempt {Attempt} was abandoned because execution job {JobId} ended; the step is rejected and will be retried")]
    private static partial void LogAbandoned(ILogger logger, int stepNumber, int attempt, Guid jobId);
}

public static class ExecutionJobServiceCollectionExtensions
{
    /// <summary>The execution runner (FR-31–FR-37): job handler, continuation and the endpoints' services (the completion hook is registered by <c>AddPullRequests</c>).</summary>
    public static IServiceCollection AddExecution(this IServiceCollection services)
    {
        services.AddScoped<WorkingCopyPreparer>();
        services.AddScoped<ExecutionService>();
        services.AddScoped<IJobSucceededHook, ExecutionContinuationHook>();
        services.AddScoped<IJobEndedHook, ExecutionJobEndedHook>();
        services.AddJobHandler<ExecutionJobHandler>(JobTypes.Execution);
        return services;
    }
}
