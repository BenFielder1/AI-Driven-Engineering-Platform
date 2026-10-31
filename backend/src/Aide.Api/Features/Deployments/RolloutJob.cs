using Aide.Api.Features.Databases;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Deployments;

/// <summary>
/// FR-147, FR-149, §4.21: the <c>preview_rollout</c> and <c>production_rollout</c> jobs. Each run looks at the rollout's
/// steps (<see cref="RolloutRules.Plan"/>: Supabase BaaS migrations → backend → frontend) and does the next thing, deferring
/// itself while a step runs (a wait, never a blocking sleep or a counted attempt):
/// <list type="bullet">
/// <item>Migrations: preview runs AIDE's runner on the PR head (environment <c>preview</c>, only on a preview database
/// separate from production; a preview without its own database has no migration step). Production always runs the
/// runner again on the merge commit after the merge (idempotent: recorded versions are skipped, and only merged files
/// exist there), so commits pushed after the release job's pre-merge run (<c>releases.migration_run_id</c>, kept) are
/// migrated too. The post-merge run is linked to the release by its <c>release_id</c> and merge commit, and is the
/// rollout's migrations step. A failed production migration step writes <c>DEPLOYMENT_FAILED</c> (Owners are told, FR-91).</item>
/// <item>Backend and frontend: a deployment row with <c>rollout_id</c> = this job's id, polled by its <c>deployment</c> job
/// (production: adopts the platform's own build of the merge commit within 60 s, else creates one; an automatic row of the
/// release that already exists is adopted, so the rollout is idempotent per <c>(release_id, component)</c>). The frontend
/// starts only after the backend is <c>ready</c>, so it builds against the backend preview URL (env synced first).</item>
/// <item>A failure stops the later steps. A preview whose database diverged (FR-148) is blocked with
/// <c>PREVIEW_DATABASE_DIVERGED</c> before anything deploys.</item>
/// </list>
/// A preview rollout replaced by a newer one stops; one queued while a preview database reset is queued or running waits
/// for it (FR-148). A finished preview rollout writes <c>PREVIEW_ROLLOUT_COMPLETED</c>.
/// </summary>
public sealed partial class RolloutJobHandler(
    AideDbContext db,
    DeploymentScheduler deployments,
    RolloutScheduler rollouts,
    MigrationRunService migrations,
    TimeProvider time,
    ILogger<RolloutJobHandler> logger) : IJobHandler
{
    /// <summary>A migration run left <c>running</c> longer than this by a crashed attempt is failed and run again.</summary>
    public static readonly TimeSpan StaleRun = TimeSpan.FromMinutes(15);

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (RolloutScheduler.TryPayload(job.Payload) is not { } payload)
        {
            throw new JobFailedException("The rollout job payload can't be read.", transient: false, ErrorCodes.Validation);
        }

        var preview = payload.Environment == DeploymentEnvironment.Preview;
        if (preview && await SupersededAsync(job.Id, payload.ProjectId, cancellationToken))
        {
            LogSuperseded(logger, payload.ProjectId, job.Id);
            return;
        }

        var now = time.GetUtcNow();
        if (preview && await rollouts.PreviewResetPendingAsync(payload.ProjectId, cancellationToken))
        {
            throw new JobDeferredException("Waiting for the preview database reset to finish.", now + RolloutRules.RecheckInterval);
        }

        var plan = await PlanAsync(payload, cancellationToken);
        var release = payload.ReleaseId is { } releaseId ? await db.Releases.FirstOrDefaultAsync(r => r.Id == releaseId, cancellationToken) : null;

        // FR-148: before anything deploys, a preview whose database has migrations the branch no longer has is blocked.
        if (preview && !await StartedAsync(job.Id, payload, cancellationToken))
        {
            var missing = await rollouts.DivergedAsync(payload.ProjectId, payload.GitSha, cancellationToken);
            if (missing.Count > 0)
            {
                await CompleteAsync(job.Id, payload, RolloutState.Blocked, ErrorCodes.PreviewDatabaseDiverged, PreviewDatabaseRules.Describe(missing), cancellationToken);
                return;
            }
        }

        for (var round = 0; round < plan.Count + 1; round++)
        {
            var states = await StatesAsync(job.Id, payload, plan, now, cancellationToken);
            var decision = RolloutRules.Next(plan, states);
            switch (decision.Kind)
            {
                case RolloutDecisionKind.Completed:
                    await CompleteAsync(job.Id, payload, RolloutState.Succeeded, null, null, cancellationToken);
                    return;
                case RolloutDecisionKind.Failed:
                    var (code, message) = await FailureAsync(job.Id, payload, decision.Step!.Value, cancellationToken);
                    var blocked = preview && code is ErrorCodes.MigrationHistoryDiverged or ErrorCodes.PreviewDatabaseDiverged;
                    await CompleteAsync(job.Id, payload, blocked ? RolloutState.Blocked : RolloutState.Failed, blocked ? ErrorCodes.PreviewDatabaseDiverged : code, message, cancellationToken);
                    if (!preview && decision.Step == RolloutStepKind.Migrations)
                    {
                        await ProductionMigrationFailedAsync(payload, code, cancellationToken);
                    }

                    return;
                case RolloutDecisionKind.Wait when now - payload.RequestedAt > RolloutRules.Ceiling:
                    await CompleteAsync(job.Id, payload, RolloutState.Failed, ErrorCodes.DeploymentTimeout, "The rollout didn't finish in time.", cancellationToken);
                    return;
                case RolloutDecisionKind.Wait:
                    throw new JobDeferredException($"Waiting for the {decision.Step!.Value.ToWire()} step of the rollout.", now + RolloutRules.RecheckInterval);
                case RolloutDecisionKind.Start when decision.Step == RolloutStepKind.Migrations:
                    await MigrateAsync(payload, release, cancellationToken);
                    continue;
                default:
                    await DeployAsync(job.Id, payload, RolloutRules.ComponentOf(decision.Step!.Value), now, cancellationToken);
                    throw new JobDeferredException($"Started the {decision.Step!.Value.ToWire()} step of the rollout.", now + RolloutRules.RecheckInterval);
            }
        }

        throw new JobDeferredException("The rollout looks again shortly.", now + RolloutRules.RecheckInterval);
    }

    private async Task<bool> SupersededAsync(Guid jobId, Guid projectId, CancellationToken cancellationToken)
    {
        var createdAt = await db.Jobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => (DateTimeOffset?)j.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return createdAt is { } at && await db.Jobs.AsNoTracking().AnyAsync(
            j => j.ProjectId == projectId && j.Type == JobTypes.PreviewRollout && j.Id != jobId && j.CreatedAt > at && j.Status != JobStatus.Cancelled,
            cancellationToken);
    }

    /// <summary>The rollout's steps from the project's targets and whether it migrates a Supabase BaaS database.</summary>
    private async Task<IReadOnlyList<RolloutStepPlan>> PlanAsync(RolloutJobPayload payload, CancellationToken cancellationToken)
    {
        var targets = await db.DeploymentTargets.AsNoTracking().Where(t => t.ProjectId == payload.ProjectId).ToListAsync(cancellationToken);
        var baas = await migrations.HasMigrationStepAsync(payload.ProjectId, payload.Environment, cancellationToken);
        return RolloutRules.Plan(
            payload.Environment,
            baas,
            [.. targets.Select(t => new RolloutTarget(t.Component, t.Provider, t.ResourceId is not null && (t.ConnectionId is not null || t.Provider == DeploymentProvider.GithubPages)))]);
    }

    private async Task<bool> StartedAsync(Guid jobId, RolloutJobPayload payload, CancellationToken cancellationToken) =>
        await db.Deployments.AsNoTracking().AnyAsync(d => d.RolloutId == jobId, cancellationToken)
        || await RunQuery(payload).AnyAsync(cancellationToken);

    /// <summary>The rollout's own migration runs: its environment, release and commit, started after it was requested.</summary>
    private IQueryable<DatabaseMigrationRun> RunQuery(RolloutJobPayload payload) =>
        db.DatabaseMigrationRuns.Where(r => r.ProjectId == payload.ProjectId && r.Environment == payload.Environment && r.ReleaseId == payload.ReleaseId
            && r.GitSha == payload.GitSha && r.StartedAt >= payload.RequestedAt);

    /// <summary>
    /// The rollout's migration run: the newest it started itself (production too: the release's pre-merge run never
    /// stands in for the post-merge run on the merge commit).
    /// </summary>
    private Task<DatabaseMigrationRun?> RunAsync(RolloutJobPayload payload, CancellationToken cancellationToken) =>
        RunQuery(payload).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(cancellationToken);

    private async Task<Dictionary<RolloutStepKind, RolloutStepState>> StatesAsync(
        Guid jobId, RolloutJobPayload payload, IReadOnlyList<RolloutStepPlan> plan, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var states = new Dictionary<RolloutStepKind, RolloutStepState>();
        if (plan.Any(s => s.Kind == RolloutStepKind.Migrations) && await RunAsync(payload, cancellationToken) is { } run)
        {
            if (run.Status == MigrationRunStatus.Running && now - run.StartedAt > StaleRun)
            {
                // An attempt crashed mid-run: the runner resumes after the last committed file, so run again.
                run.Status = MigrationRunStatus.Failed;
                run.ErrorCode = ErrorCodes.MigrationFailed;
                run.ErrorMessage = "The migration run was interrupted; AIDE runs it again.";
                run.FinishedAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                states[RolloutStepKind.Migrations] = RolloutRules.StateOf(run.Status);
            }
        }

        foreach (var component in new[] { DeploymentComponent.Backend, DeploymentComponent.Frontend })
        {
            if (await RowAsync(jobId, payload, component, cancellationToken) is { } row)
            {
                states[component == DeploymentComponent.Backend ? RolloutStepKind.Backend : RolloutStepKind.Frontend] = RolloutRules.StateOf(row.Status);
            }
        }

        return states;
    }

    /// <summary>The rollout's newest row of the component; production also adopts the release's automatic row (idempotent).</summary>
    private async Task<Deployment?> RowAsync(Guid jobId, RolloutJobPayload payload, DeploymentComponent component, CancellationToken cancellationToken)
    {
        var row = await db.Deployments
            .Where(d => d.RolloutId == jobId && d.Component == component)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (row is not null || payload.ReleaseId is not { } releaseId)
        {
            return row;
        }

        row = await db.Deployments.FirstOrDefaultAsync(
            d => d.ReleaseId == releaseId && d.Component == component && d.Environment == DeploymentEnvironment.Production && d.Trigger == DeploymentTrigger.Auto,
            cancellationToken);
        if (row is { RolloutId: null })
        {
            row.RolloutId = jobId;
            await db.SaveChangesAsync(cancellationToken);
        }

        return row;
    }

    private async Task MigrateAsync(RolloutJobPayload payload, Domain.Entities.Release? release, CancellationToken cancellationToken)
    {
        try
        {
            var run = await migrations.RunAsync(payload.ProjectId, payload.Environment, payload.ReleaseVersionId, payload.ReleaseId, payload.GitSha, cancellationToken);

            // The release keeps the pre-merge run that gated its merge; one merged without it records this run.
            if (release is { MigrationRunId: null } && run.Status == MigrationRunStatus.Succeeded)
            {
                release.MigrationRunId = run.Id;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (MigrationTargetMissingException ex)
        {
            // The database went away between planning and running: the next round plans without the step.
            LogNoDatabase(logger, payload.ProjectId, ex.Message);
        }
    }

    private async Task DeployAsync(Guid jobId, RolloutJobPayload payload, DeploymentComponent component, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await deployments.AddAsync(
            new NewDeployment(
                payload.ProjectId, payload.ReleaseVersionId, payload.ReleaseId, payload.Environment, payload.GitRef, payload.GitSha, payload.Trigger, payload.TriggeredBy, component, jobId),
            payload.TriggeredBy?.ToString() ?? SystemActor.Name,
            now,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        LogStep(logger, payload.ProjectId, payload.Environment, component);
    }

    private async Task<(string? Code, string? Message)> FailureAsync(
        Guid jobId, RolloutJobPayload payload, RolloutStepKind step, CancellationToken cancellationToken)
    {
        if (step == RolloutStepKind.Migrations)
        {
            var run = await RunAsync(payload, cancellationToken);
            return (run?.ErrorCode ?? ErrorCodes.MigrationFailed, run?.ErrorMessage);
        }

        var row = await RowAsync(jobId, payload, RolloutRules.ComponentOf(step), cancellationToken);
        return (row?.ErrorCode ?? ErrorCodes.PlatformError, row?.ErrorMessage);
    }

    /// <summary>A finished preview rollout writes <c>PREVIEW_ROLLOUT_COMPLETED</c> (with its state); production writes nothing more.</summary>
    private async Task CompleteAsync(Guid jobId, RolloutJobPayload payload, RolloutState state, string? errorCode, string? message, CancellationToken cancellationToken)
    {
        LogCompleted(logger, payload.ProjectId, payload.Environment, state, errorCode);
        if (payload.Environment != DeploymentEnvironment.Preview)
        {
            return;
        }

        var components = await db.Deployments.AsNoTracking()
            .Where(d => d.RolloutId == jobId)
            .OrderBy(d => d.CreatedAt)
            .Select(d => new { component = d.Component, status = d.Status, url = d.Url })
            .ToListAsync(cancellationToken);
        var runId = await RunQuery(payload).OrderByDescending(r => r.StartedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken);
        db.AddEvent(
            EventTypes.PreviewRolloutCompleted,
            payload.ProjectId,
            new { rolloutId = jobId, releaseVersionId = payload.ReleaseVersionId, gitSha = payload.GitSha, state = state.ToWire(), errorCode, message, components, migrationRunId = runId },
            SystemActor.Name,
            time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// FR-91 parity, §4.21: the merged code's production migrations failed after the merge: Owners are told through the
    /// deployment-failed notification (<c>DEPLOYMENT_FAILED</c>, component <c>migrations</c>, no deployment row).
    /// </summary>
    private async Task ProductionMigrationFailedAsync(RolloutJobPayload payload, string? code, CancellationToken cancellationToken)
    {
        var run = await RunAsync(payload, cancellationToken);
        var releaseVersion = await db.ReleaseVersions.AsNoTracking()
            .Where(v => v.Id == payload.ReleaseVersionId)
            .Select(v => v.Version)
            .FirstOrDefaultAsync(cancellationToken);
        db.AddEvent(
            EventTypes.DeploymentFailed,
            payload.ProjectId,
            new
            {
                deploymentId = (Guid?)null,
                component = "migrations",
                target = payload.Environment,
                errorCode = code ?? ErrorCodes.MigrationFailed,
                migrationRunId = run?.Id,
                releaseVersion,
                gitSha = payload.GitSha,
                releaseId = payload.ReleaseId,
                deploymentFailed = true,
            },
            SystemActor.Name,
            time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: preview rollout {JobId} was replaced by a newer one")]
    private static partial void LogSuperseded(ILogger logger, Guid projectId, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {Environment} rollout started its {Component} step")]
    private static partial void LogStep(ILogger logger, Guid projectId, DeploymentEnvironment environment, DeploymentComponent component);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {Environment} rollout finished {State} ({ErrorCode})")]
    private static partial void LogCompleted(ILogger logger, Guid projectId, DeploymentEnvironment environment, RolloutState state, string? errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: the rollout's migration step found no database: {Reason}")]
    private static partial void LogNoDatabase(ILogger logger, Guid projectId, string reason);
}
