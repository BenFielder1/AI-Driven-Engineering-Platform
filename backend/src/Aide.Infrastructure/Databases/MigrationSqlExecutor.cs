using Aide.Domain.Databases;
using Npgsql;

namespace Aide.Infrastructure.Databases;

/// <summary>Opens a session on a database for the migration runner (one connection for the whole run).</summary>
public interface IMigrationSqlExecutor
{
    Task<IMigrationSession> OpenAsync(string connectionString, CancellationToken cancellationToken);
}

/// <summary>
/// A migration session: holds AIDE's advisory lock (one run per database at a time), creates the CLI's history table when
/// missing and reads it, and applies one file in its own transaction together with its history row.
/// </summary>
public interface IMigrationSession : IAsyncDisposable
{
    Task<IReadOnlyList<RecordedMigration>> EnsureHistoryAsync(CancellationToken cancellationToken);

    Task ApplyAsync(MigrationFile file, IReadOnlyList<string> statements, CancellationToken cancellationToken);
}

/// <summary>
/// D-V4-12, §4.21: the Npgsql session (over the Supabase session pooler in production). The history is the Supabase CLI's
/// <c>supabase_migrations.schema_migrations (version text primary key, statements text[], name text)</c>, created as the CLI
/// does when missing (<c>CREATE SCHEMA/TABLE IF NOT EXISTS</c>, <c>ADD COLUMN IF NOT EXISTS</c>).
/// </summary>
public sealed class NpgsqlMigrationSqlExecutor : IMigrationSqlExecutor
{
    public async Task<IMigrationSession> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(hashtext('aide_supabase_migrations'))", connection))
            {
                command.CommandTimeout = 120;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return new Session(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Session(NpgsqlConnection connection) : IMigrationSession
    {
        private const string EnsureSql = """
            CREATE SCHEMA IF NOT EXISTS supabase_migrations;
            CREATE TABLE IF NOT EXISTS supabase_migrations.schema_migrations (version text NOT NULL PRIMARY KEY);
            ALTER TABLE supabase_migrations.schema_migrations ADD COLUMN IF NOT EXISTS statements text[];
            ALTER TABLE supabase_migrations.schema_migrations ADD COLUMN IF NOT EXISTS name text;
            """;

        public async Task<IReadOnlyList<RecordedMigration>> EnsureHistoryAsync(CancellationToken cancellationToken)
        {
            await using (var ensure = new NpgsqlCommand(EnsureSql, connection))
            {
                await ensure.ExecuteNonQueryAsync(cancellationToken);
            }

            var rows = new List<RecordedMigration>();
            await using var read = new NpgsqlCommand("SELECT version, name, statements FROM supabase_migrations.schema_migrations ORDER BY version", connection);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new RecordedMigration(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? [] : reader.GetFieldValue<string[]>(2)));
            }

            return rows;
        }

        public async Task ApplyAsync(MigrationFile file, IReadOnlyList<string> statements, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(statements);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(file.Sql))
            {
                await using var apply = new NpgsqlCommand(file.Sql, connection, transaction) { CommandTimeout = 600 };
                await apply.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var record = new NpgsqlCommand(
                "INSERT INTO supabase_migrations.schema_migrations (version, name, statements) VALUES ($1, $2, $3)", connection, transaction))
            {
                record.Parameters.Add(new NpgsqlParameter { Value = file.Version });
                record.Parameters.Add(new NpgsqlParameter { Value = file.Name });
                record.Parameters.Add(new NpgsqlParameter { Value = statements.ToArray() });
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext('aide_supabase_migrations'))", connection);
                    await unlock.ExecuteNonQueryAsync();
                }
            }
            catch (NpgsqlException)
            {
                // The lock goes with the session anyway.
            }

            await connection.DisposeAsync();
        }
    }
}
