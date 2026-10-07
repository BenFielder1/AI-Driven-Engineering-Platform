using Aide.Domain.Access;
using Aide.Infrastructure;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Aide.Infrastructure.Security;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Aide.IntegrationTests;

/// <summary>Boots the API against a real PostgreSQL 17 container with the EF Core migrations applied.
/// One container per test collection (<see cref="ApiTestGroup"/>). A bootstrap Admin exists from the start, so
/// accounts registered by tests are Users (FR-3 first-user behaviour is tested on a fresh database).</summary>
public sealed class AideApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string PostgresImage = "postgres:17";
    public const string AdminEmail = "bootstrap-admin@aide.test";
    public const string Password = "Passw0rd-test";

    /// <summary>Requests go to https://localhost so Secure cookies round-trip like in a browser.</summary>
    public static readonly Uri BaseUri = new("https://localhost");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage).Build();
    private readonly string _keysPath = Path.Combine(Path.GetTempPath(), "aide-tests-keys", Guid.NewGuid().ToString("N"));

    public AideApiFactory()
    {
        ClientOptions.BaseAddress = BaseUri;
        ClientOptions.AllowAutoRedirect = false;
    }

    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>Every log entry written by the app (all factories derived from this one).</summary>
    public LogSink Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting($"ConnectionStrings:{DependencyInjection.ConnectionStringName}", ConnectionString);
        builder.UseSetting(DataProtectionSetup.KeysPathKey, _keysPath);

        // Tests share one client IP; the limits themselves are tested with their real values in RateLimitTests.
        builder.UseSetting("Auth:RateLimits:LoginPerMinute", "100000");
        builder.UseSetting("Auth:RateLimits:RegisterPerMinute", "100000");
        // Capture logs for assertions instead of writing JSON to the test console.
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.Services.AddSingleton<ILoggerProvider>(_ => new CapturingLoggerProvider(Logs));
        });
    }

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AideDbContext>().Database.MigrateAsync();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var admin = new AppUser { Email = AdminEmail, UserName = AdminEmail, Name = "Bootstrap Admin", SystemRole = SystemRole.Admin, CreatedAt = DateTimeOffset.UtcNow };
        var result = await users.CreateAsync(admin, Password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }

    /// <summary>A new, migrated, empty database on the same server (for "first user" scenarios).</summary>
    public async Task<string> CreateMigratedDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"aide_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<AideDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable(DependencyInjection.MigrationsHistoryTable))
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new AideDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
        return connectionString;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        try
        {
            Directory.Delete(_keysPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<AideApiFactory>
{
    public const string Name = "Api";
}
