using System.Text.Json.Serialization;
using Aide.Api.Health;
using Aide.Infrastructure;
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

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "AIDE Platform API";
    document.Info.Version = "v1";
    return Task.CompletedTask;
}));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddAideInfrastructure();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadyHealthCheck>("database", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json");
}

app.MapHealthEndpoints();

await app.RunAsync();

/// <summary>Entry point; public so <c>WebApplicationFactory&lt;Program&gt;</c> can reach it.</summary>
public partial class Program;
