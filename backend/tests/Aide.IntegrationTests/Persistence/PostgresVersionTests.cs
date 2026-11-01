using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aide.IntegrationTests.Persistence;

/// <summary>FR-163, D-V5-1: AIDE's own database is PostgreSQL 18, and every migration applies to it from empty.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PostgresVersionTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR163_database_reports_server_version_18()
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SHOW server_version", connection);

        var version = Assert.IsType<string>(await command.ExecuteScalarAsync(Ct));

        Assert.StartsWith("18", version, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR163_every_migration_is_applied_from_empty()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync(Ct));
    }
}
