using Aide.Api.Features.Deployments;
using Aide.Api.Jobs;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Review;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Review;

/// <summary>The preview a review cycle used (FR-90): the stored check and its deterministic finding (a failed build).</summary>
public sealed record CollectedPreview(PreviewCheckDto Check, IReadOnlyList<ReviewFinding> Findings);

/// <summary>
/// FR-90, §4.15 review waiting: before scoring, the latest preview of the version (recorded when execution opened or
/// updated the PR, so of its head) is waited for until it is <c>ready</c>, <c>error</c> or <c>canceled</c>: the review job
/// defers itself every 10 seconds (not a blocking sleep, not a counted attempt; when waiting started is kept in the job
/// payload) for up to 20 minutes, then review proceeds with <c>timedOut</c>. No preview (Vercel not configured): no wait.
/// <c>error</c> adds a critical <c>architecture</c> deterministic finding with the build-log tail as evidence.
/// </summary>
public sealed partial class PreviewCollector(AideDbContext db, TimeProvider time, ILogger<PreviewCollector> logger)
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

        var preview = await db.Deployments.AsNoTracking()
            .Where(d => d.ProjectId == project.Id && d.ReleaseVersionId == id && d.Environment == DeploymentEnvironment.Preview)
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (preview is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var decision = PreviewReviewRules.Decide(preview.Status, payload.PreviewWaitSince, now);
        if (decision.Wait)
        {
            LogWaiting(logger, project.Id, preview.Id, preview.Status);
            throw new JobDeferredException(
                "Waiting for the Vercel preview to finish before scoring (up to 20 minutes).",
                decision.RecheckAt!.Value,
                AideJson.Serialize(payload with { PreviewWaitSince = decision.WaitingSince }));
        }

        if (decision.TimedOut)
        {
            LogTimedOut(logger, project.Id, preview.Id);
        }

        // How long review waited for it: until it finished (a later checks wait doesn't count), or until it gave up.
        var waited = payload.PreviewWaitSince is { } since && !decision.TimedOut && DeploymentStates.IsTerminal(preview.Status)
            ? (int)Math.Max(0, Math.Round(((preview.UpdatedAt < now ? preview.UpdatedAt : now) - since).TotalSeconds))
            : decision.WaitedSeconds;
        var check = new PreviewCheckDto(preview.Id, preview.Status, preview.Url, preview.BuildLogTail, waited, decision.TimedOut);
        return new CollectedPreview(check, PreviewReviewRules.Findings(preview.Status, preview.BuildLogTail, preview.Url));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Review of project {ProjectId} waits for preview {DeploymentId} ({Status})")]
    private static partial void LogWaiting(ILogger logger, Guid projectId, Guid deploymentId, DeploymentStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review of project {ProjectId} stopped waiting for preview {DeploymentId} after 20 minutes")]
    private static partial void LogTimedOut(ILogger logger, Guid projectId, Guid deploymentId);
}
