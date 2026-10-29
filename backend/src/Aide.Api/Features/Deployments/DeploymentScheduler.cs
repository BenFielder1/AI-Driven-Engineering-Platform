using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>Payload of the <c>deployment</c> job (§4.15): the project and the deployment row it creates and polls.</summary>
public sealed record DeploymentJobPayload(Guid ProjectId, Guid DeploymentId);

/// <summary>A deployment to record: what to deploy, for which version (and release attempt), why and by whom.</summary>
public sealed record NewDeployment(
    Guid ProjectId,
    Guid ReleaseVersionId,
    Guid? ReleaseId,
    DeploymentEnvironment Target,
    string GitRef,
    string GitSha,
    DeploymentTrigger Trigger,
    Guid? TriggeredBy);

/// <summary>The deployment audit events (FR-90, FR-91): payloads carry ids, state and URL, never env values or logs.</summary>
public static class DeploymentEvents
{
    public static void Created(AideDbContext db, Deployment d, string? releaseVersion, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(d);
        db.AddEvent(
            EventTypes.DeploymentCreated,
            d.ProjectId,
            new
            {
                deploymentId = d.Id,
                target = d.Environment,
                provider = d.Provider,
                trigger = d.Trigger,
                status = d.Status,
                url = d.Url,
                gitRef = d.GitRef,
                gitSha = d.GitSha,
                releaseVersionId = d.ReleaseVersionId,
                releaseVersion,
                releaseId = d.ReleaseId,
                promotedFromDeploymentId = d.PromotedFromDeploymentId,
            },
            actor,
            now);
    }

    public static void Updated(AideDbContext db, Deployment d, string? releaseVersion, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(d);
        db.AddEvent(EventTypes.DeploymentUpdated, d.ProjectId, Facts(d, releaseVersion), actor, now);
    }

    /// <summary>FR-91: Owners are told (production failed, or the token was rejected).</summary>
    public static void Failed(AideDbContext db, Deployment d, string? releaseVersion, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(d);
        db.AddEvent(
            EventTypes.DeploymentFailed,
            d.ProjectId,
            new { deploymentId = d.Id, target = d.Environment, status = d.Status, url = d.Url, errorCode = d.ErrorCode, releaseVersion, gitSha = d.GitSha, releaseId = d.ReleaseId, deploymentFailed = true },
            actor,
            now);
    }

    /// <summary>FR-90: every member is told the preview is ready.</summary>
    public static void PreviewReady(AideDbContext db, Deployment d, string? releaseVersion, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(d);
        db.AddEvent(
            EventTypes.DeploymentPreviewReady,
            d.ProjectId,
            new { deploymentId = d.Id, target = d.Environment, status = d.Status, url = d.Url, releaseVersion, gitSha = d.GitSha, previewReady = true },
            actor,
            now);
    }

    private static object Facts(Deployment d, string? releaseVersion) =>
        new { deploymentId = d.Id, target = d.Environment, status = d.Status, url = d.Url, errorCode = d.ErrorCode, releaseVersion, gitSha = d.GitSha };
}

/// <summary>
/// FR-90, FR-91, §4.15: records deployments and queues their <c>deployment</c> job in the caller's unit of work (outbox):
/// <list type="bullet">
/// <item>a preview after execution opens or updates the PR, or on request; at most one preview is polled per project, so a
/// newer one stops the polling of older unfinished ones (they stay recorded, <c>canceled</c>);</item>
/// <item>the production deployment of a release once per attempt (unique by <c>(release_id, component)</c>), and redeploys as
/// new rows.</item>
/// </list>
/// Nothing is sent to the provider here: the job creates and polls. Without deployment settings (no bound connection or
/// Vercel project) the automatic deployments are skipped.
/// </summary>
public sealed class DeploymentScheduler(AideDbContext db, JobEnqueuer jobs, IDeploymentProviderResolver providers)
{
    /// <summary>Adds the queued row, its job and <c>DEPLOYMENT_CREATED</c> (the caller saves).</summary>
    public async Task<Deployment> AddAsync(NewDeployment spec, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        // FR-151: the row names the target (v2 flow: the project's frontend target) and its component.
        var targetId = await db.DeploymentTargets.AsNoTracking()
            .Where(t => t.ProjectId == spec.ProjectId && t.Component == DeploymentComponent.Frontend)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var deployment = new Deployment
        {
            ProjectId = spec.ProjectId,
            ReleaseVersionId = spec.ReleaseVersionId,
            ReleaseId = spec.ReleaseId,
            Provider = providers.Current.Kind,
            TargetId = targetId,
            Component = DeploymentComponent.Frontend,
            Kind = DeploymentTargetRules.KindOf(spec.Trigger),
            Environment = spec.Target,
            ProviderDeploymentId = string.Empty,
            GitRef = spec.GitRef,
            GitSha = spec.GitSha,
            Status = DeploymentStatus.Queued,
            Trigger = spec.Trigger,
            TriggeredBy = spec.TriggeredBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var version = await VersionTextAsync(db, spec.ReleaseVersionId, cancellationToken);
        if (spec.Target == DeploymentEnvironment.Preview)
        {
            await StopOlderPreviewsAsync(spec.ProjectId, version, actor, now, cancellationToken);
        }

        db.Deployments.Add(deployment);
        DeploymentEvents.Created(db, deployment, version, actor, now);
        jobs.Enqueue(JobTypes.Deployment, new DeploymentJobPayload(spec.ProjectId, deployment.Id), spec.ProjectId, now);
        return deployment;
    }

    /// <summary>FR-90: the automatic preview of the PR head after execution, when deployment is set up; else <c>null</c>.</summary>
    public async Task<Deployment?> AutoPreviewAsync(Guid projectId, Guid releaseVersionId, string branch, string sha, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await ConfiguredAsync(db, projectId, cancellationToken))
        {
            return null;
        }

        return await AddAsync(
            new NewDeployment(projectId, releaseVersionId, null, DeploymentEnvironment.Preview, branch, sha, DeploymentTrigger.Auto, null),
            SystemActor.Name,
            now,
            cancellationToken);
    }

