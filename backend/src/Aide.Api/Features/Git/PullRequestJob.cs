using System.Text.Json;
using Aide.Api.Agents;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Git;

/// <summary>Payload of a <see cref="JobTypes.PullRequest"/> job (FR-36): the project, the plan whose steps all completed, and what queued it.</summary>
public sealed record PullRequestJobPayload(Guid ProjectId, Guid PlanArtifactId, string Trigger)
{
    /// <summary>The last step of the plan completed (<see cref="GitCompletionHook"/>).</summary>
    public const string ExecutionCompleted = "execution_completed";

    /// <summary>A writer advanced execution → review by hand before the PR existed (<see cref="LifecycleService"/>).</summary>
    public const string PhaseAdvanced = "phase_advanced";

    /// <summary>A writer asked to open the PR again after the job failed (<c>POST /git/pull-request</c>).</summary>
    public const string Retried = "retried";

    /// <summary>The Git settings or token changed while no PR was recorded for the current plan.</summary>
    public const string SettingsChanged = "settings_changed";
}

/// <summary>Queues the pull request job in the caller's unit of work (outbox, §4.9); at most one queued or running per project.</summary>
public sealed class PullRequestJobs(AideDbContext db, JobEnqueuer jobs)
{
    public async Task EnqueueAsync(Guid projectId, Guid planArtifactId, string trigger, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await PendingAsync(projectId, cancellationToken))
        {
            await jobs.EnqueueAsync(JobTypes.PullRequest, new PullRequestJobPayload(projectId, planArtifactId, trigger), projectId, cancellationToken, now);
        }
    }

    /// <summary>
    /// FR-36: queues the job for the current plan when <see cref="PullRequestRules.CanRequest"/> allows it (execution with
    /// every step completed, or review; no PR recorded for the plan; no job queued or running). Nothing is saved here.
    /// </summary>
    public async Task<Result> RequestAsync(Project project, string trigger, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var planId = await ExecutionQueries.CurrentPlanIdAsync(db, project.Id, cancellationToken);
        var steps = planId is { } plan ? await ExecutionQueries.CurrentStepsAsync(db, plan, track: false, cancellationToken) : [];
        var recorded = planId is { } current && await PullRequestQueries.RecordedForPlanAsync(db, project.Id, current, cancellationToken);
        var facts = new PullRequestFacts(
            project.CurrentPhase,
            planId is not null,
            ExecutionRules.AllCompleted([.. steps.Select(s => s.Status)]),
            recorded,
            await PendingAsync(project.Id, cancellationToken));
        var verdict = PullRequestRules.CanRequest(facts);
        if (verdict.IsSuccess)
        {
            await jobs.EnqueueAsync(JobTypes.PullRequest, new PullRequestJobPayload(project.Id, planId!.Value, trigger), project.Id, cancellationToken, now);
        }

        return verdict;
    }

    private async Task<bool> PendingAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.ChangeTracker.Entries<Job>()
            .Any(e => e.State == EntityState.Added && e.Entity.Type == JobTypes.PullRequest && e.Entity.ProjectId == projectId)
        || await db.Jobs.AnyAsync(
            j => j.ProjectId == projectId && j.Type == JobTypes.PullRequest && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
            cancellationToken);
}

/// <summary>
/// FR-36: when the last step of the current plan completes (in the same unit of work as its <c>STEP_COMPLETED</c> and
/// <c>EXECUTION_COMPLETED</c>), queue the pull request job. Git and GitHub calls never run inside that transaction.
/// </summary>
public sealed class GitCompletionHook(PullRequestJobs pullRequests) : IExecutionCompletionHook
{
    public Task OnExecutionCompletedAsync(AideDbContext db, Project project, Guid planArtifactId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        return pullRequests.EnqueueAsync(project.Id, planArtifactId, PullRequestJobPayload.ExecutionCompleted, now, cancellationToken);
    }
}

