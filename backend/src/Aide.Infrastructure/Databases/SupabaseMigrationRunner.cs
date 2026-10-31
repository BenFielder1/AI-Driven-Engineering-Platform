using System.Data.Common;
using System.Diagnostics;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Entities;
using Aide.Infrastructure.Git;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aide.Infrastructure.Databases;

/// <summary>
/// What a migration run needs: the project and commit (its working copy must hold the commit), the session connection
/// string (keyword form; <see cref="SupabaseMigrationRunner.SessionConnectionString"/>), and the Supabase project for the
/// security advisors (<c>null</c> skips them). Never logged.
/// </summary>
public sealed record MigrationRunRequest(Guid ProjectId, string GitSha, string ConnectionString, IDatabaseProvider? Provider, DatabaseAuth? Auth, string? ResourceId)
{
    public override string ToString() => $"MigrationRunRequest {{ ProjectId = {ProjectId}, GitSha = {GitSha} }}";
}

/// <summary>
/// A run's outcome for the caller to record in <c>database_migration_runs</c>: status, the files applied with their
/// durations, the error code (<c>MIGRATION_FAILED</c>, <c>MIGRATION_HISTORY_DIVERGED</c>) and message, the divergences, the
/// advisor findings, and why the advisors couldn't be read (if so).
/// </summary>
public sealed record MigrationRunResult(
    MigrationRunStatus Status,
    IReadOnlyList<AppliedMigration> Applied,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<MigrationDivergence> Diverged,
    IReadOnlyList<AdvisorFinding> Advisors,
    string? AdvisorError);

/// <summary>
/// FR-153, §4.21, D-V4-12: AIDE's own Supabase BaaS migration runner. It reads <c>supabase/migrations/&lt;version&gt;_&lt;name&gt;.sql</c>
/// at the commit from the project's working copy, plans with <see cref="MigrationPlanning"/> against
/// <c>supabase_migrations.schema_migrations</c>, and applies the pending files in version order, each in its own transaction
/// with its history row <c>(version, name, statements)</c>; a crashed run resumes after the last committed file. A recorded
/// version whose file changed or disappeared fails the run with <c>MIGRATION_HISTORY_DIVERGED</c> before anything is
/// applied. After the run, <c>GET /projects/{ref}/advisors/security</c>. No Management API migration or query endpoint is used.
/// </summary>
public sealed partial class SupabaseMigrationRunner(IWorkingCopies workingCopies, IMigrationSqlExecutor executor, ILogger<SupabaseMigrationRunner> logger)
{
    public const int MaxPaths = 50_000;

