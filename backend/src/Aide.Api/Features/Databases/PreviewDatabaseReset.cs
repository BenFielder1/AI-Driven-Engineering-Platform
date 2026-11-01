using Aide.Api.Features.Deployments;
using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Databases;

/// <summary>Payload of the <c>preview_database_reset</c> job (FR-148).</summary>
public sealed record PreviewDatabaseResetPayload(Guid ProjectId, Guid? DatabaseId, PreviewResetMethod Method, string Actor);

/// <summary>
/// FR-148: the queued reset: its <c>jobId</c> and <c>method</c> (<c>reset_schema</c>: AIDE empties the schema, Supabase BaaS
/// then re-applies the base commit's migrations; <c>reset_schema_external</c>: Render, over its external URL;
/// <c>manual</c>: Railway — empty the preview Postgres in Railway, AIDE records the reset). The next backend preview's
/// pre-deploy re-applies every migration. Progress: <c>GET …/deployments/preview</c> (<c>databaseDiverged</c>).
/// </summary>
public sealed record PreviewDatabaseResetDto(Guid JobId, PreviewResetMethod Method);

/// <summary>FR-148: queues a preview database reset (the caller passed the Owner check).</summary>
public sealed class PreviewDatabaseResetService(AideDbContext db, RolloutScheduler rollouts, DatabaseSeparation separation, JobEnqueuer jobs, TimeProvider time)
{
    /// <summary>
    /// Under the project lock (as <see cref="RolloutScheduler.EnqueuePreviewAsync"/>), so a reset and a preview rollout are
    /// never both queued: a running or queued rollout refuses the reset, and a queued reset makes a rollout wait.
    /// </summary>
    public async Task<(PreviewDatabaseResetDto? Reset, DomainError? Error)> ResetAsync(Guid projectId, string actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await Projects.ProjectLocks.LockProjectAsync(db, projectId, cancellationToken);
        if (await rollouts.PreviewRunningAsync(projectId, cancellationToken) || await rollouts.PreviewResetPendingAsync(projectId, cancellationToken))
        {
            return (null, new DomainError(ErrorCodes.InvalidState, "A preview rollout or reset is running. Wait for it to finish, then reset the preview database."));
        }

        var row = await db.ManagedDatabases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.ProjectId == projectId && d.Environment == DeploymentEnvironment.Preview && d.Status != ManagedDatabaseStatus.Removed, cancellationToken);
        if (row is not null && !await separation.IsSeparatePreviewAsync(row, cancellationToken))
        {
            return (null, new DomainError(ErrorCodes.Validation, $"{DatabaseSeparationRules.NotSeparateMessage}: link a separate preview database before resetting it."));
        }

        var method = PreviewDatabaseRules.ResetMethod(row?.Provider, row?.Status);
        if (row is null)
        {
            // Render and Railway create the preview database with the preview service (it may have no card of its own).
            var backend = await db.DeploymentTargets.AsNoTracking()
                .Where(t => t.ProjectId == projectId && t.Component == DeploymentComponent.Backend)
                .Select(t => (DeploymentProvider?)t.Provider)
                .FirstOrDefaultAsync(cancellationToken);
            method = backend is DeploymentProvider.Render or DeploymentProvider.Railway ? PreviewResetMethod.Manual : PreviewResetMethod.None;
        }

        if (method == PreviewResetMethod.None)
        {
            return (null, row is null
                ? new DomainError(ErrorCodes.NotFound, "There is no preview database to reset.")
                : new DomainError(ErrorCodes.InvalidState, "The preview database isn't available yet; wait for it, then reset it."));
        }

        var now = time.GetUtcNow();
        var job = jobs.Enqueue(JobTypes.PreviewDatabaseReset, new PreviewDatabaseResetPayload(projectId, row?.Id, method, actor), projectId, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (new PreviewDatabaseResetDto(job.Id, method), null);
    }
}