/// <summary>
/// FR-36: the pull request job. Idempotent (§4.9); every step can be redelivered.
/// <list type="number">
/// <item>Skip unless the project is in execution or review, the payload's plan is still the current plan and all its steps
/// are completed; in review, skip if the PR for this plan is already recorded (and not forgotten since).</item>
/// <item>Prepare the working copy (GitHub: fetch) and publish the project branch (GitHub: push — this catches up any push
/// that failed after a step commit).</item>
/// <item>Open the PR (GitHub: reuse the open PR for the branch, else create it; Local: nothing to open) with a
/// deterministic title and description built from the spec, the plan and the step list
/// (<see cref="PullRequestDescription"/>), and compare the branch with the default branch.</item>
/// <item>Check again after the slow Git work: a rollback, a newer plan or reset steps in between mean the plan was
/// superseded, so nothing is recorded and the job ends skipped; changed Git settings retry the job against the new
/// repository. Then record the PR number and URL and (once per plan) <c>PULL_REQUEST_OPENED</c>, and — in execution —
/// advance to review through the lifecycle state machine (expected from execution), in one transaction guarded by the
/// project's <c>xmin</c>, with the review job (outbox). In review the PR is only recorded.</item>
/// </list>
/// Transient failures (network, 5xx, rate limits, a concurrent change) retry the job; permanent ones (bad token, rejected
/// push, GitHub validation) fail it, and <see cref="PullRequestJobEndedHook"/> records <c>PULL_REQUEST_FAILED</c>.
/// </summary>
public sealed partial class PullRequestJobHandler(
    AideDbContext db,
    WorkingCopyPreparer preparer,
    IWorkingCopies workingCopies,
    AgentJobInputs inputs,
    LifecycleService lifecycle,
    Deployments.DeploymentScheduler deployments,
    Microsoft.Extensions.Options.IOptions<Notifications.AppOptions> appOptions,
    TimeProvider time,
    ILogger<PullRequestJobHandler> logger) : IJobHandler
{
    /// <summary>Cap on the commits and files counted for the <c>PULL_REQUEST_OPENED</c> summary.</summary>
    public const int ComparisonLimit = 1000;

    public const string AdvanceReason = "All steps of the current plan are completed; pull request opened";

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = Payload(job);
        var before = await CheckAsync(payload, track: false, cancellationToken);
        if (before.Skip is { } skip)
        {
            LogSkipped(logger, skip);
            return;
        }

        var project = before.Project!;
        var steps = before.Steps!;
        if (project.CurrentPhase == Phase.Review && before.Recorded)
        {
            LogSkipped(logger, "the pull request for this plan is already recorded");
            return;
        }

        var preparation = await preparer.TryPrepareAsync(project.Id, cancellationToken);
        if (preparation.Result.IsFailure)
        {
            // No token, a bad repository URL, bad credentials, a missing repository or default branch are permanent.
            var error = preparation.Result.Error!;
            throw new JobFailedException(error.Message, preparation.Transient, error.Code);
        }

        var working = preparation.Result.Value;
        var published = await working.Provider.PublishBranchAsync(working.Connection, working.Branch, working.Token, cancellationToken);
        if (!published.Ok)
        {
            throw new JobFailedException($"The project branch could not be pushed: {published.Reason}", published.Transient, published.ErrorCode);
        }

        var input = await DescriptionInputAsync(project, payload.PlanArtifactId, steps, cancellationToken);
        var body = PullRequestDescription.Body(input);
        if (working.Provider.Kind is GitProvider.GithubApp or GitProvider.Gitlab)
        {
            // FR-82, FR-137: the App's PR and the GitLab merge request link back to the AIDE project.
            body += $"\n\n---\nOpened by [AIDE]({appOptions.Value.Absolute($"/projects/{project.Id}")}) for {project.Name}.";
        }

        var request = new PullRequestRequest(working.Branch, PullRequestDescription.Title(project.Name, steps.Count), body);
        var opened = await working.Provider.OpenPullRequestAsync(working.Connection, request, working.Token, cancellationToken);
        if (!opened.Ok)
        {
            throw new JobFailedException($"The pull request could not be opened: {opened.Reason}", opened.Transient, opened.ErrorCode);
        }

        var comparison = await CompareAsync(working, cancellationToken);
        await RecordAsync(payload, working, opened, comparison, cancellationToken);
    }

    /// <summary>
    /// Records the PR once the Git work is done — only if the plan is still current — and advances execution → review.
    /// The project row is loaded tracked and touched, so a rollback or settings change committed after this check makes
    /// the save fail with a conflict (the job retries and checks again) instead of recording a superseded PR.
    /// </summary>
    private async Task RecordAsync(
        PullRequestJobPayload payload, PreparedWorkingCopy working, PullRequestOutcome opened, BranchComparison? comparison, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var after = await CheckAsync(payload, track: true, cancellationToken);
        if (after.Skip is { } superseded)
        {
            // E.g. a rollback while the PR was being opened: nothing is recorded for the reset steps and nothing advances.
            LogSkipped(logger, superseded);
            return;
        }

        var project = after.Project!;
        project.UpdatedAt = now;
        var settings = await db.ProjectGitSettings.FirstAsync(s => s.ProjectId == project.Id, cancellationToken);
        if (settings.Provider != working.Provider.Kind
            || GitConnectionSettings.From(settings).RepoUrl != working.Connection.RepoUrl
            || settings.DefaultBranch != working.Connection.DefaultBranch)
        {
            throw new JobFailedException(
                "The Git settings changed while the pull request was being opened; retrying against the new repository.", transient: true, ErrorCodes.Conflict);
        }

        if (await db.CurrentReleaseVersionAsync(project.Id, cancellationToken) is { } version)
        {
            version.PullRequestNumber = opened.Number;
            version.PullRequestUrl = opened.Url;

            // FR-90: the PR opened or updated, so its head is deployed as a preview (when Vercel is set up); the review that
            // follows waits for it. Recorded in this transaction, built by the deployment job.
            var head = comparison?.HeadSha ?? await HeadAsync(project.Id, working.Branch, cancellationToken);
            if (head is not null)
            {
                await deployments.AutoPreviewAsync(project.Id, version.Id, working.Branch, head, now, cancellationToken);
            }
        }

        if (!after.Recorded)
        {
            db.AddEvent(
                EventTypes.PullRequestOpened,
                project.Id,
                new
                {
                    provider = working.Provider.Kind,
                    planArtifactId = payload.PlanArtifactId,
                    number = opened.Number,
                    url = opened.Url,
                    reused = opened.Reused,
                    baseBranch = working.Connection.DefaultBranch,
                    headBranch = working.Branch,
                    baseSha = comparison?.BaseSha,
                    headSha = comparison?.HeadSha,
                    commitCount = comparison?.Commits.Count,
                    fileCount = comparison?.Files.Count,
                    stepCount = after.Steps!.Count,
                },
                SystemActor.Name,
                now);
        }

        LogOpened(logger, working.Provider.Kind, opened.Number, opened.Reused);
        if (project.CurrentPhase != Phase.Execution)
        {
            // Review: the project is already there (a writer advanced by hand, or this is a retry); only record the PR.
            await SaveAsync(cancellationToken);
            return;
        }

        // The PR record, its event, the transition, PHASE_CHANGED and the review job commit together (§4.9).
        var advanced = await lifecycle.AdvanceAsync(project.Id, AdvanceReason, SystemActor.Name, cancellationToken, expectedFrom: Phase.Execution);
        if (advanced.IsSuccess)
        {
            return;
        }

        switch (advanced.Error!.Code)
        {
            case ErrorCodes.Conflict:
                throw new JobFailedException("The advance to review collided with a concurrent change.", transient: true, ErrorCodes.Conflict);
            case ErrorCodes.WrongPhase:
                db.ChangeTracker.Clear();
                LogSkipped(logger, "the project left execution while the pull request was being opened");
                return;
            default:
                // The plan is still current but the state machine refused (e.g. a precondition): keep the PR record.
                LogAdvanceRefused(logger, advanced.Error.Code, advanced.Error.Message);
                await SaveAsync(cancellationToken);
                return;
        }
    }

    /// <summary>The branch head in the working copy, or <c>null</c> when it can't be read.</summary>
    private async Task<string?> HeadAsync(Guid projectId, string branch, CancellationToken cancellationToken)
    {
        try
        {
            return await workingCopies.ResolveCommitAsync(projectId, $"refs/heads/{branch}", cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            return null;
        }
    }

    /// <summary>Whether the payload's plan is still the one to open the PR for: why not (<c>Skip</c>), or the facts.</summary>
    private async Task<Check> CheckAsync(PullRequestJobPayload payload, bool track, CancellationToken cancellationToken)
    {
        var projects = track ? db.Projects : db.Projects.AsNoTracking();
        var project = await projects.FirstOrDefaultAsync(p => p.Id == payload.ProjectId, cancellationToken);
        if (project is null || project.CurrentPhase is not (Phase.Execution or Phase.Review))
        {
            return Check.Skipped("the project is gone or not in execution or review");
        }

        var planId = await ExecutionQueries.CurrentPlanIdAsync(db, project.Id, cancellationToken);
        if (planId != payload.PlanArtifactId)
        {
            return Check.Skipped("the plan is no longer the current plan");
        }

        var steps = await ExecutionQueries.CurrentStepsAsync(db, payload.PlanArtifactId, track: false, cancellationToken);
        if (!ExecutionRules.AllCompleted([.. steps.Select(s => s.Status)]))
        {
            return Check.Skipped("not every step of the plan is completed");
        }

        var recorded = await PullRequestQueries.RecordedForPlanAsync(db, project.Id, payload.PlanArtifactId, cancellationToken);
        return new Check(project, steps, recorded, null);
    }

    private async Task<PullRequestInput> DescriptionInputAsync(Project project, Guid planId, List<ExecutionStep> steps, CancellationToken cancellationToken)
    {
        var spec = await inputs.CurrentAsync(project.Id, ArtifactType.TechnicalSpecification, cancellationToken);
        var plan = await db.Artifacts.AsNoTracking()
            .Where(a => a.Id == planId)
            .Select(a => new { a.Type, a.Version })
            .FirstAsync(cancellationToken);
        return new PullRequestInput(
            project.Id,
            project.Name,
            project.Checkpoint,
            spec?.Content,
            spec?.Version,
            plan.Type,
            plan.Version,
            [.. steps.Select(s => new PullRequestStep(
                s.StepNumber,
                s.Title,
                s.CommitSha,
                s.FileManifest?.Select(f => f.Path).ToList() ?? [.. s.DeclaredFiles.Select(f => f.Path)]))]);
    }

    private async Task<BranchComparison?> CompareAsync(PreparedWorkingCopy working, CancellationToken cancellationToken)
    {
        try
        {
            return await workingCopies.CompareAsync(
                working.Connection.ProjectId, working.Provider.ComparisonBaseRef(working.Connection), working.Branch, ComparisonLimit, ComparisonLimit, cancellationToken);
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException)
        {
            // The comparison only enriches the event; the PR itself is recorded either way.
            LogCompareFailed(logger, ex.GetType().Name);
            return null;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw new JobFailedException($"Recording the pull request collided with a concurrent change: {ex.GetType().Name}.", transient: true, ErrorCodes.Conflict);
        }
    }

    private static PullRequestJobPayload Payload(JobExecution job)
    {
        try
        {
            return AideJson.Deserialize<PullRequestJobPayload>(job.Payload);
        }
        catch (JsonException ex)
        {
            throw new JobFailedException($"The pull request job payload can't be read: {ex.Message}", transient: false, ErrorCodes.Validation);
        }
    }

    private sealed record Check(Project? Project, List<ExecutionStep>? Steps, bool Recorded, string? Skip)
    {
        public static Check Skipped(string reason) => new(null, null, false, reason);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pull request job did nothing: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pull request recorded ({Provider}, number {Number}, reused {Reused})")]
    private static partial void LogOpened(ILogger logger, GitProvider provider, int? number, bool reused);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The pull request is recorded but the advance to review was refused ({ErrorCode}): {Reason}")]
    private static partial void LogAdvanceRefused(ILogger logger, string errorCode, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Comparing the project branch with the default branch failed ({ErrorType})")]
    private static partial void LogCompareFailed(ILogger logger, string errorType);
}

/// <summary>A pull request job that ended without succeeding records <c>PULL_REQUEST_FAILED</c> with its last (token-free) error.</summary>
public sealed class PullRequestJobEndedHook(TimeProvider time) : IJobEndedHook
{
    public Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobType == JobTypes.PullRequest && job.ProjectId is { } projectId)
        {
            db.AddEvent(
                EventTypes.PullRequestFailed,
                projectId,
                new { jobId = job.JobId, deadLettered = job.DeadLettered, reason = job.Error },
                SystemActor.Name,
                time.GetUtcNow());
        }

        return Task.CompletedTask;
    }
}

