using Aide.Api.Features.Execution;
using Aide.Api.Features.Stack;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Entities;
using Aide.Domain.Stack;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Databases;

/// <summary>
/// FR-153, §4.21: running AIDE's Supabase BaaS migration runner for an environment and recording the run
/// (<c>database_migration_runs</c>): the working copy is synced to the commit first, the run row is saved as <c>running</c>,
/// then the runner applies the pending files over the session pooler (the Fake's <c>*.test</c> host in Fake mode) and the
/// result is recorded with <see cref="MigrationRunReader.Record"/>. FR-155: <c>MIGRATION_RUN_STARTED</c>, then
/// <c>MIGRATION_RUN_COMPLETED</c> or <c>MIGRATION_RUN_FAILED</c> (which notifies Owners).
/// </summary>
public sealed partial class MigrationRunService(
    AideDbContext db,
    ManagedDatabaseSecrets secrets,
    ManagedDatabaseService databases,
    DatabaseSeparation separation,
    IDatabaseProviderResolver providers,
    SupabaseMigrationRunner runner,
    WorkingCopyPreparer preparer,
    VersionStackLoader stacks,
    TimeProvider time,
    ILogger<MigrationRunService> logger)
{
    /// <summary>
    /// Whether the project is Supabase BaaS: its current composition's data pack is <c>data-supabase</c>; a version without
    /// a composition (no data pack, so no ORM to migrate on the platform) counts as BaaS when it has a Supabase database.
    /// </summary>
    public async Task<bool> IsBaasAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var stack = (await stacks.CurrentAsync(projectId, cancellationToken, readBase: false)).Stack;
        if (stack is not null)
        {
            return stack.Composition.Data?.Pack == StackIds.Supabase;
        }

        return await db.ManagedDatabases.AsNoTracking()
            .AnyAsync(d => d.ProjectId == projectId && d.Provider == ManagedDatabaseProvider.Supabase && d.Status != ManagedDatabaseStatus.Removed, cancellationToken);
    }

    /// <summary>The environment's database row and what it means for migrations (<c>null</c> row: there is none).</summary>
    private async Task<(ManagedDatabase? Row, DatabaseMigrationTarget Target)> TargetAsync(Guid projectId, DeploymentEnvironment environment, CancellationToken cancellationToken)
    {
        var row = await db.ManagedDatabases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.ProjectId == projectId && d.Environment == environment, cancellationToken);
        if (row is null)
        {
            return (null, DatabaseMigrationTarget.Absent);
        }

        var connection = secrets.Connection(row);
        var target = ManagedDatabaseRules.MigrationTarget(row.Status, row.ResourceId is not null || connection.Url is not null || connection.PoolerHost is not null);
        return (row, target);
    }

    /// <summary>
    /// Whether a rollout of <paramref name="environment"/> has a migration step: Supabase BaaS with a database for it, usable
    /// or not (a paused one fails the step in <see cref="RunAsync"/>). A BaaS project without a preview database skips
    /// preview migrations (preview then uses production without them).
    /// </summary>
    public async Task<bool> HasMigrationStepAsync(Guid projectId, DeploymentEnvironment environment, CancellationToken cancellationToken) =>
        await IsBaasAsync(projectId, cancellationToken) && (await TargetAsync(projectId, environment, cancellationToken)).Target != DatabaseMigrationTarget.Absent;

    /// <summary>
    /// Runs the migrations of <paramref name="gitSha"/> on the environment's database and records the run (saved). A failed
    /// preparation or a missing database is a failed run (<c>MIGRATION_FAILED</c>) so the caller always has one. §4.21: a
    /// preview run on a database that isn't separate from production (same resource or connection) runs nothing and is a
    /// failed run (<c>VALIDATION</c>): unmerged migrations never reach production. A real database that can't take migrations
    /// now (paused, failed, provisioning) is a failed run too (<c>MIGRATION_FAILED</c>), which blocks the merge.
    /// </summary>
    public async Task<DatabaseMigrationRun> RunAsync(
        Guid projectId, DeploymentEnvironment environment, Guid releaseVersionId, Guid? releaseId, string gitSha, CancellationToken cancellationToken)
    {
        // No row to hang the run on (its FK needs a database): the caller treats this as a failed step.
        var (row, target) = await TargetAsync(projectId, environment, cancellationToken);
        if (row is null || target == DatabaseMigrationTarget.Absent)
        {
            throw new MigrationTargetMissingException($"There is no available {environment.ToWire()} database to migrate.");
        }

        var run = new DatabaseMigrationRun
        {
            ProjectId = projectId,
            ManagedDatabaseId = row.Id,
            ReleaseId = releaseId,
            ReleaseVersionId = releaseVersionId,
            GitSha = gitSha,
            Environment = environment,
            Status = MigrationRunStatus.Running,
            StartedAt = time.GetUtcNow(),
        };
        db.DatabaseMigrationRuns.Add(run);
        db.AddEvent(
            EventTypes.MigrationRunStarted,
            projectId,
            new { runId = run.Id, environment = environment.ToWire(), releaseId, gitSha },
            SystemActor.Name,
            run.StartedAt);
        await db.SaveChangesAsync(cancellationToken);
        var result = target == DatabaseMigrationTarget.Unavailable
            ? Failed($"The {environment.ToWire()} database is {row.Status.ToWire()}: AIDE didn't run the migrations. Restore or fix it in Settings → Deployment, then try again.")
            : environment == DeploymentEnvironment.Preview && !await separation.IsSeparatePreviewAsync(row, cancellationToken)
            ? Failed($"{DatabaseSeparationRules.NotSeparateMessage}: AIDE didn't run the migrations. Link a separate preview database.", ErrorCodes.Validation)
            : await ExecuteAsync(row, projectId, gitSha, cancellationToken);
        MigrationRunReader.Record(run, result, time.GetUtcNow());
        if (run.Status == MigrationRunStatus.Failed)
        {
            db.AddEvent(
                EventTypes.MigrationRunFailed,
                projectId,
                new { runId = run.Id, environment = environment.ToWire(), releaseId, gitSha, errorCode = run.ErrorCode },
                SystemActor.Name,
                time.GetUtcNow());
            LogFailed(logger, projectId, environment, run.ErrorCode);
        }
        else
        {
            db.AddEvent(
                EventTypes.MigrationRunCompleted,
                projectId,
                new { runId = run.Id, environment = environment.ToWire(), releaseId, gitSha, applied = run.Applied.Count },
                SystemActor.Name,
                time.GetUtcNow());
            LogSucceeded(logger, projectId, environment, run.Applied.Count);
        }

        await db.SaveChangesAsync(cancellationToken);
        return run;
    }

    /// <summary>Prepares the working copy, builds the connection and runs the runner; failures before the runner are a failed result.</summary>
    private async Task<MigrationRunResult> ExecuteAsync(ManagedDatabase row, Guid projectId, string gitSha, CancellationToken cancellationToken)
    {
        // The commit must be in the working copy (a merge commit made on the host needs a fetch).
        var prepared = await preparer.TryPrepareAsync(projectId, cancellationToken);
        if (prepared.Result.IsFailure)
        {
            LogNotPrepared(logger, projectId);
        }

        var connection = secrets.Connection(row);
        var connectionString = row.Provider == ManagedDatabaseProvider.Supabase && connection.PoolerHost is not null && secrets.Password(row) is { } password
            ? SupabaseMigrationRunner.SessionConnectionString(connection, password)
            : MigrationConnections.ToNpgsql(connection.Url ?? connection.ExternalUrl);
        if (connectionString is null)
        {
            return Failed($"AIDE can't connect to the {row.Environment.ToWire()} database: its connection details or password are missing. Link it again.");
        }

        var auth = await databases.AuthAsync(row, cancellationToken);
        var provider = providers.ForHost(row.Provider);
        return await runner.RunAsync(
            new MigrationRunRequest(projectId, gitSha, connectionString, auth.IsSuccess ? provider : null, auth.IsSuccess ? auth.Value.Auth : null, row.ResourceId),
            cancellationToken);
    }

    private static MigrationRunResult Failed(string message, string code = ErrorCodes.MigrationFailed) =>
        new(MigrationRunStatus.Failed, [], code, message, [], [], null);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: {Environment} migration run failed ({ErrorCode})")]
    private static partial void LogFailed(ILogger logger, Guid projectId, DeploymentEnvironment environment, string? errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: {Environment} migration run applied {Count} file(s)")]
    private static partial void LogSucceeded(ILogger logger, Guid projectId, DeploymentEnvironment environment, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: the working copy couldn't be synced before the migration run; using what it has")]
    private static partial void LogNotPrepared(ILogger logger, Guid projectId);
}

/// <summary>The environment has no database AIDE can migrate.</summary>
public sealed class MigrationTargetMissingException(string message) : Exception(message);
