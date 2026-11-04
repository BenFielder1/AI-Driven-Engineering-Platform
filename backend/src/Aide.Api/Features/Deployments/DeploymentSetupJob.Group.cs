using Aide.Api.Features.Databases;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// FR-190, §4.28: the <c>deployment_setup</c> job of a group: the platform project (stored as soon as it exists), then
/// backend, frontend and database, each with its own status. A part that fails doesn't undo the others, and every step is
/// idempotent, so a redelivery resumes: ready parts are skipped, and the stored project is reused (with none stored, an
/// existing name is refused). The database's platform work, polling and env sync stay in its
/// <c>database_provisioning</c> jobs (one per environment), which this job queues last.
/// </summary>
public sealed partial class DeploymentSetupJobHandler
{
    private async Task HandleGroupAsync(JobExecution job, DeploymentSetupPayload payload, CancellationToken cancellationToken)
    {
        var groupId = payload.GroupId!.Value;
        var group = await db.DeploymentGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.ProjectId == payload.ProjectId, cancellationToken);
        if (group is null || group.Status != DeploymentGroupStatus.Configuring || !await IsNewestGroupJobAsync(job.Id, payload, cancellationToken))
        {
            LogGroupSkipped(logger, groupId);
            return;
        }

        var now = time.GetUtcNow();
        var auth = await access.ConnectionAuthAsync(group.Provider, group.ConnectionId, cancellationToken);
        if (auth.IsFailure)
        {
            await FailGroupAsync(group, auth.Error!.Code, auth.Error.Message, cancellationToken);
        }

