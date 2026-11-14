using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.BuildChecks;

/// <summary>Payload of a <see cref="JobTypes.BuildCheck"/> job: the check, and when the preview and the checks started being
/// waited for (§4.47; kept across deferrals, <c>null</c> before the first wait).</summary>
public sealed record BuildCheckJobPayload(Guid ProjectId, Guid BuildCheckId, DateTimeOffset? PreviewWaitSince = null, DateTimeOffset? ChecksWaitSince = null);

/// <summary>Payload of a <see cref="JobTypes.BuildFix"/> job: the failed check to plan fix steps for (FR-256).</summary>
public sealed record BuildFixJobPayload(Guid ProjectId, Guid BuildCheckId);

/// <summary>What the pull request job does about the build check after execution (FR-254, FR-258, §4.47).</summary>
public enum BuildCheckGate
{
    /// <summary><c>maxBuildFixAttempts</c> is 0 (or the head is unknown): advance to Review as in 0.6.0.</summary>
    Off = 0,

    /// <summary>Queue the preview rollout of the head and start the next check.</summary>
    Start = 1,

    /// <summary>A check is waiting, or its fix is being planned: only record the pull request.</summary>
    Pending = 2,

    /// <summary>The cycle's latest check is exhausted, or passed or was skipped for this head: advance with no new rollout.</summary>
    Advance = 3,
}

/// <summary>
/// FR-254–FR-258, §4.47: the build check's writes, shared by the pull request job, the build-check and build-fix jobs, the
/// escalation <c>continue</c> and the cancellation paths. Every transition writes its audit event (<c>BUILD_CHECK_STARTED</c>,
/// <c>BUILD_CHECK_COMPLETED</c>, <c>BUILD_FIX_PLANNED</c>, each carrying the build check item for the live
/// <c>buildCheckUpdated</c> message) and a structured log entry. Nothing is saved here: the caller saves.
/// </summary>
public sealed partial class BuildCheckService(AideDbContext db, JobEnqueuer jobs, Git.PullRequestJobs pullRequests, ILogger<BuildCheckService> logger)
{
    /// <summary>The pull request job's trigger after an exhausted check whose fix steps were dropped (escalation <c>continue</c>).</summary>
    public const string ExhaustedTrigger = "build_check_exhausted";

    /// <summary>
    /// §4.47: what the pull request job does for <paramref name="headSha"/> of the version in the project's current review
    /// cycle (the attempt count resets each cycle). Only checks in force count: superseded ones (before a rollback or an
    /// invalidation of the version's work) and cancelled ones are ignored.
    /// </summary>
    public async Task<BuildCheckGate> GateAsync(Project project, Guid releaseVersionId, string? headSha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!BuildCheckRules.ShouldStart(project.MaxBuildFixAttempts) || string.IsNullOrWhiteSpace(headSha))
        {
            return BuildCheckGate.Off;
        }

        var latest = await InForce(releaseVersionId, project.ReviewCycle)
            .Where(c => c.Status != BuildCheckStatus.Cancelled)
            .OrderByDescending(c => c.Attempt)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
        {
            return BuildCheckGate.Start;
        }