    public const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>The Supabase session pooler (<c>:5432</c>) as an Npgsql connection string (TLS required, no pooling).</summary>
    public static string SessionConnectionString(ManagedDatabaseConnection connection, string password)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new NpgsqlConnectionStringBuilder
        {
            Host = connection.PoolerHost,
            Port = ManagedDatabaseRules.SupabaseSessionPort,
            Username = connection.PoolerUser,
            Password = password,
            Database = connection.DatabaseName ?? ManagedDatabaseRules.DefaultDatabaseName,
            SslMode = SslMode.Require,
            Pooling = false,
            Timeout = 30,
        }.ConnectionString;
    }

    public async Task<MigrationRunResult> RunAsync(MigrationRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var files = await ReadFilesAsync(request.ProjectId, request.GitSha, cancellationToken);
        if (files is null)
        {
            return Failed([], ErrorCodes.MigrationFailed, $"Commit {Short(request.GitSha)} isn't in the project's working copy.", [], [], null);
        }

        var applied = new List<AppliedMigration>();
        MigrationRunResult result;
        try
        {
            await using var session = await executor.OpenAsync(request.ConnectionString, cancellationToken);
            var recorded = await session.EnsureHistoryAsync(cancellationToken);
            var plan = MigrationPlanning.Plan(files, recorded);
            if (plan.HasDiverged)
            {
                LogDiverged(logger, request.ProjectId, plan.Diverged.Count);
                result = Failed([], ErrorCodes.MigrationHistoryDiverged, MigrationPlanning.Describe(plan.Diverged), plan.Diverged, [], null);
            }
            else if (plan.HasUnreadable)
            {
                // A file AIDE can't read is never applied, recorded, or taken as changed or missing: the run stops first.
                LogUnreadable(logger, request.ProjectId, plan.Unreadable.Count);
                result = Failed([], ErrorCodes.MigrationFailed, MigrationPlanning.DescribeUnreadable(plan.Unreadable), [], [], null);
            }
            else
            {
                result = await ApplyAsync(session, plan, applied, request, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            LogConnectFailed(logger, request.ProjectId, ex.GetType().Name);
            return Failed(applied, ErrorCodes.MigrationFailed, $"Could not run migrations on the database: {Clean(ex.Message)}", [], [], null);
        }

        var (advisors, advisorError) = await AdvisorsAsync(request, cancellationToken);
        return result with { Advisors = advisors, AdvisorError = advisorError };
    }

    private async Task<MigrationRunResult> ApplyAsync(
        IMigrationSession session, MigrationPlan plan, List<AppliedMigration> applied, MigrationRunRequest request, CancellationToken cancellationToken)
    {
        foreach (var file in plan.Pending)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await session.ApplyAsync(file, MigrationPlanning.Statements(file), cancellationToken);
            }
            catch (DbException ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogFileFailed(logger, request.ProjectId, file.Version);
                return Failed(applied, ErrorCodes.MigrationFailed, FileError(file, ex), [], [], null);
            }

            applied.Add(new AppliedMigration(file.Version, file.Name, watch.ElapsedMilliseconds));
            LogApplied(logger, request.ProjectId, file.Version, watch.ElapsedMilliseconds);
        }

        return new MigrationRunResult(MigrationRunStatus.Succeeded, applied, null, null, [], [], null);
    }

    /// <summary>
    /// A failed file as the stored message: the file, the SQLSTATE and the first line of the primary message only. The
    /// detail, hint, where and the server's row data are left out (they can hold row values).
    /// </summary>
    public static string FileError(MigrationFile file, DbException ex)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(ex);
        var text = ex is PostgresException pg ? pg.MessageText : ex.Message;
        var firstLine = (text ?? string.Empty).Split('\n', 2)[0].Trim();
        if (firstLine.Length > 300)
        {
            firstLine = firstLine[..300];
        }

        var state = string.IsNullOrEmpty(ex.SqlState) ? string.Empty : $" (SQLSTATE {ex.SqlState})";
        return $"{file.Version}_{file.Name}.sql failed{state}: {Clean(firstLine)}";
    }

    /// <summary>
    /// The migration files at the commit (path, SQL; <c>null</c> SQL when the working copy couldn't read the file); <c>null</c>
    /// when the commit isn't in the working copy.
    /// </summary>
    private async Task<IReadOnlyList<(string Path, string? Sql)>?> ReadFilesAsync(Guid projectId, string sha, CancellationToken cancellationToken)
    {
        var paths = await workingCopies.ListPathsAtCommitAsync(projectId, sha, MaxPaths, cancellationToken);
        if (paths is null)
        {
            return null;
        }

        var wanted = paths.Where(p => p.StartsWith(MigrationPlanning.Folder + "/", StringComparison.Ordinal) && p.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            && p.IndexOf('/', MigrationPlanning.Folder.Length + 1) < 0).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var texts = await workingCopies.ReadTextAtCommitAsync(projectId, sha, wanted, MaxFileBytes, cancellationToken);

        // A file the working copy couldn't read (binary, too big, repository gone) is present without SQL: never missing or applied.
        return [.. wanted.Select(p => (p, texts.TryGetValue(p, out var text) ? text : (string?)null))];
    }

    private async Task<(IReadOnlyList<AdvisorFinding> Findings, string? Error)> AdvisorsAsync(MigrationRunRequest request, CancellationToken cancellationToken)
    {
        if (request.Provider is null || request.Auth is null || request.ResourceId is null)
        {
            return ([], null);
        }

        try
        {
            return (await request.Provider.SecurityAdvisorsAsync(request.Auth, request.ResourceId, cancellationToken), null);
        }
        catch (DatabaseProviderException ex)
        {
            LogAdvisorsFailed(logger, request.ProjectId, ex.Failure.ToString());
            return ([], Clean(ex.Message));
        }
    }

    private static MigrationRunResult Failed(
        IReadOnlyList<AppliedMigration> applied, string code, string message, IReadOnlyList<MigrationDivergence> diverged, IReadOnlyList<AdvisorFinding> advisors, string? advisorError) =>
        new(MigrationRunStatus.Failed, [.. applied], code, message, diverged, advisors, advisorError);

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;

    private static string Clean(string message)
    {
        var redacted = SecretRedaction.SanitizeProviderError(message);
        return redacted.Length > 1000 ? redacted[..1000] : redacted;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: migration history diverged ({Count} version(s))")]
    private static partial void LogDiverged(ILogger logger, Guid projectId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: {Count} migration file(s) couldn't be read")]
    private static partial void LogUnreadable(ILogger logger, Guid projectId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: could not run migrations ({ErrorType})")]
    private static partial void LogConnectFailed(ILogger logger, Guid projectId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: migration {Version} failed")]
    private static partial void LogFileFailed(ILogger logger, Guid projectId, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: migration {Version} applied in {Ms} ms")]
    private static partial void LogApplied(ILogger logger, Guid projectId, string version, long ms);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId}: security advisors couldn't be read ({Failure})")]
    private static partial void LogAdvisorsFailed(ILogger logger, Guid projectId, string failure);
}