        if (DeploymentGrouping.Capability(group.Provider) == SharedProjectCapability.Project && group.SharedResourceId is null)
        {
            try
            {
                var created = await access.ProviderFor(group.Provider).CreateSharedProjectAsync(auth.Value, new SharedProjectRequest(group.Name, group.Region), cancellationToken);

                // Its own statement, uncancellable and free of the row's xmin: nothing after the create may lose this id.
                await db.DeploymentGroups.Where(g => g.Id == group.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.SharedResourceId, created.Id), CancellationToken.None);
                await db.Entry(group).ReloadAsync(CancellationToken.None);
            }
            catch (DeploymentProviderException ex) when (ex.Failure is DeploymentFailure.RateLimited or DeploymentFailure.Upstream)
            {
                throw Retryable(ex, now);
            }
            catch (DeploymentProviderException ex)
            {
                if (ex.Failure == DeploymentFailure.CredentialRejected)
                {
                    await access.MarkInvalidAsync(group.ConnectionId, group.ProjectId, SystemActor.Name, now, cancellationToken);
                }

                await FailGroupAsync(group, PartCode(ex), DeploymentSettingsService.Message(ex), cancellationToken);
            }
        }

        var failures = new List<(string Code, string Message)>();
        foreach (var part in new[] { DeploymentPart.Backend, DeploymentPart.Frontend })
        {
            var component = DeploymentGroupReader.ComponentOf(part);
            var target = await db.DeploymentTargets.FirstOrDefaultAsync(t => t.ProjectId == group.ProjectId && t.Component == component, cancellationToken);
            if (target is { Status: DeploymentTargetStatus.Configuring } && target.GroupId == group.Id
                && await SetUpPartAsync(job, group, payload, part, target, cancellationToken) is { } failure)
            {
                failures.Add(failure);
            }
        }

        await QueueDatabasesAsync(group, payload, time.GetUtcNow(), cancellationToken);
        await DeploymentGroups.RefreshAsync(db, group.ProjectId, group.Id, SystemActor.Name, time.GetUtcNow(), cancellationToken);
        LogGroupDone(logger, group.ProjectId, group.Provider, failures.Count);
        if (failures.Count > 0)
        {
            throw new JobFailedException(SecretRedaction.SanitizeProviderError(string.Join(" ", failures.Select(f => f.Message))), transient: false, failures[0].Code);
        }
    }

    /// <summary>Sets up one part; the failure (code, message) when the platform refuses it. A rate limit or an unreachable platform retries the job.</summary>
    private async Task<(string Code, string Message)?> SetUpPartAsync(
        JobExecution job, DeploymentGroup group, DeploymentSetupPayload payload, DeploymentPart part, DeploymentTarget target, CancellationToken cancellationToken)
    {
        var context = await access.ResolveAsync(target, cancellationToken, requireResource: false);
        if (context.IsFailure)
        {
            return await FailPartAsync(job.Id, payload, target, context.Error!.Code, context.Error.Message, cancellationToken);
        }

        var linkPart = part == DeploymentPart.Frontend ? payload.LinkParts?.Frontend : payload.LinkParts?.Backend;
        var linkId = linkPart is null ? null : DeploymentGroupRules.LinkId(group.Provider, group.SharedResourceId, linkPart);
        target.StatusReason = linkId is null ? DeploymentTargetService.StepCreating : DeploymentTargetService.StepLinking;
        await db.SaveChangesAsync(cancellationToken);
        var now = time.GetUtcNow();
        DeploymentSetupResult result;
        try
        {
            result = await context.Value.Provider.SetupTargetAsync(
                context.Value.Auth,
                new DeploymentSetupRequest(
                    context.Value.TargetRef, linkId is null, linkId is null ? DeploymentGroupRules.PartName(group.Name, part) : null, group.Region, group.Plan, linkId)
                {
                    SharedProjectId = group.SharedResourceId,
                },
                cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure is DeploymentFailure.RateLimited or DeploymentFailure.Upstream)
        {
            throw Retryable(ex, now);
        }
        catch (DeploymentProviderException ex)
        {
            if (ex.Failure == DeploymentFailure.CredentialRejected)
            {
                await access.MarkInvalidAsync(context.Value.Connection?.Id, target.ProjectId, SystemActor.Name, now, cancellationToken);
            }

            return await FailPartAsync(job.Id, payload, target, PartCode(ex), DeploymentSettingsService.Message(ex), cancellationToken);
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await Projects.ProjectLocks.LockProjectAsync(db, target.ProjectId, cancellationToken);
            await db.Entry(target).ReloadAsync(cancellationToken);
            if (db.Entry(target).State == EntityState.Detached || target.Status != DeploymentTargetStatus.Configuring || !await IsNewestGroupJobAsync(job.Id, payload, cancellationToken))
            {
                LogGroupSkipped(logger, group.Id);
                return null;
            }

            if (target.Provider == DeploymentProvider.Render)
            {
                target.BuildConfig = RenderRules.ConfigForPlan(target.BuildConfig, result.Plan ?? group.Plan);
            }

            if (result.PlacementUnavailable)
            {
                group.StatusReason = DeploymentGroupRules.PlacementUnavailable;
            }

            await ApplyResultAsync(target, result, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await SyncAfterSetupAsync(target, cancellationToken);
        return null;
    }

    /// <summary>A part's refusal: the target is <c>error</c> with the code, and the group carries on with its other parts.</summary>
    private async Task<(string Code, string Message)?> FailPartAsync(Guid jobId, DeploymentSetupPayload payload, DeploymentTarget target, string code, string message, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await Projects.ProjectLocks.LockProjectAsync(db, target.ProjectId, cancellationToken);
        await db.Entry(target).ReloadAsync(cancellationToken);
        if (db.Entry(target).State != EntityState.Detached && target.Status == DeploymentTargetStatus.Configuring && await IsNewestGroupJobAsync(jobId, payload, cancellationToken))
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
        LogFailed(logger, target.ProjectId, target.Component, code);
        return (code, $"{target.Component.ToWire()}: {message}");
    }

    /// <summary>
    /// The group's database rows waiting for a job get one each. Linking: Railway's <c>aide-preview</c> environment holds the
    /// preview instance of the same service; elsewhere the preview is a new instance, since it must never be production's (§4.21).
    /// </summary>
    private async Task QueueDatabasesAsync(DeploymentGroup group, DeploymentSetupPayload payload, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var production = DeploymentGroupRules.PartName(group.Name, DeploymentPart.Database);
        var link = payload.LinkParts?.Database;
        var rows = await db.ManagedDatabases.Where(d => d.GroupId == group.Id && d.Status == ManagedDatabaseStatus.Provisioning).ToListAsync(cancellationToken);
        foreach (var row in rows.OrderByDescending(d => d.Environment == DeploymentEnvironment.Production))
        {
            var connection = secrets.Connection(row);
            if (connection.JobId is not null)
            {
                continue;
            }

            var linkHere = link is not null && (row.Environment == DeploymentEnvironment.Production || (group.Provider == DeploymentProvider.Railway && !access.UsesFake));
            var name = row.Environment == DeploymentEnvironment.Preview ? ManagedDatabaseRules.RenderPreviewName(production) : production;
            var job = jobs.Enqueue(
                JobTypes.DatabaseProvisioning,
                new DatabaseProvisioningPayload(
                    row.ProjectId, row.Id, linkHere ? DatabaseSetupMode.Link : DatabaseSetupMode.Create, name, group.Region, null, linkHere ? link : null, DatabaseProvisioningPayload.SetupPhase),
                row.ProjectId,
                now);
            secrets.SetConnection(row, connection with { Name = name, JobId = job.Id });
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The group can't go on (no usable connection, or no project): every unfinished part fails with it, and the job fails for good.</summary>
    private async Task FailGroupAsync(DeploymentGroup group, string code, string message, CancellationToken cancellationToken)
    {
        var clean = SecretRedaction.SanitizeProviderError(message);
        var now = time.GetUtcNow();
        foreach (var target in await db.DeploymentTargets.Where(t => t.GroupId == group.Id && t.Status == DeploymentTargetStatus.Configuring).ToListAsync(cancellationToken))
        {
            target.Status = DeploymentTargetStatus.Error;
            target.StatusReason = code;
            target.UpdatedAt = now;
            DeploymentTargetEvents.Failed(db, target, DeploymentTargetFailureReasons.SetupFailed, code, SystemActor.Name, now);
        }

        await DeploymentGroups.FailUnqueuedDatabasesAsync(db, secrets, group.Id, code, clean, now, cancellationToken);
        group.StatusReason = code;
        await db.SaveChangesAsync(cancellationToken);
        await DeploymentGroups.RefreshAsync(db, group.ProjectId, group.Id, SystemActor.Name, now, cancellationToken);
        throw new JobFailedException(clean, transient: false, code);
    }

    private static string PartCode(DeploymentProviderException ex) =>
        ex.Failure == DeploymentFailure.Rejected ? ErrorCodes.Validation : VercelErrorRules.ErrorCodeFor(ex.Failure);

    private static Exception Retryable(DeploymentProviderException ex, DateTimeOffset now)
    {
        if (ex.Failure == DeploymentFailure.Upstream)
        {
            return new JobFailedException(DeploymentSettingsService.Message(ex), transient: true, ErrorCodes.UpstreamError);
        }

        var until = JobPolicy.DeferUntil(now, ex.RetryAt ?? now + TimeSpan.FromMinutes(1));
        return new JobDeferredException($"{ErrorCodes.PlatformRateLimited}: {DeploymentPlatforms.DisplayName(ex.Platform)} is rate limiting requests; setup waits until {until:u}.", until);
    }

    private async Task<bool> IsNewestGroupJobAsync(Guid jobId, DeploymentSetupPayload payload, CancellationToken cancellationToken)
    {
        var candidates = await db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == payload.ProjectId && j.Type == JobTypes.DeploymentSetup)
            .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        return candidates.FirstOrDefault(j => DeploymentTargetService.TryPayload(j.Payload)?.GroupId == payload.GroupId)?.Id == jobId;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deployment setup of group {GroupId} did nothing: it is gone, superseded or no longer configuring")]
    private static partial void LogGroupSkipped(ILogger logger, Guid groupId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: deployment group on {Provider} set up, {Failures} part(s) failed")]
    private static partial void LogGroupDone(ILogger logger, Guid projectId, DeploymentProvider provider, int failures);
}