        return latest.Status switch
        {
            BuildCheckStatus.Waiting => BuildCheckGate.Pending,
            BuildCheckStatus.Failed => await db.ExecutionSteps.AnyAsync(s => s.BuildCheckId == latest.Id, cancellationToken)
                ? BuildCheckGate.Start
                : BuildCheckGate.Pending,
            // Exhausted ends the cycle's checks whatever was committed after it (a fix step may have committed before
            // another was blocked); passed and skipped hold only for the commit they checked.
            BuildCheckStatus.Exhausted => BuildCheckGate.Advance,
            _ when BuildCheckRules.GoesToReview(latest.Status) && BuildCheckRules.SameSha(latest.HeadSha, headSha) => BuildCheckGate.Advance,
            _ => BuildCheckGate.Start,
        };
    }

    /// <summary>
    /// FR-254: starts the next check of the cycle for <paramref name="headSha"/> (attempt <see cref="BuildCheckRules.NextAttempt"/>,
    /// <c>waiting</c>) and queues its job. With nothing to wait for — no preview rollout queued and no checks host (a Local
    /// project) — it is decided at once (<c>skipped</c>, §4.47), without a job. Returns the check.
    /// </summary>
    public async Task<BuildCheck> StartAsync(
        Project project, Guid releaseVersionId, string headSha, bool previewQueued, bool checksHost, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var existing = await db.BuildChecks.AsNoTracking().Where(c => c.ReleaseVersionId == releaseVersionId).ToListAsync(cancellationToken);
        var check = new BuildCheck
        {
            ProjectId = project.Id,
            ReleaseVersionId = releaseVersionId,
            ReviewCycle = project.ReviewCycle,
            Attempt = BuildCheckRules.NextAttempt(existing, project.ReviewCycle),
            HeadSha = headSha.Trim(),
            Status = BuildCheckStatus.Waiting,
            StartedAt = now,
        };
        db.BuildChecks.Add(check);
        db.AddEvent(
            EventTypes.BuildCheckStarted,
            project.Id,
            new { buildCheckId = check.Id, releaseVersionId, reviewCycle = check.ReviewCycle, attempt = check.Attempt, headSha = check.HeadSha, check = BuildCheckReader.ToDto(check, []) },
            SystemActor.Name,
            now);
        LogTransition(logger, project.Id, releaseVersionId, check.ReviewCycle, check.Attempt, check.Id, check.Status, string.Empty);

        if (!previewQueued && !checksHost)
        {
            var fixesUsed = await FixesUsedAsync(check, cancellationToken);
            var decision = BuildCheckRules.Decide(new BuildCheckObservation(check.HeadSha, null, null), fixesUsed, project.MaxBuildFixAttempts, now);
            Complete(check, decision.Status, decision.Sources, [], now);
            return check;
        }

        jobs.Enqueue(JobTypes.BuildCheck, new BuildCheckJobPayload(project.Id, check.Id), project.Id, now);
        return check;
    }

    /// <summary>Ends a waiting check with <paramref name="status"/> (or a failed one as exhausted/cancelled) and writes <c>BUILD_CHECK_COMPLETED</c>.</summary>
    public void Complete(BuildCheck check, BuildCheckStatus status, IReadOnlyList<BuildCheckSource> sources, IReadOnlyList<BuildCheckFailure> failures, DateTimeOffset now, string actor = SystemActor.Name)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!BuildCheckRules.CanTransition(check.Status, status) || status == BuildCheckStatus.Waiting)
        {
            return;
        }

        check.Status = status;
        if (sources.Count > 0 || check.Sources.Count == 0)
        {
            check.Sources = sources;
        }

        if (failures.Count > 0)
        {
            check.Failures = failures;
        }

        check.CompletedAt ??= now;
        var failureIds = check.Failures.Select(f => f.FailureId).ToList();
        db.AddEvent(
            EventTypes.BuildCheckCompleted,
            check.ProjectId,
            new
            {
                buildCheckId = check.Id,
                releaseVersionId = check.ReleaseVersionId,
                reviewCycle = check.ReviewCycle,
                attempt = check.Attempt,
                status,
                failureIds,
                check = BuildCheckReader.ToDto(check, FixStepNumbers(check.Id)),
            },
            actor,
            now);
        var joined = string.Join(", ", failureIds);
        LogTransition(logger, check.ProjectId, check.ReleaseVersionId, check.ReviewCycle, check.Attempt, check.Id, status, joined);
    }

    /// <summary>FR-256: <c>BUILD_FIX_PLANNED</c> with the stored fix steps' numbers (added to the unit of work by the caller).</summary>
    public void FixPlanned(BuildCheck check, IReadOnlyList<int> stepNumbers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(check);
        db.AddEvent(
            EventTypes.BuildFixPlanned,
            check.ProjectId,
            new { buildCheckId = check.Id, releaseVersionId = check.ReleaseVersionId, reviewCycle = check.ReviewCycle, attempt = check.Attempt, stepNumbers, check = BuildCheckReader.ToDto(check, stepNumbers) },
            SystemActor.Name,
            now);
        var numbers = string.Join(", ", stepNumbers);
        LogFixPlanned(logger, check.ProjectId, check.ReleaseVersionId, check.ReviewCycle, check.Attempt, check.Id, numbers);
    }

    /// <summary>
    /// §4.47 last row: the version was invalidated or rolled back, or execution was stopped: the project's waiting and failed
    /// checks become <c>cancelled</c> (their queued jobs are cancelled with them).
    /// </summary>
    public async Task CancelOpenAsync(Guid projectId, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var open = await db.BuildChecks
            .Where(c => c.ProjectId == projectId && (c.Status == BuildCheckStatus.Waiting || c.Status == BuildCheckStatus.Failed))
            .ToListAsync(cancellationToken);
        foreach (var check in open)
        {
            Complete(check, BuildCheckRules.Cancel(check.Status), [], [], now, actor);
        }

        if (open.Count > 0)
        {
            var queued = await db.Jobs
                .Where(j => j.ProjectId == projectId && (j.Type == JobTypes.BuildCheck || j.Type == JobTypes.BuildFix) && j.Status == JobStatus.Queued)
                .ToListAsync(cancellationToken);
            foreach (var job in queued)
            {
                job.Status = JobPolicy.Cancel(job.Status);
                job.CompletedAt = now;
            }
        }
    }

    /// <summary>
    /// §4.47 "any → exhausted" (fix planning invalid after a retry, a blocked fix step answered "continue", or the check's job
    /// ended without succeeding): the check is exhausted, its fix steps that didn't complete are dropped (invalidated) so every
    /// remaining step is completed, and the pull request job is queued: it finds the exhausted check of the head and advances
    /// to Review without a new rollout. Returns whether the check changed.
    /// </summary>
    public async Task<bool> ExhaustAsync(BuildCheck check, Guid? planArtifactId, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        var next = BuildCheckRules.Exhaust(check.Status);
        if (next == check.Status)
        {
            return false;
        }

        var dropped = await db.ExecutionSteps
            .Where(s => s.BuildCheckId == check.Id && s.Status != StepStatus.Completed && s.Status != StepStatus.Invalidated)
            .ToListAsync(cancellationToken);
        foreach (var step in dropped)
        {
            step.Status = StepStatus.Invalidated;
        }

        Complete(check, next, [], [], now, actor);
        if (planArtifactId is { } planId)
        {
            await pullRequests.EnqueueAsync(check.ProjectId, planId, ExhaustedTrigger, now, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// §4.47, FR-258: the fixes used in <paramref name="check"/>'s cycle before it (<see cref="BuildCheckRules.FixesUsed"/>):
    /// the checks in force that fix steps were planned from.
    /// </summary>
    public async Task<int> FixesUsedAsync(BuildCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        var inCycle = await InForce(check.ReleaseVersionId, check.ReviewCycle).AsNoTracking().ToListAsync(cancellationToken);
        var ids = inCycle.Select(c => c.Id).ToList();
        var withSteps = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.BuildCheckId != null && ids.Contains(s.BuildCheckId.Value))
            .Select(s => s.BuildCheckId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        return BuildCheckRules.FixesUsed(inCycle, check.ReviewCycle, check.Id, withSteps.ToHashSet());
    }

    /// <summary>
    /// §4.47: a rollback or an invalidation of the version's work supersedes the project's checks in force (kept as history;
    /// the open ones are cancelled first, <see cref="CancelOpenAsync"/>), so the gate, the attempt numbers, the fix budget and
    /// the review prompt's note start again: re-execution gets attempt 0 and the whole budget.
    /// </summary>
    public async Task SupersedeAsync(Guid projectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var inForce = await db.BuildChecks.Where(c => c.ProjectId == projectId && c.SupersededAt == null).ToListAsync(cancellationToken);
        foreach (var check in inForce)
        {
            check.SupersededAt = now;
        }

        foreach (var added in db.ChangeTracker.Entries<BuildCheck>().Where(e => e.State == EntityState.Added && e.Entity.ProjectId == projectId))
        {
            added.Entity.SupersededAt ??= now;
        }
    }

    /// <summary>The version's checks of <paramref name="reviewCycle"/> that are in force (not superseded).</summary>
    public IQueryable<BuildCheck> InForce(Guid releaseVersionId, int reviewCycle) =>
        db.BuildChecks.Where(c => c.ReleaseVersionId == releaseVersionId && c.ReviewCycle == reviewCycle && c.SupersededAt == null);

    private List<int> FixStepNumbers(Guid checkId) =>
        [.. db.ChangeTracker.Entries<ExecutionStep>()
            .Where(e => e.Entity.BuildCheckId == checkId && e.Entity.Status != StepStatus.Invalidated)
            .Select(e => e.Entity.StepNumber)
            .Distinct()
            .Order()];

    /// <summary>Reads a job payload or fails the job permanently.</summary>
    internal static T Payload<T>(JobExecution job, string what)
    {
        try
        {
            return AideJson.Deserialize<T>(job.Payload);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new JobFailedException($"The {what} job payload can't be read: {ex.Message}", transient: false, ErrorCodes.Validation);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Build check {BuildCheckId} of project {ProjectId} version {ReleaseVersionId} cycle {ReviewCycle} attempt {Attempt}: {Status} (failures: {FailureIds})")]
    private static partial void LogTransition(ILogger logger, Guid projectId, Guid releaseVersionId, int reviewCycle, int attempt, Guid buildCheckId, BuildCheckStatus status, string failureIds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Build fix planned for check {BuildCheckId} of project {ProjectId} version {ReleaseVersionId} cycle {ReviewCycle} attempt {Attempt}: steps {StepNumbers}")]
    private static partial void LogFixPlanned(ILogger logger, Guid projectId, Guid releaseVersionId, int reviewCycle, int attempt, Guid buildCheckId, string stepNumbers);
}

public static class BuildCheckServiceCollectionExtensions
{
    /// <summary>FR-254–FR-258: the build check service, the build-check and build-fix jobs and their ended hook.</summary>
    public static IServiceCollection AddBuildChecks(this IServiceCollection services)
    {
        services.AddScoped<BuildCheckService>();
        services.AddScoped<IJobEndedHook, BuildCheckJobEndedHook>();
        services.AddJobHandler<BuildCheckJobHandler>(JobTypes.BuildCheck);
        services.AddJobHandler<BuildFixJobHandler>(JobTypes.BuildFix);
        return services;
    }
}