/// <summary>Reads of the recorded project PR (FR-36).</summary>
public static class PullRequestQueries
{
    /// <summary>
    /// The latest <c>PULL_REQUEST_OPENED</c> event of the project, or <c>null</c> when there is none or a
    /// <c>PULL_REQUEST_FORGOTTEN</c> (the repository changed) came after it.
    /// </summary>
    public static async Task<AuditEvent?> LatestOpenedAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var latest = await db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && (e.Type == EventTypes.PullRequestOpened || e.Type == EventTypes.PullRequestForgotten))
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return latest?.Type == EventTypes.PullRequestOpened ? latest : null;
    }

    /// <summary>
    /// A <c>PULL_REQUEST_OPENED</c> event exists for <paramref name="planArtifactId"/> after the latest
    /// <c>PULL_REQUEST_FORGOTTEN</c>; a forget still pending in this unit of work counts too.
    /// </summary>
    public static async Task<bool> RecordedForPlanAsync(AideDbContext db, Guid projectId, Guid planArtifactId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.ChangeTracker.Entries<AuditEvent>().Any(e =>
            e.State == EntityState.Added && e.Entity.ProjectId == projectId && e.Entity.Type == EventTypes.PullRequestForgotten))
        {
            return false;
        }

        var forgottenAt = await db.Events.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Type == EventTypes.PullRequestForgotten)
            .MaxAsync(e => (DateTimeOffset?)e.CreatedAt, cancellationToken);
        var match = JsonSerializer.Serialize(new { planArtifactId });
        return await db.Events.AsNoTracking().AnyAsync(
            e => e.ProjectId == projectId && e.Type == EventTypes.PullRequestOpened && EF.Functions.JsonContains(e.Payload, match)
                && (forgottenAt == null || e.CreatedAt > forgottenAt),
            cancellationToken);
    }
}

public static class PullRequestServiceCollectionExtensions
{
    /// <summary>FR-36: the completion hook, the pull request job and its reader.</summary>
    public static IServiceCollection AddPullRequests(this IServiceCollection services)
    {
        services.AddScoped<PullRequestJobs>();
        services.AddScoped<IExecutionCompletionHook, GitCompletionHook>();
        services.AddScoped<IJobEndedHook, PullRequestJobEndedHook>();
        services.AddScoped<PullRequestReader>();
        services.AddJobHandler<PullRequestJobHandler>(JobTypes.PullRequest);
        return services;
    }
}