    /// <summary>
    /// FR-91: the release's automatic production deployment of its merge commit, once per attempt, when deployment is set
    /// up; <c>null</c> otherwise (or when it exists already).
    /// </summary>
    public async Task<Deployment?> AutoProductionAsync(Domain.Entities.Release release, string defaultBranch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.MergeCommitSha is not { Length: > 0 } sha || !await ConfiguredAsync(db, release.ProjectId, cancellationToken))
        {
            return null;
        }

        var exists = db.ChangeTracker.Entries<Deployment>().Any(e => e.State == EntityState.Added && e.Entity.ReleaseId == release.Id && e.Entity.Environment == DeploymentEnvironment.Production)
            || await db.Deployments.AnyAsync(
                d => d.ReleaseId == release.Id && d.Environment == DeploymentEnvironment.Production && d.Trigger == DeploymentTrigger.Auto, cancellationToken);
        if (exists)
        {
            return null;
        }

        return await AddAsync(
            new NewDeployment(release.ProjectId, release.ReleaseVersionId, release.Id, DeploymentEnvironment.Production, defaultBranch, sha, DeploymentTrigger.Auto, null),
            SystemActor.Name,
            now,
            cancellationToken);
    }

    /// <summary>FR-88: the project can deploy: its frontend target has a bound connection and a linked Vercel project.</summary>
    public static Task<bool> ConfiguredAsync(AideDbContext db, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.DeploymentTargets.AsNoTracking()
            .AnyAsync(
                t => t.ProjectId == projectId && t.Component == DeploymentComponent.Frontend && t.ConnectionId != null && t.ResourceId != null,
                cancellationToken);
    }

    public static Task<string?> VersionTextAsync(AideDbContext db, Guid releaseVersionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.ReleaseVersions.AsNoTracking().Where(v => v.Id == releaseVersionId).Select(v => v.Version).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// §4.15: one preview poll per project. Older unfinished previews stop being polled: their queued jobs are cancelled and
    /// the rows become <c>canceled</c> with the reason (a running poll sees the newer row and stops).
    /// </summary>
    private async Task StopOlderPreviewsAsync(Guid projectId, string? version, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var older = await db.Deployments
            .Where(d => d.ProjectId == projectId && d.Environment == DeploymentEnvironment.Preview && (d.Status == DeploymentStatus.Queued || d.Status == DeploymentStatus.Building))
            .ToListAsync(cancellationToken);
        if (older.Count == 0)
        {
            return;
        }

        var ids = older.Select(d => d.Id).ToHashSet();
        var queued = await db.Jobs
            .Where(j => j.ProjectId == projectId && j.Type == JobTypes.Deployment && j.Status == JobStatus.Queued)
            .ToListAsync(cancellationToken);
        foreach (var job in queued)
        {
            if (TryPayload(job.Payload) is { } payload && ids.Contains(payload.DeploymentId))
            {
                job.Status = JobPolicy.Cancel(job.Status);
                job.CompletedAt = now;
            }
        }

        foreach (var deployment in older)
        {
            deployment.Status = DeploymentStatus.Canceled;
            deployment.ErrorMessage = DeploymentPollRules.SupersededMessage;
            deployment.UpdatedAt = now;
            DeploymentEvents.Updated(db, deployment, version, actor, now);
        }
    }

    public static DeploymentJobPayload? TryPayload(string payload)
    {
        try
        {
            return AideJson.Deserialize<DeploymentJobPayload>(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
