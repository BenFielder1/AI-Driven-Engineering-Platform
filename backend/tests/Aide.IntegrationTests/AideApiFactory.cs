using Aide.Domain.Access;
using Aide.Infrastructure;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Identity;
using Aide.Infrastructure.Presets;
using Aide.Infrastructure.Security;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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

    /// <summary>Approvals:SigningKey in tests (FR-48).</summary>
    public const string TestSigningKey = "test-approval-signing-key-0123456789abcdef";

    /// <summary>Requests go to https://localhost so Secure cookies round-trip like in a browser.</summary>
    public static readonly Uri BaseUri = new("https://localhost");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage).Build();
    private readonly string _keysPath = Path.Combine(Path.GetTempPath(), "aide-tests-keys", Guid.NewGuid().ToString("N"));

    /// <summary>Git working copies (<c>Workspaces:Root</c>) for this factory, deleted with it.</summary>
    public string WorkspacesRoot { get; } = Path.Combine(Path.GetTempPath(), "aide-tests-workspaces", Guid.NewGuid().ToString("N"));

    /// <summary>GitHub API base address for tests: unresolvable, so no test can reach the real GitHub. Tests that
    /// need GitHub swap the <c>github</c> HttpClient's handler for a fake.</summary>
    public const string UnreachableGitHubApi = "https://github-api.invalid/";

    /// <summary>LLM provider base address for tests: unresolvable (see <see cref="NoNetworkHandler"/>).</summary>
    public const string UnreachableLlmApi = "https://llm-api.invalid/";

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
        builder.UseSetting("Workspaces:Root", WorkspacesRoot);
        builder.UseSetting("GitHub:ApiBaseUrl", UnreachableGitHubApi);

        // No system LLM keys, whatever the machine's environment: providers needing a key are "not configured".
        builder.UseSetting("Llm:Providers:Anthropic:ApiKey", string.Empty);
        builder.UseSetting("Llm:Providers:OpenAI:ApiKey", string.Empty);

        // No test can reach a real LLM provider (CLAUDE.md rule 11): unresolvable base URLs, and every provider's
        // HttpClient answers through a handler that refuses to send. Tests that script a provider swap in their own
        // handler with ConfigureTestServices (registered later, so it wins).
        builder.UseSetting("Llm:Providers:Anthropic:BaseUrl", UnreachableLlmApi);
        builder.UseSetting("Llm:Providers:OpenAI:BaseUrl", UnreachableLlmApi);
        builder.UseSetting("Llm:Providers:Ollama:BaseUrl", UnreachableLlmApi);
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<AnthropicLlmProvider>().ConfigurePrimaryHttpMessageHandler(() => new NoNetworkHandler());
            services.AddHttpClient<OpenAiLlmProvider>().ConfigurePrimaryHttpMessageHandler(() => new NoNetworkHandler());
            services.AddHttpClient<OllamaLlmProvider>().ConfigurePrimaryHttpMessageHandler(() => new NoNetworkHandler());

            // FR-74: the test-only fixture preset (tests/Fixtures/Presets/test-stack) next to the shipped ones.
            services.AddSingleton<IPresetSource>(new EmbeddedPresetSource(typeof(AideApiFactory).Assembly, TestPresetId.Prefix));
        });

        // Tests share one client IP; the limits themselves are tested with their real values in RateLimitTests.
        builder.UseSetting("Auth:RateLimits:LoginPerMinute", "100000");
        builder.UseSetting("Auth:RateLimits:RegisterPerMinute", "100000");

        // The background job worker doesn't run in tests; job tests drive JobWorker.RunOnceAsync with their own clock.
        builder.UseSetting("Jobs:Enabled", "false");
        // Likewise the notification sweep (FR-15, FR-49): notification tests call NotificationSweeper's passes directly.
        builder.UseSetting("Notifications:SweepEnabled", "false");
        // FR-48 links are signed with a test key; no SMTP server: email channels are skipped unless a test fakes the transport.
        builder.UseSetting("Approvals:SigningKey", TestSigningKey);
        builder.UseSetting("Smtp:Host", string.Empty);
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
        var connectionString = await CreateEmptyDatabaseAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AideDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable(DependencyInjection.MigrationsHistoryTable))
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new AideDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
        return connectionString;
    }

    /// <summary>A new database on the same server with no migrations applied (every migration is pending).</summary>
    public async Task<string> CreateEmptyDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"aide_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        foreach (var path in new[] { _keysPath, WorkspacesRoot })
        {
            try
            {
                // Git marks object files read-only, which blocks deletion on Windows.
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}

/// <summary>An HTTP handler that never sends: every request fails as a connection error.</summary>
public sealed class NoNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ConnectionError, $"Tests never reach real providers ({request.RequestUri?.Host})."));
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<AideApiFactory>
{
    public const string Name = "Api";
}
