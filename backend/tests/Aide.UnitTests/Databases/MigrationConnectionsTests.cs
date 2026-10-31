using Aide.Domain.Databases;
using Aide.Infrastructure.Databases;
using Npgsql;

namespace Aide.UnitTests.Databases;

/// <summary>FR-148, FR-153, §9: stored connection URIs for the runner, and the Fake executor's in-memory history.</summary>
public class MigrationConnectionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FR153_A_postgres_uri_becomes_an_npgsql_keyword_string()
    {
        var text = MigrationConnections.ToNpgsql("postgresql://aide:p%40ss@db.example.com:6543/app");
        var builder = new NpgsqlConnectionStringBuilder(text);

        Assert.Equal(("db.example.com", 6543, "aide", "p@ss", "app"), (builder.Host, builder.Port, builder.Username, builder.Password, builder.Database));
        Assert.False(builder.Pooling);
        Assert.Null(MigrationConnections.ToNpgsql(null));
        Assert.Equal("Host=x;Database=y", MigrationConnections.ToNpgsql("Host=x;Database=y"));
    }

    [Fact]
    public void FR153_Fake_hosts_are_recognised()
    {
        Assert.True(MigrationConnections.IsFake(MigrationConnections.ToNpgsql("postgresql://aide:fake@fake-db-preview.test:5432/app")!));
        Assert.False(MigrationConnections.IsFake("Host=db.example.com;Database=app"));
    }

    [Fact]
    public async Task FR148_FR153_The_Fake_executor_records_history_fails_on_the_marker_and_resets()
    {
        var executor = new FakeAwareMigrationSqlExecutor(new NpgsqlMigrationSqlExecutor());
        var connection = MigrationConnections.ToNpgsql("postgresql://aide:fake@fake-db-preview.test:5432/app")!;

        await using (var session = await executor.OpenAsync(connection, Ct))
        {
            await session.ApplyAsync(new MigrationFile("supabase/migrations/0001_init.sql", "0001", "init", "create table a();"), ["create table a();"], Ct);
            await Assert.ThrowsAsync<FakeMigrationException>(() => session.ApplyAsync(
                new MigrationFile("supabase/migrations/0002_bad.sql", "0002", "bad", "-- aide-fake:fail"), [], Ct));
            Assert.Equal(["0001"], (await session.EnsureHistoryAsync(Ct)).Select(r => r.Version));
        }

        await executor.ResetAsync(connection, supabaseGrants: true, Ct);

        await using var after = await executor.OpenAsync(connection, Ct);
        Assert.Empty(await after.EnsureHistoryAsync(Ct));
    }
}
