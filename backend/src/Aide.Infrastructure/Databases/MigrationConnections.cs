using System.Collections.Concurrent;
using System.Data.Common;
using Aide.Domain.Databases;
using Npgsql;

namespace Aide.Infrastructure.Databases;

/// <summary>FR-148: empties a database's schema (preview reset). Never logs the connection string.</summary>
public interface IDatabaseSchemaReset
{
    /// <summary>
    /// Drops <c>public</c> (and the Supabase CLI's <c>supabase_migrations</c> history) and recreates <c>public</c>; with
    /// <paramref name="supabaseGrants"/> the Supabase default grants to <c>anon</c>, <c>authenticated</c> and <c>service_role</c>.
    /// </summary>
    Task ResetAsync(string connectionString, bool supabaseGrants, CancellationToken cancellationToken);
}

/// <summary>Turning stored connection URIs into Npgsql keyword strings, and recognising Fake (<c>*.test</c>) hosts.</summary>
public static class MigrationConnections
{
    /// <summary>
    /// A <c>postgres://</c>/<c>postgresql://</c> URI (Render, External, Fake) as an Npgsql keyword string (TLS preferred,
    /// no pooling); a keyword string is returned as is. <c>null</c> when it can't be read.
    /// </summary>
    public static string? ToNpgsql(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
        {
            return null;
        }

        var text = connection.Trim();
        if (!text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            Database = uri.AbsolutePath.Trim('/') is { Length: > 0 } db ? Uri.UnescapeDataString(db) : ManagedDatabaseRules.DefaultDatabaseName,
            SslMode = SslMode.Prefer,
            Pooling = false,
            Timeout = 30,
        }.ConnectionString;
    }

    /// <summary>Whether the keyword connection string points at a Fake host (<c>*.test</c>, §9): no network is used.</summary>
    public static bool IsFake(string connectionString)
    {
        try
        {
            var host = new NpgsqlConnectionStringBuilder(connectionString).Host ?? string.Empty;
            return host.EndsWith(".test", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The Fake store key of a connection string (host and database).</summary>
    internal static string FakeKey(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return $"{builder.Host}/{builder.Database}".ToLowerInvariant();
    }
}

/// <summary>
/// §9: the runner's executor in every mode. A Fake host (<c>*.test</c>, what Fake databases return) uses an in-memory history
/// per host and database: a file whose SQL contains <see cref="FailMarker"/> fails with SQLSTATE <c>P0001</c>. Any other
/// host uses Npgsql (<see cref="NpgsqlMigrationSqlExecutor"/>).
/// </summary>
public sealed class FakeAwareMigrationSqlExecutor(NpgsqlMigrationSqlExecutor npgsql) : IMigrationSqlExecutor, IDatabaseSchemaReset
{
    /// <summary>SQL containing this makes the Fake migration fail.</summary>
    public const string FailMarker = "aide-fake:fail";

    private readonly ConcurrentDictionary<string, List<RecordedMigration>> _fake = new(StringComparer.Ordinal);

    public Task<IMigrationSession> OpenAsync(string connectionString, CancellationToken cancellationToken) =>
        MigrationConnections.IsFake(connectionString)
            ? Task.FromResult<IMigrationSession>(new FakeSession(_fake.GetOrAdd(MigrationConnections.FakeKey(connectionString), _ => [])))
            : npgsql.OpenAsync(connectionString, cancellationToken);

    public async Task ResetAsync(string connectionString, bool supabaseGrants, CancellationToken cancellationToken)
    {
        if (MigrationConnections.IsFake(connectionString))
        {
            var history = _fake.GetOrAdd(MigrationConnections.FakeKey(connectionString), _ => []);
            lock (history)
            {
                history.Clear();
            }

            return;
        }

        await npgsql.ResetAsync(connectionString, supabaseGrants, cancellationToken);
    }

    private sealed class FakeSession(List<RecordedMigration> history) : IMigrationSession
    {
        public Task<IReadOnlyList<RecordedMigration>> EnsureHistoryAsync(CancellationToken cancellationToken)
        {
            lock (history)
            {
                return Task.FromResult<IReadOnlyList<RecordedMigration>>([.. history]);
            }
        }

        public Task ApplyAsync(MigrationFile file, IReadOnlyList<string> statements, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (file.Sql.Contains(FailMarker, StringComparison.Ordinal))
            {
                throw new FakeMigrationException("The Fake migration failed: the file contains aide-fake:fail.");
            }

            lock (history)
            {
                history.Add(new RecordedMigration(file.Version, file.Name, statements));
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A Fake migration failure (SQLSTATE <c>P0001</c>, raise_exception).</summary>
public sealed class FakeMigrationException(string message) : DbException(message)
{
    public override string SqlState => "P0001";
}
