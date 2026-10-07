using System.Text.Json.Serialization;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Auth;
using Aide.Api.Features.Tokens;
using Aide.Api.Health;
using Aide.Api.Json;
using Aide.Infrastructure;
using Aide.Infrastructure.Security;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Logging: structured JSON to stdout (SPEC §8 NFR-OBS).
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

// Tracing and metrics (OpenTelemetry). Console exporter only when Telemetry:ConsoleExporter=true (Development).
var consoleExporter = builder.Configuration.GetValue("Telemetry:ConsoleExporter", false);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("aide-api"))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/api/health"))
            .AddHttpClientInstrumentation()
            .AddNpgsql();
        if (consoleExporter)
        {
            tracing.AddConsoleExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
        if (consoleExporter)
        {
            metrics.AddConsoleExporter();
        }
    });

// Errors: ProblemDetails with errorCode + traceId everywhere (§6).
builder.Services.AddAideProblemDetails();
builder.Services.AddValidation();

// Domain enums on the wire use the snake_case names of SPEC.md and the database ("admin", "in_progress");
// numbers are plain JSON numbers (not "number or numeric string").
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new DomainEnumJsonConverter());
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "AIDE Platform API";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
    options.AddSchemaTransformer<ProblemDetailsSchemaTransformer>();
});
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddAideInfrastructure();
builder.Services.AddAideDataProtection(builder.Configuration);
builder.AddAideAuth();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadyHealthCheck>("database", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAideAuth();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json");
}

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapApiTokenEndpoints();
app.MapAdminUserEndpoints();

await app.RunAsync();

/// <summary>Entry point; public so <c>WebApplicationFactory&lt;Program&gt;</c> can reach it.</summary>
public partial class Program;
