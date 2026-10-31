using Aide.Api.Features.Deployments;
using Aide.Api.Jobs;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Review;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Review;

/// <summary>The preview a review cycle used (FR-90, FR-147): the stored check and its deterministic findings.</summary>
public sealed record CollectedPreview(PreviewCheckDto Check, IReadOnlyList<ReviewFinding> Findings);

/// <summary>
/// FR-147 (replaces FR-90's per-run preview), §4.15 review waiting: before scoring, the version's newest preview rollout
/// (queued when execution opened or updated the PR, so of its head) is waited for until it finishes: the review job defers
/// itself every 10 seconds (not a blocking sleep, not a counted attempt; when waiting started is kept in the job payload) for
/// up to 30 minutes, then review proceeds with <c>timedOut</c>. No rollout (deployment not set up): no wait. Findings
/// (<see cref="RolloutFindings"/>): a failed build or pre-deploy is a critical <c>architecture</c> finding with the log tail,
/// a failed preview migration a critical <c>data_model</c> one, and Supabase advisor findings of the preview run are
/// <c>security</c> findings (ERROR major, WARN minor).
/// </summary>
public sealed partial class PreviewCollector(AideDbContext db, RolloutReader rollouts, TimeProvider time, ILogger<PreviewCollector> logger)
{
    /// <summary>The preview, or <c>null</c>; throws <see cref="JobDeferredException"/> while waiting for it.</summary>
    public async Task<CollectedPreview?> CollectAsync(Project project, LifecycleJobPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(payload);
        var versionId = project.CurrentReleaseVersionId ?? await db.CurrentReleaseVersionIdAsync(project.Id, cancellationToken);
        if (versionId is not { } id)
        {
            return null;
        }

        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == project.Id && j.Type == JobTypes.PreviewRollout && j.Status != JobStatus.Cancelled)
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Take(20)
            .ToListAsync(cancellationToken);
        var job = jobs.FirstOrDefault(j => RolloutScheduler.TryPayload(j.Payload)?.ReleaseVersionId == id);
        if (job is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var active = job.Status is JobStatus.Queued or JobStatus.Running;
        var decision = PreviewReviewRules.Decide(active ? DeploymentStatus.Building : DeploymentStatus.Ready, payload.PreviewWaitSince, now);
        if (decision.Wait)
        {
            LogWaiting(logger, project.Id, job.Id);
            throw new JobDeferredException(
                "Waiting for the preview rollout to finish before scoring (up to 30 minutes).",
                decision.RecheckAt!.Value,
                AideJson.Serialize(payload with { PreviewWaitSince = decision.WaitingSince }));
        }

        if (decision.TimedOut)
        {
            LogTimedOut(logger, project.Id, job.Id);
        }

        var rollout = await rollouts.ForJobAsync(job, cancellationToken);
        var waited = payload.PreviewWaitSince is { } since && !decision.TimedOut
            ? (int)Math.Max(0, Math.Round(((job.CompletedAt is { } done && done < now ? done : now) - since).TotalSeconds))
            : decision.WaitedSeconds;

        // The check names the frontend preview (else the backend); a rollout that deployed nothing names itself.
        var primary = rollout.Steps.LastOrDefault(s => s.DeploymentId is not null);
        var status = primary?.Status ?? (rollout.State == RolloutState.Running ? DeploymentStatus.Building : DeploymentStatus.Canceled);
        var check = new PreviewCheckDto(
            primary?.DeploymentId ?? job.Id, status, primary?.Url, primary?.BuildLogTail, waited, decision.TimedOut, rollout);
        return new CollectedPreview(check, await FindingsAsync(rollout, cancellationToken));
    }

    private async Task<IReadOnlyList<ReviewFinding>> FindingsAsync(RolloutDto rollout, CancellationToken cancellationToken)
    {
        var components = rollout.Steps
            .Where(s => s.Step != RolloutStepKind.Migrations && s.Status is { } && s.Provider is { })
            .Select(s => new RolloutComponentOutcome(RolloutRules.ComponentOf(s.Step), s.Provider!.Value, s.Status!.Value, s.ErrorCode, s.ErrorMessage, s.BuildLogTail, s.Url))
            .ToList();
        RolloutMigrationOutcome? migration = null;
        if (rollout.Steps.FirstOrDefault(s => s.Step == RolloutStepKind.Migrations)?.MigrationRunId is { } runId
            && await db.DatabaseMigrationRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken) is { } run)
        {
            migration = new RolloutMigrationOutcome(run.Status, run.ErrorCode, run.ErrorMessage, run.AdvisorFindings);
        }
        else if (rollout.State == RolloutState.Blocked)
        {
            // FR-148: blocked before anything ran (host mode, or a preview database that diverged).
            migration = new RolloutMigrationOutcome(MigrationRunStatus.Failed, rollout.ErrorCode, rollout.ErrorMessage, []);
        }

        return RolloutFindings.For(components, migration);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Review of project {ProjectId} waits for preview rollout {RolloutId}")]
    private static partial void LogWaiting(ILogger logger, Guid projectId, Guid rolloutId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review of project {ProjectId} stopped waiting for preview rollout {RolloutId} after 30 minutes")]
    private static partial void LogTimedOut(ILogger logger, Guid projectId, Guid rolloutId);
}