/// <summary>
/// FR-148: the <c>preview_database_reset</c> job. Empties the preview schema (<see cref="IDatabaseSchemaReset"/>) where AIDE
/// can reach it, writes <c>DATABASE_RESET</c> (the divergence baseline restarts there), then for Supabase BaaS re-applies the
/// migrations of the version's base commit (recorded as a preview run). A Railway reset only records the reset.
/// </summary>
public sealed partial class PreviewDatabaseResetJobHandler(
    AideDbContext db,
    ManagedDatabaseSecrets secrets,
    DatabaseSeparation separation,
    IDatabaseSchemaReset schema,
    MigrationRunService migrations,
    TimeProvider time,
    ILogger<PreviewDatabaseResetJobHandler> logger) : IJobHandler
{
    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        PreviewDatabaseResetPayload payload;
        try
        {
            payload = AideJson.Deserialize<PreviewDatabaseResetPayload>(job.Payload);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new JobFailedException("The reset job payload can't be read.", transient: false, ErrorCodes.Validation);
        }

        var row = payload.DatabaseId is { } id ? await db.ManagedDatabases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken) : null;

        // §4.21: never reset production. The row must still be the project's preview database and not production's
        // (same resource or connection); otherwise the job fails before any SQL runs.
        if (row is not null && (row.ProjectId != payload.ProjectId || !await separation.IsSeparatePreviewAsync(row, cancellationToken)))
        {
            LogRefused(logger, payload.ProjectId);
            throw new JobFailedException($"{DatabaseSeparationRules.NotSeparateMessage}: AIDE didn't reset it. Link a separate preview database, then reset again.", transient: false, ErrorCodes.Validation);
        }

        if (payload.Method is PreviewResetMethod.ResetSchema or PreviewResetMethod.ResetSchemaExternal)
        {
            if (row is null)
            {
                throw new JobFailedException("The preview database is gone.", transient: false, ErrorCodes.NotFound);
            }

            var connection = secrets.Connection(row);
            var connectionString = payload.Method == PreviewResetMethod.ResetSchemaExternal
                ? MigrationConnections.ToNpgsql(connection.ExternalUrl)
                : row.Provider == ManagedDatabaseProvider.Supabase && connection.PoolerHost is not null && secrets.Password(row) is { } password
                    ? SupabaseMigrationRunner.SessionConnectionString(connection, password)
                    : MigrationConnections.ToNpgsql(connection.Url);
            if (connectionString is null)
            {
                throw new JobFailedException("AIDE can't connect to the preview database (no connection details or password). Link it again.", transient: false, ErrorCodes.Validation);
            }

            try
            {
                await schema.ResetAsync(connectionString, row.Provider == ManagedDatabaseProvider.Supabase, cancellationToken);
            }
            catch (System.Data.Common.DbException ex)
            {
                throw new JobFailedException($"Resetting the preview database failed (SQLSTATE {ex.SqlState}).", transient: true, ErrorCodes.UpstreamError);
            }
        }

        db.AddEvent(
            EventTypes.DatabaseReset,
            payload.ProjectId,
            new { databaseId = row?.Id, provider = row?.Provider.ToWire(), method = payload.Method.ToWire(), environment = "preview" },
            payload.Actor,
            time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        LogReset(logger, payload.ProjectId, payload.Method);

        // Supabase BaaS: the preview gets the base commit's migrations back (the next rollout applies the branch's own).
        var version = await db.CurrentReleaseVersionAsync(payload.ProjectId, cancellationToken);
        if (version?.BaseCommitSha is { Length: > 0 } baseSha
            && await migrations.HasMigrationStepAsync(payload.ProjectId, DeploymentEnvironment.Preview, cancellationToken))
        {
            var run = await migrations.RunAsync(payload.ProjectId, DeploymentEnvironment.Preview, version.Id, null, baseSha, cancellationToken);
            LogReapplied(logger, payload.ProjectId, run.Status, run.Applied.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: preview database reset refused: it isn't a separate preview database")]
    private static partial void LogRefused(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: preview database reset ({Method})")]
    private static partial void LogReset(ILogger logger, Guid projectId, PreviewResetMethod method);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: base commit migrations re-applied to the preview database ({Status}, {Count} file(s))")]
    private static partial void LogReapplied(ILogger logger, Guid projectId, MigrationRunStatus status, int count);
}
