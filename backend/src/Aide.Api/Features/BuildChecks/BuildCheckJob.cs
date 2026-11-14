using Aide.Api.Features.Deployments;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Features.Review;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.BuildChecks;

/// <summary>
/// FR-255, §4.47: one poll of a build check. It reads the same sources review reads — the version's newest preview rollout
/// (<see cref="PreviewCollector"/>) and the PR head's checks with their required marks (<see cref="ExternalChecksCollector"/>)
/// — and lets <see cref="BuildCheckRules.Decide"/> rule:
/// <list type="bullet">
/// <item><c>waiting</c>: the sources are stored and the job defers itself to the re-check time (never sleeps), keeping when
/// the waits started in its payload. Results for an older SHA count as not there yet.</item>
/// <item><c>failed</c>: the failures (failure id, fingerprint, message, excerpt, error summary) are stored and the build-fix
/// job is queued (FR-256).</item>
/// <item><c>passed</c>, <c>skipped</c>, <c>exhausted</c>: the pull request job's advance to Review runs (expected from
/// execution) with no new rollout; review's collectors read the same rollout and checks.</item>
/// </list>
/// A project that left execution, or moved to another version, cancels the check. Redelivery is safe: a final or failed
/// check is left alone.
/// </summary>
public sealed partial class BuildCheckJobHandler(
    AideDbContext db,
    BuildCheckService buildChecks,
    JobEnqueuer jobs,
    PreviewCollector previews,
    ExternalChecksCollector externalChecks,
    RolloutScheduler rollouts,
    LifecycleService lifecycle,
    TimeProvider time,
    ILogger<BuildCheckJobHandler> logger) : IJobHandler
{
    public const string AdvanceReason = "Build check finished; pull request opened";

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = BuildCheckService.Payload<BuildCheckJobPayload>(job, "build check");
        var check = await db.BuildChecks.FirstOrDefaultAsync(c => c.Id == payload.BuildCheckId, cancellationToken);
        if (check is null || check.Status != BuildCheckStatus.Waiting || check.SupersededAt is not null)
        {
            LogSkipped(logger, "the check is gone or no longer waiting");
            return;
        }

        var now = time.GetUtcNow();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == check.ProjectId, cancellationToken);
        if (project is null || project.CurrentPhase != Phase.Execution
            || (project.CurrentReleaseVersionId ?? await db.CurrentReleaseVersionIdAsync(project.Id, cancellationToken)) != check.ReleaseVersionId)
        {
            // §4.47: execution was stopped (the project left execution) or the version changed.
            buildChecks.Complete(check, BuildCheckStatus.Cancelled, [], [], now);
            await SaveAsync(cancellationToken);
            return;
        }

        var (preview, rollout) = await PreviewAsync(project.Id, check.ReleaseVersionId, cancellationToken);
        var collected = await externalChecks.ReadAsync(project, excerpts: false, cancellationToken);
        var observation = new BuildCheckObservation(
            check.HeadSha,
            preview,
            collected is null ? null : new BuildCheckChecks(collected.HeadSha, collected.Checks))
        {
            PreviewWaitSince = payload.PreviewWaitSince,
            ChecksWaitSince = payload.ChecksWaitSince,
        };
        var decision = BuildCheckRules.Decide(observation, await buildChecks.FixesUsedAsync(check, cancellationToken), project.MaxBuildFixAttempts, now);
        if (decision.Wait)
        {
            check.Sources = decision.Sources;
            await SaveAsync(cancellationToken);
            LogWaiting(logger, project.Id, check.Id, check.Attempt);
            throw new JobDeferredException(
                "Waiting for the preview build and the required checks of the head commit (build check).",
                decision.RecheckAt!.Value,
                AideJson.Serialize(payload with { PreviewWaitSince = decision.PreviewWaitSince, ChecksWaitSince = decision.ChecksWaitSince }));
        }

        IReadOnlyList<BuildCheckFailure> failures = [];
        if (decision.Status is BuildCheckStatus.Failed or BuildCheckStatus.Exhausted)
        {
            // The failed checks' excerpts and logs are fetched once, now that the decision is final (not on every poll).
            if (collected is not null && collected.Checks.Any(c => c.Required && Domain.Releases.MergeReadinessRules.IsFailing(c)))
            {
                collected = await externalChecks.ReadAsync(project, excerpts: true, cancellationToken) ?? collected;
            }

            failures = await FailuresAsync(check.HeadSha, rollout, collected, decision, cancellationToken);
        }

        buildChecks.Complete(check, decision.Status, decision.Sources, failures, now);
        if (decision.PlanFix)
        {
            jobs.Enqueue(JobTypes.BuildFix, new BuildFixJobPayload(project.Id, check.Id), project.Id, now);
            await SaveAsync(cancellationToken);
            return;
        }

        await AdvanceAsync(project.Id, cancellationToken);
    }

    /// <summary>The PR job's advance to Review (expected from execution), committing the check with the transition.</summary>
    private async Task AdvanceAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var advanced = await lifecycle.AdvanceAsync(projectId, AdvanceReason, SystemActor.Name, cancellationToken, expectedFrom: Phase.Execution);
        if (advanced.IsSuccess)
        {
            return;
        }

        switch (advanced.Error!.Code)
        {
            case ErrorCodes.Conflict:
                throw new JobFailedException("The advance to review collided with a concurrent change.", transient: true, ErrorCodes.Conflict);
            default:
                // The check's result stands; the state machine refused (e.g. a precondition): keep the check.
                LogAdvanceRefused(logger, advanced.Error.Code, advanced.Error.Message);
                await SaveAsync(cancellationToken);
                return;
        }
    }

    /// <summary>
    /// The version's preview as the build check sees it: the newest rollout's commit, state and components (hosts without
    /// previews left out), or no rollout yet while one would have steps; <c>null</c> without a preview target.
    /// </summary>
    private async Task<(BuildCheckPreview? Preview, RolloutDto? Rollout)> PreviewAsync(Guid projectId, Guid versionId, CancellationToken cancellationToken)
    {
        if (await previews.NewestRolloutJobAsync(projectId, versionId, cancellationToken) is not { } job)
        {
            return await rollouts.HasStepsAsync(projectId, DeploymentEnvironment.Preview, cancellationToken)
                ? (new BuildCheckPreview(null, null, []), null)
                : (null, null);
        }

        var rollout = await previews.RolloutAsync(job, cancellationToken);
        var components = rollout.Steps
            .Where(s => s.Step != RolloutStepKind.Migrations && s.State != RolloutStepState.Skipped && s.Provider is not null)
            .Select(s => new BuildCheckPreviewComponent(RolloutRules.ComponentOf(s.Step), s.Provider!.Value, s.Status, s.Url))
            .ToList();
        return (new BuildCheckPreview(rollout.GitSha, rollout.State, components), rollout);
    }

    /// <summary>
    /// FR-256: the failures of the head's sources (preview findings with their build log tails, failed required checks with
    /// their excerpts) plus the timed-out sources; a failing source without a finding gets a plain failure.
    /// </summary>
    private async Task<IReadOnlyList<BuildCheckFailure>> FailuresAsync(
        string head, RolloutDto? rollout, CollectedChecks? checks, BuildCheckDecision decision, CancellationToken cancellationToken)
    {
        var inputs = new List<BuildCheckFailureInput>();
        if (rollout is not null && BuildCheckRules.SameSha(rollout.GitSha, head))
        {
            var outcomes = rollout.Steps
                .Where(s => s.Step != RolloutStepKind.Migrations && s.Status is { } && s.Provider is { })
                .Select(s => new RolloutComponentOutcome(RolloutRules.ComponentOf(s.Step), s.Provider!.Value, s.Status!.Value, s.ErrorCode, s.ErrorMessage, s.BuildLogTail, s.Url))
                .ToList();
            var logs = outcomes.ToDictionary(o => RolloutFindings.FailureId(o, outcomes), o => o.BuildLogTail, StringComparer.Ordinal);
            foreach (var finding in await previews.FindingsAsync(rollout, cancellationToken))
            {
                inputs.Add(new BuildCheckFailureInput(finding, finding.FailureId is { } id ? logs.GetValueOrDefault(id) : null));
            }
        }

        if (checks is not null && BuildCheckRules.SameSha(checks.HeadSha, head))
        {
            inputs.AddRange(checks.Findings().Select(f => new BuildCheckFailureInput(f, null)));
        }

        var failures = BuildCheckFailures.Collect(inputs, decision.TimedOut).ToList();
        if (failures.Count == 0)
        {
            // A failing source the collectors gave no finding for (e.g. a blocked rollout): name it, so a fix can be planned.
            foreach (var name in decision.Failing)
            {
                var preview = decision.Sources.Any(s => s.Name == name && s.Kind == BuildCheckSourceKind.Preview);
                var id = preview ? FailureIds.Of(FailureIds.Preview, "preview") : FailureIds.Of(FailureIds.Check, name);
                if (failures.All(f => f.FailureId != id))
                {
                    failures.Add(new BuildCheckFailure(id, id, $"{name} failed", preview ? FailureIds.Preview : FailureIds.Check, null));
                }
            }
        }

        return failures;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw new JobFailedException($"The build check collided with a concurrent change: {ex.GetType().Name}.", transient: true, ErrorCodes.Conflict);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Build check job did nothing: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Build check {BuildCheckId} of project {ProjectId} (attempt {Attempt}) waits for its sources")]
    private static partial void LogWaiting(ILogger logger, Guid projectId, Guid buildCheckId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The build check finished but the advance to review was refused ({ErrorCode}): {Reason}")]
    private static partial void LogAdvanceRefused(ILogger logger, string errorCode, string reason);
}

/// <summary>
/// A build-check or build-fix job that ended without succeeding must not leave the project waiting in execution forever:
/// its check ends <c>exhausted</c> (§4.47 "any → exhausted"), its unfinished fix steps are dropped and the pull request job
/// is queued, which advances to Review with the failures.
/// </summary>
public sealed class BuildCheckJobEndedHook(BuildCheckService buildChecks, TimeProvider time) : IJobEndedHook
{
    public async Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobType is not (JobTypes.BuildCheck or JobTypes.BuildFix) || job.ProjectId is not { } projectId)
        {
            return;
        }

        var payload = await db.Jobs.AsNoTracking().Where(j => j.Id == job.JobId).Select(j => j.Payload).FirstOrDefaultAsync(cancellationToken);
        Guid? checkId;
        try
        {
            checkId = payload is null ? null : AideJson.Deserialize<BuildFixJobPayload>(payload).BuildCheckId;
        }
        catch (System.Text.Json.JsonException)
        {
            checkId = null;
        }

        if (checkId is null || await db.BuildChecks.FirstOrDefaultAsync(c => c.Id == checkId && c.ProjectId == projectId, cancellationToken) is not { } check)
        {
            return;
        }

        var planId = await ExecutionQueries.CurrentPlanIdAsync(db, projectId, cancellationToken);
        await buildChecks.ExhaustAsync(check, planId, SystemActor.Name, time.GetUtcNow(), cancellationToken);
    }
}
