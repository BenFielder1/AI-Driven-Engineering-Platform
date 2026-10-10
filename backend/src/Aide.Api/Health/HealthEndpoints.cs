using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aide.Api.Health;

/// <summary>Liveness and readiness probes (SPEC §6, §8 NFR-OBS). Public, no auth.</summary>
public static class HealthEndpoints
{
    /// <summary>Tag for health checks that gate readiness.</summary>
    public const string ReadyTag = "ready";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/health").WithTags("Health");

        group.MapGet("/live", GetLive)
            .WithName("GetHealthLive")
            .WithSummary("Liveness: the process is up. No dependency checks.");

        group.MapGet("/ready", GetReady)
            .WithName("GetHealthReady")
            .WithSummary("Readiness: the database is reachable and all EF Core migrations are applied.")
            .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static Ok<HealthResponse> GetLive() =>
        TypedResults.Ok(new HealthResponse(HealthState.Healthy, []));

    private static async Task<Results<Ok<HealthResponse>, JsonHttpResult<HealthResponse>>> GetReady(
        HealthCheckService healthChecks, CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains(ReadyTag), cancellationToken);

        var body = new HealthResponse(
            ToState(report.Status),
            [.. report.Entries.Select(e => new HealthCheckEntry(e.Key, ToState(e.Value.Status), e.Value.Description))]);

        return report.Status == HealthStatus.Unhealthy
            ? TypedResults.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable)
            : TypedResults.Ok(body);
    }

    private static HealthState ToState(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => HealthState.Healthy,
        HealthStatus.Degraded => HealthState.Degraded,
        _ => HealthState.Unhealthy,
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<HealthState>))]
public enum HealthState
{
    Healthy,
    Degraded,
    Unhealthy,
}

public sealed record HealthResponse(HealthState Status, IReadOnlyList<HealthCheckEntry> Checks);

public sealed record HealthCheckEntry(string Name, HealthState Status, string? Description);
