using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// FR-90, FR-91, §4.15, NFR-REL2: the <c>deployment</c> job of one deployment row. One job row per deployment that defers
/// itself between polls (a wait, never a blocking sleep or a counted attempt), so polling survives restarts:
/// <list type="number">
/// <item>A preview that a newer preview of the project replaced stops (<c>canceled</c>, still recorded).</item>
/// <item>Not created yet: a release's automatic production deployment first adopts the one Vercel made for the merge
/// commit (looking again for 30 seconds); otherwise pending env vars are pushed and the deployment is created
/// (<c>POST /v13/deployments</c> with <c>gitSource</c>).</item>
/// <item>Poll (<c>GET /v13/deployments/{id}</c>) every 10 seconds for 2 minutes, then every 30 seconds; after 30 minutes
/// <c>error</c> with <c>DEPLOYMENT_TIMEOUT</c>. <c>error</c> records the build-log tail.</item>
/// </list>
/// Every state change writes <c>DEPLOYMENT_UPDATED</c>; a ready preview <c>DEPLOYMENT_PREVIEW_READY</c> (members notified);
/// a failed production deployment <c>DEPLOYMENT_FAILED</c> (Owners notified; the release stands, D-V2-10). A Vercel rate
/// limit defers to its reset without counting an attempt; Vercel unreachable is a poll without news; a rejected token marks
/// the connection <c>invalid</c> and fails the deployment.
/// </summary>
public sealed partial class DeploymentJobHandler(
    AideDbContext db,
    DeploymentAccess access,
    DeploymentEnvSync envSync,
    TimeProvider time,
    ILogger<DeploymentJobHandler> logger) : IJobHandler
{
    /// <summary>The production alias Vercel reported for a ready production deployment (its project domain), if any.</summary>
    private string? _productionUrl;

    /// <summary>The deployment this run created at the provider (cancelled if its row was stopped meanwhile).</summary>
    private (string Id, DeploymentAuth Auth)? _created;

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (DeploymentScheduler.TryPayload(job.Payload) is not { } payload)
        {
            throw new JobFailedException("The deployment job payload can't be read.", transient: false, ErrorCodes.Validation);
        }

        var deployment = await db.Deployments.FirstOrDefaultAsync(d => d.Id == payload.DeploymentId && d.ProjectId == payload.ProjectId, cancellationToken);
        if (deployment is null || DeploymentStates.IsTerminal(deployment.Status))
        {
            // Stopped before its create was recorded (e.g. a newer preview won the race): cancel what an earlier attempt made.
            if (deployment is { Status: DeploymentStatus.Canceled, ProviderDeploymentId.Length: 0 })
            {
                await CancelOrphanAsync(deployment, cancellationToken);
            }

            LogSkipped(logger, "the deployment is gone or finished");
            return;
        }

        var now = time.GetUtcNow();
        var version = await DeploymentScheduler.VersionTextAsync(db, deployment.ReleaseVersionId, cancellationToken);
        var before = deployment.Status;

        if (deployment.Environment == DeploymentEnvironment.Preview
            && await db.Deployments.AnyAsync(
                d => d.ProjectId == deployment.ProjectId && d.Environment == DeploymentEnvironment.Preview && d.Id != deployment.Id && d.CreatedAt > deployment.CreatedAt,
                cancellationToken))
        {
            deployment.Status = DeploymentStatus.Canceled;
            deployment.ErrorMessage = DeploymentPollRules.SupersededMessage;
            await FinishAsync(deployment, before, version, now, cancellationToken);
            return;
        }

        var resolved = await access.ResolveAsync(deployment.ProjectId, cancellationToken);
        if (resolved.IsFailure)
        {
            Fail(deployment, resolved.Error!.Code, resolved.Error.Message);
            await FinishAsync(deployment, before, version, now, cancellationToken);
            return;
        }

        var context = resolved.Value;
        try
        {
            if (string.IsNullOrEmpty(deployment.ProviderDeploymentId))
            {
                if (await StartAsync(deployment, context, now, cancellationToken) is { } lookAgainAt)
                {
                    throw new JobDeferredException("Looking for Vercel's own production deployment of the merge commit.", lookAgainAt);
                }
            }
            else
            {
                Apply(deployment, await context.Provider.GetDeploymentAsync(context.Auth, deployment.ProviderDeploymentId, cancellationToken));
            }

            if (deployment.Status == DeploymentStatus.Error)
            {
                await RecordBuildLogAsync(deployment, context, cancellationToken);
            }
        }
        catch (DeploymentProviderException ex) when (ex.Failure == VercelFailure.RateLimited)
        {
            // NFR-REL2: back off until the reset without counting an attempt (nothing changed).
            var until = JobPolicy.DeferUntil(now, ex.RetryAt ?? now + TimeSpan.FromMinutes(1));
            throw new JobDeferredException($"Vercel's rate limit is reached; the deployment poll waits until {until:u}.", until);
        }
        catch (DeploymentProviderException ex) when (ex.Failure == VercelFailure.CredentialRejected)
        {
            await access.MarkInvalidAsync(context.Connection.Id, deployment.ProjectId, SystemActor.Name, now, cancellationToken);
            Fail(deployment, ErrorCodes.CredentialInvalid, "Vercel rejected the bound connection's token. Its owner needs to add a new token and an Owner to bind it.");
            await FinishAsync(deployment, before, version, now, cancellationToken, notifyOwners: true);
            return;
        }
        catch (DeploymentProviderException ex) when (ex.Failure == VercelFailure.Upstream)
        {
            // A failed request is a poll without news; the 30-minute ceiling still applies.
            LogUpstream(logger, deployment.Id, ex.Status);
        }
        catch (DeploymentProviderException ex)
        {
            LogRefused(logger, deployment.Id, ex.Failure, ex.Status);
            Fail(deployment, ErrorCodes.VercelError, SecretRedaction.SanitizeProviderError(DeploymentSettingsService.Message(ex)));
        }

        var decision = DeploymentPollRules.Decide(deployment.CreatedAt, now, deployment.Status);
        if (decision.Kind == DeploymentPollDecisionKind.TimedOut)
        {
            Fail(deployment, ErrorCodes.DeploymentTimeout, DeploymentPollRules.TimeoutMessage);
        }

        await FinishAsync(deployment, before, version, now, cancellationToken);
        if (decision.Kind == DeploymentPollDecisionKind.Wait)
        {
            throw new JobDeferredException($"Waiting for the deployment (next poll {decision.NextPollAt:u}).", decision.NextPollAt!.Value);
        }
    }

    /// <summary>Adopts or creates the provider's deployment; returns when to look again for Vercel's own production deployment.</summary>
    private async Task<DateTimeOffset?> StartAsync(Deployment deployment, DeploymentContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var vercelProjectId = context.Target.ResourceId!;
        if (ProductionDeploymentRules.Adopts(deployment.Environment, deployment.Trigger, context.Provider.Kind))
        {
            if (await context.Provider.FindProductionDeploymentAsync(context.Auth, vercelProjectId, deployment.GitSha, cancellationToken) is { } adopted)
            {
                deployment.ProviderDeploymentId = adopted.Id;
                Apply(deployment, adopted);
                LogAdopted(logger, deployment.Id);
                return null;
            }

            if (ProductionDeploymentRules.LookAgainAt(deployment.CreatedAt, now) is { } again)
            {
                return again;
            }
        }

        // §4.15 idempotency: an earlier attempt may have created it (its save failed, or the create timed out).
        if (await context.Provider.FindByAideIdAsync(context.Auth, vercelProjectId, deployment.GitSha, deployment.Environment, deployment.Id, cancellationToken) is { } earlier)
        {
            deployment.ProviderDeploymentId = earlier.Id;
            Apply(deployment, earlier);
            LogAdopted(logger, deployment.Id);
            return null;
        }

        await envSync.PushPendingAsync(context, cancellationToken);
        var created = await context.Provider.CreateDeploymentAsync(
            context.Auth,
            new CreateDeployment(
                deployment.Id,
                deployment.ProjectId,
                vercelProjectId,
                context.Target.Name ?? vercelProjectId,
                deployment.Environment,
                deployment.GitRef,
                deployment.GitSha,
                context.Repo.RepoId,
                context.Repo.Owner,
                context.Repo.Name),
            cancellationToken);
        deployment.ProviderDeploymentId = created.Id;
        _created = (created.Id, context.Auth);
        Apply(deployment, created);
        LogCreated(logger, deployment.Id, deployment.Environment);
        return null;
    }

    private void Apply(Deployment deployment, ProviderDeployment observed)
    {
        deployment.Url = observed.Url ?? deployment.Url;
        deployment.InspectorUrl = observed.InspectorUrl ?? deployment.InspectorUrl;
        var status = observed.Status;
        if (DeploymentStates.CanTransition(deployment.Status, status))
        {
            deployment.Status = status;
        }

        if (deployment.Status == DeploymentStatus.Error)
        {
            deployment.ErrorCode ??= ErrorCodes.VercelError;
            deployment.ErrorMessage ??= observed.ErrorMessage ?? "The Vercel build failed.";
        }

        if (deployment.Status == DeploymentStatus.Ready && observed.Alias is { } alias && deployment.Environment == DeploymentEnvironment.Production)
        {
            _productionUrl = alias;
        }
    }

    private async Task RecordBuildLogAsync(Deployment deployment, DeploymentContext context, CancellationToken cancellationToken)
    {
        if (deployment.BuildLogTail is not null || string.IsNullOrEmpty(deployment.ProviderDeploymentId))
        {
            return;
        }

        try
        {
            if (await context.Provider.GetBuildLogAsync(context.Auth, deployment.ProviderDeploymentId, cancellationToken) is { } log)
            {
                // NFR-SEC2: the tail is shown to members and is review evidence for the LLM, so the whole log is masked first
                // (env values from 4 characters with their encoded forms, the token, anything secret-shaped; lines kept)
                // and only then cut to the tail, so nothing straddling the cut survives.
                var masked = BuildLogMasking.MaskEnvValues(log, await EnvValuesAsync(deployment.ProjectId, cancellationToken));
                deployment.BuildLogTail = Deployment.TailOf(SecretScanner.Redact(SecretRedaction.Redact(masked, context.Auth.Token)));
            }
        }
        catch (DeploymentProviderException ex) when (ex.Failure is not VercelFailure.CredentialRejected)
        {
            LogNoBuildLog(logger, deployment.Id, ex.Failure);
        }
    }

    /// <summary>The project's env values (decrypted only to mask them in the build log).</summary>
    private async Task<IReadOnlyList<string>> EnvValuesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var encrypted = await db.DeploymentEnvVars.AsNoTracking().Where(v => v.ProjectId == projectId).Select(v => v.ValueEncrypted).ToListAsync(cancellationToken);
        return [.. encrypted.Select(e => access.Values.TryUnprotect(e, out var value) ? value : null).OfType<string>()];
    }

    private static void Fail(Deployment deployment, string errorCode, string message)
    {
        deployment.Status = DeploymentStatus.Error;
        deployment.ErrorCode = errorCode;
        deployment.ErrorMessage = message;
    }

    /// <summary>Records the row's change with its events and saves; a concurrent change retries the job.</summary>
    private async Task FinishAsync(Deployment deployment, DeploymentStatus before, string? version, DateTimeOffset now, CancellationToken cancellationToken, bool notifyOwners = false)
    {
        deployment.UpdatedAt = now;
        if (deployment.Status != before)
        {
            DeploymentEvents.Updated(db, deployment, version, SystemActor.Name, now);
            if (deployment.Status == DeploymentStatus.Ready)
            {
                deployment.ReadyAt = now;
                if (deployment.Environment == DeploymentEnvironment.Preview)
                {
                    DeploymentEvents.PreviewReady(db, deployment, version, SystemActor.Name, now);
                }
                else if (deployment.Trigger != DeploymentTrigger.Promote
                    && await db.DeploymentTargets.FirstOrDefaultAsync(
                        t => t.ProjectId == deployment.ProjectId && t.Component == deployment.Component, cancellationToken) is { } settings)
                {
                    settings.ProductionUrl = _productionUrl ?? deployment.Url;
                    settings.UpdatedAt = now;
                }
            }
            else if (deployment.Status == DeploymentStatus.Error && (notifyOwners || deployment.Environment == DeploymentEnvironment.Production))
            {
                DeploymentEvents.Failed(db, deployment, version, SystemActor.Name, now);
            }

            LogState(logger, deployment.Id, deployment.Environment, deployment.Status);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // E.g. a newer preview cancelled this one meanwhile. A deployment this run created is cancelled when its row was
            // stopped (no AIDE row will poll it); otherwise the retry finds it by its meta id and adopts it.
            if (_created is { } created
                && await db.Deployments.AsNoTracking().Where(d => d.Id == deployment.Id).Select(d => (DeploymentStatus?)d.Status).FirstOrDefaultAsync(cancellationToken) is var current
                && (current is null || DeploymentStates.IsTerminal(current.Value)))
            {
                await CancelQuietlyAsync(created.Auth, created.Id, cancellationToken);
            }

            throw new JobFailedException($"Recording the deployment collided with a concurrent change: {ex.GetType().Name}.", transient: true, ErrorCodes.Conflict);
        }
    }

    /// <summary>A stopped row whose create may have happened anyway: find it by the AIDE id and cancel it.</summary>
    private async Task CancelOrphanAsync(Deployment deployment, CancellationToken cancellationToken)
    {
        var resolved = await access.ResolveAsync(deployment.ProjectId, cancellationToken);
        if (resolved.IsFailure)
        {
            return;
        }

        var context = resolved.Value;
        try
        {
            if (await context.Provider.FindByAideIdAsync(context.Auth, context.Target.ResourceId!, deployment.GitSha, deployment.Environment, deployment.Id, cancellationToken) is { } orphan)
            {
                await CancelQuietlyAsync(context.Auth, orphan.Id, cancellationToken);
            }
        }
        catch (DeploymentProviderException ex)
        {
            LogCancelFailed(logger, deployment.Id, ex.Failure);
        }
    }

    private async Task CancelQuietlyAsync(DeploymentAuth auth, string providerDeploymentId, CancellationToken cancellationToken)
    {
        try
        {
            await access.Provider.CancelDeploymentAsync(auth, providerDeploymentId, cancellationToken);
            LogOrphanCancelled(logger, providerDeploymentId);
        }
        catch (DeploymentProviderException ex)
        {
            LogCancelFailed(logger, Guid.Empty, ex.Failure);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cancelled provider deployment {ProviderDeploymentId}: no AIDE deployment polls it")]
    private static partial void LogOrphanCancelled(ILogger logger, string providerDeploymentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deployment {DeploymentId}: cancelling an unrecorded provider deployment failed ({Failure})")]
    private static partial void LogCancelFailed(ILogger logger, Guid deploymentId, VercelFailure failure);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deployment job did nothing: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deployment {DeploymentId} ({Target}) created at the provider")]
    private static partial void LogCreated(ILogger logger, Guid deploymentId, DeploymentEnvironment target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deployment {DeploymentId} adopted Vercel's production deployment of the merge commit")]
    private static partial void LogAdopted(ILogger logger, Guid deploymentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deployment {DeploymentId} ({Target}) is {Status}")]
    private static partial void LogState(ILogger logger, Guid deploymentId, DeploymentEnvironment target, DeploymentStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deployment {DeploymentId}: Vercel failed with HTTP {Status}; polling again later")]
    private static partial void LogUpstream(ILogger logger, Guid deploymentId, int? status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deployment {DeploymentId}: Vercel refused it ({Failure}, HTTP {Status})")]
    private static partial void LogRefused(ILogger logger, Guid deploymentId, VercelFailure failure, int? status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deployment {DeploymentId}: the build log couldn't be read ({Failure})")]
    private static partial void LogNoBuildLog(ILogger logger, Guid deploymentId, VercelFailure failure);
}

/// <summary>
/// A deployment job that ended without succeeding (retries used up) fails its deployment with the job's last (token-free)
/// error, so it doesn't stay <c>building</c>; a production deployment notifies Owners.
/// </summary>
public sealed class DeploymentJobEndedHook(TimeProvider time) : IJobEndedHook
{
    public async Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobType != JobTypes.Deployment || job.ProjectId is not { } projectId)
        {
            return;
        }

        var payload = await db.Jobs.AsNoTracking().Where(j => j.Id == job.JobId).Select(j => j.Payload).FirstOrDefaultAsync(cancellationToken);
        if (payload is null || DeploymentScheduler.TryPayload(payload) is not { } parsed)
        {
            return;
        }

        var deployment = await db.Deployments.FirstOrDefaultAsync(d => d.Id == parsed.DeploymentId && d.ProjectId == projectId, cancellationToken);
        if (deployment is null || DeploymentStates.IsTerminal(deployment.Status))
        {
            return;
        }

        var now = time.GetUtcNow();
        deployment.Status = DeploymentStatus.Error;
        deployment.ErrorCode = ErrorCodes.VercelError;
        deployment.ErrorMessage = string.IsNullOrWhiteSpace(job.Error) ? "The deployment job failed." : SecretRedaction.SanitizeProviderError(job.Error);
        deployment.UpdatedAt = now;
        var version = await DeploymentScheduler.VersionTextAsync(db, deployment.ReleaseVersionId, cancellationToken);
        DeploymentEvents.Updated(db, deployment, version, SystemActor.Name, now);
        if (deployment.Environment == DeploymentEnvironment.Production)
        {
            DeploymentEvents.Failed(db, deployment, version, SystemActor.Name, now);
        }
    }
}
