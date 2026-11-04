using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// FR-144: the <c>deployment_setup</c> job of one target: creates or links the platform resource through
/// <see cref="IDeploymentProvider.SetupTargetAsync"/> (progress in <c>status_reason</c>: <c>creating</c>/<c>linking</c>,
/// then <c>syncing_env</c>), records the resource and URLs, recomputes the derived env vars (§4.22) and syncs them. A
/// refusal (Git not connected, plan limit, missing permission, not found, rejected token) makes the target <c>error</c>
/// with the error code as its reason and fails the job with the message; an unreachable platform retries; a rate limit
/// waits for its reset.
/// </summary>
public sealed partial class DeploymentSetupJobHandler(
    AideDbContext db,
    DeploymentAccess access,
    DeploymentInventoryService inventory,
    DeploymentEnvSync envSync,
    Databases.ManagedDatabaseService databases,
    Databases.ManagedDatabaseSecrets secrets,
    JobEnqueuer jobs,
    TimeProvider time,
    ILogger<DeploymentSetupJobHandler> logger) : IJobHandler
{
    private static readonly string[] ResourceChange = ["resource"];

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (DeploymentTargetService.TryPayload(job.Payload) is not { } payload)
        {
            throw new JobFailedException("The deployment setup job payload can't be read.", transient: false, ErrorCodes.Validation);
        }

        if (payload.GroupId is not null)
        {
            await HandleGroupAsync(job, payload, cancellationToken);
            return;
        }

        var target = await db.DeploymentTargets.FirstOrDefaultAsync(t => t.Id == payload.TargetId && t.ProjectId == payload.ProjectId, cancellationToken);
        if (target is null || target.Status != DeploymentTargetStatus.Configuring || !await IsNewestAsync(job.Id, payload, cancellationToken))
        {
            LogSkipped(logger, payload.TargetId);
            return;
        }

        var context = await access.ResolveAsync(target, cancellationToken, requireResource: false);
        if (context.IsFailure)
        {
            await FailAsync(job.Id, payload, target, context.Error!.Code, context.Error.Message, cancellationToken);
        }

        target.StatusReason = payload.Create ? DeploymentTargetService.StepCreating : DeploymentTargetService.StepLinking;
        await db.SaveChangesAsync(cancellationToken);

        // FR-191: a part set up in a group goes in the group's project; FR-192: alone, the name decides, and only a redelivery adopts.
        var sharedProject = target.GroupId is { } groupId
            ? await db.DeploymentGroups.AsNoTracking().Where(g => g.Id == groupId).Select(g => g.SharedResourceId).FirstOrDefaultAsync(cancellationToken)
            : null;
        var now = time.GetUtcNow();
        DeploymentSetupResult result;
        try
        {
            if (sharedProject is null && payload.Create && target.GroupId is null && target.Provider == DeploymentProvider.Railway)
            {
                sharedProject = await OwnProjectAsync(context.Value, target, payload.Name!, cancellationToken);
            }

            result = await context.Value.Provider.SetupTargetAsync(
                context.Value.Auth,
                new DeploymentSetupRequest(context.Value.TargetRef, payload.Create, payload.Name, payload.Region, payload.Plan, payload.ResourceId)
                {
                    SharedProjectId = sharedProject,
                },
                cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.RateLimited)
        {
            var until = JobPolicy.DeferUntil(now, ex.RetryAt ?? now + TimeSpan.FromMinutes(1));
            throw new JobDeferredException($"{ErrorCodes.PlatformRateLimited}: {DeploymentPlatforms.DisplayName(ex.Platform)} is rate limiting requests; setup waits until {until:u}.", until);
        }
        catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.Upstream)
        {
            throw new JobFailedException(DeploymentSettingsService.Message(ex), transient: true, ErrorCodes.UpstreamError);
        }
        catch (DeploymentProviderException ex)
        {
            if (ex.Failure == DeploymentFailure.CredentialRejected)
            {
                await access.MarkInvalidAsync(context.Value.Connection?.Id, target.ProjectId, SystemActor.Name, now, cancellationToken);
            }

            var code = ex.Failure == DeploymentFailure.Rejected ? ErrorCodes.Validation : VercelErrorRules.ErrorCodeFor(ex.Failure);
            await FailAsync(job.Id, payload, target, code, DeploymentSettingsService.Message(ex), cancellationToken);
            return;
        }

        // Only the target's newest setup request may apply its result: a later PUT (queued while this job called the
        // platform) wins, and this job ends without touching the target. Under the project lock, like the PUT.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await Projects.ProjectLocks.LockProjectAsync(db, target.ProjectId, cancellationToken);
            await db.Entry(target).ReloadAsync(cancellationToken);
            if (db.Entry(target).State == EntityState.Detached || target.Status != DeploymentTargetStatus.Configuring || !await IsNewestAsync(job.Id, payload, cancellationToken))
            {
                LogSkipped(logger, payload.TargetId);
                return;
            }

            // FR-154: a free Render service, created or linked, has no pre-deploy command, so the card doesn't show one.
            if (target.Provider == DeploymentProvider.Render)
            {
                target.BuildConfig = RenderRules.ConfigForPlan(target.BuildConfig, result.Plan ?? payload.Plan);
            }

            await ApplyResultAsync(target, result, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await SyncAfterSetupAsync(target, cancellationToken);
        await DeploymentGroups.RefreshAsync(db, target.ProjectId, target.GroupId, SystemActor.Name, time.GetUtcNow(), cancellationToken);
    }

    /// <summary>
    /// FR-192: a standalone create's project: the one this target already made under this name, else a new one (an existing
    /// name is refused). The id is written in its own statement as soon as it exists, whatever happens next: a rate limit,
    /// a restart or a refused service resumes from it.
    /// </summary>
    private async Task<string> OwnProjectAsync(DeploymentContext context, DeploymentTarget target, string name, CancellationToken cancellationToken)
    {
        if (target.PlatformProjectId is { } stored)
        {
            SharedProject? found;
            try
            {
                found = await context.Provider.GetSharedProjectAsync(context.Auth, stored, cancellationToken);
            }
            catch (DeploymentProviderException ex) when (ex.Failure is DeploymentFailure.NotFound or DeploymentFailure.Rejected or DeploymentFailure.Forbidden)
            {
                found = null;
            }

            if (found?.Name == name)
            {
                return stored;
            }
        }

        var created = await context.Provider.CreateSharedProjectAsync(context.Auth, new SharedProjectRequest(name, null), cancellationToken);
        await db.DeploymentTargets.Where(t => t.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.PlatformProjectId, created.Id), CancellationToken.None);
        await db.Entry(target).ReloadAsync(CancellationToken.None);
        return created.Id;
    }

    /// <summary>
    /// §4.22: derived values follow the targets' URLs; FR-145: the inventory is synced to the targets on change. Then the
    /// target is ready (saved), with its events.
    /// </summary>
    private async Task SyncAfterSetupAsync(DeploymentTarget target, CancellationToken cancellationToken)
    {
        var recomputed = await inventory.RecomputeDerivedAsync(target.ProjectId, await inventory.FactsAsync(target.ProjectId, cancellationToken), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await envSync.DeleteOnTargetsAsync(target.ProjectId, recomputed.Removed, cancellationToken);
        var pending = await db.DeploymentEnvVars.Where(v => v.ProjectId == target.ProjectId && (v.SyncedAt == null || v.SyncError != null)).ToListAsync(cancellationToken);
        await envSync.PushToTargetsAsync(target.ProjectId, [.. pending.Union(recomputed.Changed)], SystemActor.Name, cancellationToken);

        DeploymentTargetRules.Refresh(target);
        target.UpdatedAt = time.GetUtcNow();
        db.AddEvent(
            EventTypes.SettingsUpdated,
            target.ProjectId,
            new { projectId = target.ProjectId, section = "deployment", component = target.Component.ToWire(), changes = ResourceChange, resourceId = target.ResourceId },
            SystemActor.Name,
            target.UpdatedAt);
        DeploymentTargetEvents.Configured(db, target, SystemActor.Name, target.UpdatedAt);
        await db.SaveChangesAsync(cancellationToken);

        // FR-153: Supabase Auth URLs follow the frontend's URLs.
        if (target.Component == DeploymentComponent.Frontend)
        {
            await databases.RefreshAuthUrlsAsync(target.ProjectId, cancellationToken);
        }

        LogReady(logger, target.ProjectId, target.Component, target.Status);
    }

    /// <summary>Records the set-up resource and its URLs on the target (saved; the caller holds the project lock).</summary>
    private async Task ApplyResultAsync(DeploymentTarget target, DeploymentSetupResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (result.ResourceId != target.ResourceId)
        {
            await envSync.ResetAsync(target.ProjectId, target.Component, cancellationToken);
        }

        target.ResourceId = result.ResourceId;
        target.Name = result.Name;
        target.ProductionUrl = result.ProductionUrl ?? (target.ResourceId == result.ResourceId ? target.ProductionUrl : null);
        target.PreviewResourceId = result.PreviewResourceId;
        target.PreviewUrl = result.PreviewUrl;
        target.StatusReason = DeploymentTargetService.StepSyncingEnv;
        target.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Whether <paramref name="jobId"/> is the newest <c>deployment_setup</c> job of the payload's target.</summary>
    private async Task<bool> IsNewestAsync(Guid jobId, DeploymentSetupPayload payload, CancellationToken cancellationToken)
    {
        var candidates = await db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == payload.ProjectId && j.Type == JobTypes.DeploymentSetup)
            .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        return candidates.FirstOrDefault(j => DeploymentTargetService.TryPayload(j.Payload)?.TargetId == payload.TargetId)?.Id == jobId;
    }

    /// <summary>Records the failure on the target (saved) when this is its newest setup job, then fails the job for good with the message.</summary>
    private async Task FailAsync(Guid jobId, DeploymentSetupPayload payload, DeploymentTarget target, string code, string message, CancellationToken cancellationToken)
    {
        // A stale job (a newer setup request exists) fails without touching the target.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await Projects.ProjectLocks.LockProjectAsync(db, target.ProjectId, cancellationToken);
            await db.Entry(target).ReloadAsync(cancellationToken);
            if (db.Entry(target).State != EntityState.Detached && target.Status == DeploymentTargetStatus.Configuring && await IsNewestAsync(jobId, payload, cancellationToken))
            {
                target.Status = DeploymentTargetStatus.Error;
                target.StatusReason = code;
                target.UpdatedAt = time.GetUtcNow();
                DeploymentTargetEvents.Failed(
                    db,
                    target,
                    code == ErrorCodes.PlatformGitNotConnected ? DeploymentTargetFailureReasons.GitAccessLost : DeploymentTargetFailureReasons.SetupFailed,
                    code,
                    SystemActor.Name,
                    target.UpdatedAt);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await DeploymentGroups.RefreshAsync(db, target.ProjectId, target.GroupId, SystemActor.Name, time.GetUtcNow(), cancellationToken);
        LogFailed(logger, target.ProjectId, target.Component, code);
        throw new JobFailedException(SecretRedaction.SanitizeProviderError(message), transient: false, code);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deployment setup of target {TargetId} did nothing: it is gone or no longer configuring")]
    private static partial void LogSkipped(ILogger logger, Guid targetId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {Component} target set up ({Status})")]
    private static partial void LogReady(ILogger logger, Guid projectId, DeploymentComponent component, DeploymentTargetStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: {Component} target setup failed ({Code})")]
    private static partial void LogFailed(ILogger logger, Guid projectId, DeploymentComponent component, string code);
}

/// <summary>A setup job that ended without succeeding (retries used up) leaves its target <c>error</c> instead of <c>configuring</c>.</summary>
public sealed class DeploymentSetupJobEndedHook(Databases.ManagedDatabaseSecrets secrets, TimeProvider time) : IJobEndedHook
{
    public async Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobType != JobTypes.DeploymentSetup || job.ProjectId is not { } projectId)
        {
            return;
        }

        var payload = await db.Jobs.AsNoTracking().Where(j => j.Id == job.JobId).Select(j => j.Payload).FirstOrDefaultAsync(cancellationToken);
        if (payload is null || DeploymentTargetService.TryPayload(payload) is not { } parsed)
        {
            return;
        }

        if (parsed.GroupId is { } groupId)
        {
            await EndGroupAsync(db, job, projectId, groupId, cancellationToken);
            return;
        }

        var target = await db.DeploymentTargets.FirstOrDefaultAsync(t => t.Id == parsed.TargetId && t.ProjectId == projectId, cancellationToken);
        var newest = (await db.Jobs.AsNoTracking()
                .Where(j => j.ProjectId == projectId && j.Type == JobTypes.DeploymentSetup)
                .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
                .Take(50)
                .ToListAsync(cancellationToken))
            .FirstOrDefault(j => DeploymentTargetService.TryPayload(j.Payload)?.TargetId == parsed.TargetId)?.Id;
        if (target is { Status: DeploymentTargetStatus.Configuring } && newest == job.JobId)
        {
            target.Status = DeploymentTargetStatus.Error;
            target.StatusReason = ErrorCodes.UpstreamError;
            target.UpdatedAt = time.GetUtcNow();
            DeploymentTargetEvents.Failed(db, target, DeploymentTargetFailureReasons.SetupFailed, ErrorCodes.UpstreamError, SystemActor.Name, target.UpdatedAt);
            if (target.GroupId is { } partOf && await db.DeploymentGroups.FirstOrDefaultAsync(g => g.Id == partOf, cancellationToken) is { } group)
            {
                await DeploymentGroups.ApplyStatusAsync(db, group, SystemActor.Name, target.UpdatedAt, cancellationToken);
            }
        }
    }

    /// <summary>FR-190: the group's job gave up: unfinished parts fail, and queued databases carry on through their own jobs.</summary>
    private async Task EndGroupAsync(AideDbContext db, JobEnded job, Guid projectId, Guid groupId, CancellationToken cancellationToken)
    {
        var group = await db.DeploymentGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.ProjectId == projectId, cancellationToken);
        if (group is not { Status: DeploymentGroupStatus.Configuring })
        {
            return;
        }

        var newest = (await db.Jobs.AsNoTracking()
                .Where(j => j.ProjectId == projectId && j.Type == JobTypes.DeploymentSetup)
                .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
                .Take(50)
                .ToListAsync(cancellationToken))
            .FirstOrDefault(j => DeploymentTargetService.TryPayload(j.Payload)?.GroupId == groupId)?.Id;
        if (newest != job.JobId)
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var target in await db.DeploymentTargets.Where(t => t.GroupId == groupId && t.Status == DeploymentTargetStatus.Configuring).ToListAsync(cancellationToken))
        {
            target.Status = DeploymentTargetStatus.Error;
            target.StatusReason = ErrorCodes.UpstreamError;
            target.UpdatedAt = now;
            DeploymentTargetEvents.Failed(db, target, DeploymentTargetFailureReasons.SetupFailed, ErrorCodes.UpstreamError, SystemActor.Name, now);
        }

        await DeploymentGroups.FailUnqueuedDatabasesAsync(db, secrets, groupId, ErrorCodes.UpstreamError, job.Error ?? "The group's setup gave up.", now, cancellationToken);
        group.StatusReason = ErrorCodes.UpstreamError;
        await DeploymentGroups.ApplyStatusAsync(db, group, SystemActor.Name, now, cancellationToken);
    }
}
