using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aide.Api.Health;
using Aide.Infrastructure;
using Npgsql;

namespace Aide.IntegrationTests.Health;

/// <summary>SPEC §6 health routes and §8 NFR-OBS (ready = DB reachable and migrations applied).</summary>
[Collection(ApiTestGroup.Name)]
public class HealthEndpointsTests(AideApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NFR_OBS_live_returns_200()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/health/live", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(Json, Ct);
        Assert.Equal(HealthState.Healthy, body?.Status);
    }

    [Fact]
    public async Task NFR_OBS_ready_returns_200_when_database_is_migrated()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/health/ready", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(Json, Ct);
        Assert.Equal(HealthState.Healthy, body?.Status);
        Assert.Contains(body!.Checks, c => c.Name == "database" && c.Status == HealthState.Healthy);
    }

    [Fact]
    public async Task NFR_OBS_ready_returns_503_when_migrations_are_pending()
    {
        var unmigrated = await CreateEmptyDatabaseAsync($"aide_unmigrated_{Guid.NewGuid():N}");
        await using var unmigratedFactory = factory.WithWebHostBuilder(b =>
            b.UseSetting($"ConnectionStrings:{DependencyInjection.ConnectionStringName}", unmigrated));
        using var client = unmigratedFactory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/health/ready", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(Json, Ct);
        Assert.Equal(HealthState.Unhealthy, body?.Status);
    }

    [Fact]
    public async Task NFR_OBS_ready_returns_503_when_database_is_unreachable()
    {
        var unreachable = new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Port = 1, Timeout = 2 }.ConnectionString;
        await using var downFactory = factory.WithWebHostBuilder(b =>
            b.UseSetting($"ConnectionStrings:{DependencyInjection.ConnectionStringName}", unreachable));
        using var client = downFactory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/health/ready", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private async Task<string> CreateEmptyDatabaseAsync(string name)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{name}\"";
        await command.ExecuteNonQueryAsync(Ct);

        return new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Database = name }.ConnectionString;
    }
}
