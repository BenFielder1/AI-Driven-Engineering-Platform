using System.Text.Json.Serialization;
using Aide.Api.Agents;
using Aide.Api.Auth;
using Aide.Api.Errors;
using Aide.Api.Features.Activity;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Artifacts;
using Aide.Api.Features.Auth;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Gates;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Features.Live;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Refinement;
using Aide.Api.Features.Runs;
using Aide.Api.Features.Tokens;
using Aide.Api.Health;
using Aide.Api.Jobs;
using Aide.Api.Json;
using Aide.Api.Logging;
using Aide.Api.Seed;
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
    options.AddSchemaTransformer<WireEnumSchemaTransformer>();
    options.AddSchemaTransformer<FreeFormObjectSchemaTransformer>();
    // SignalR message schemas (FR-46) aren't reachable from any operation; add them to components.
    options.AddDocumentTransformer<LiveContractDocumentTransformer>();
    // XML-doc descriptions carry the source file's line endings; normalise them so api.json is identical on Windows and CI.
    options.AddSchemaTransformer((schema, _, _) =>
    {
        schema.Description = schema.Description?.ReplaceLineEndings("\n");
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, _, _) =>
    {
        operation.Summary = operation.Summary?.ReplaceLineEndings("\n");
        operation.Description = operation.Description?.ReplaceLineEndings("\n");
        return Task.CompletedTask;
    });
});
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddAideInfrastructure();
builder.Services.AddAideDataProtection(builder.Configuration);
builder.AddAideAuth();
builder.Services.AddScoped<ProjectCreator>();

// Lifecycle, gates and background work (FR-11–FR-14, §4.9).
builder.Services.AddScoped<LifecycleSnapshotLoader>();
builder.Services.AddScoped<LifecycleWriter>();
builder.Services.AddScoped<LifecycleService>();
builder.Services.AddScoped<RollbackWriter>();
builder.Services.AddScoped<GateService>();
builder.Services.AddScoped<GateReader>();
builder.Services.AddAideJobs();

// Artifacts: versions, approval, cascade, integrity, diff (FR-16–FR-21).
builder.Services.AddScoped<ArtifactIntegrityGuard>();
builder.Services.AddScoped<ArtifactReader>();
builder.Services.AddScoped<ArtifactService>();
builder.Services.AddScoped<AgentDraftWriter>();

// Agents (FR-22–FR-26): the runner used by agent job handlers; providers and prompts come from Aide.Infrastructure.
builder.Services.AddScoped<AgentRunner>();
builder.Services.AddScoped<IJobEndedHook, AgentRunJobEndedHook>();

// Refinement, specification and planning agents (FR-27–FR-30): job handlers, refinement rounds and execution steps.
builder.Services.AddAgentJobs();
builder.Services.AddScoped<RefinementService>();

// Execution runner (FR-31–FR-37): step jobs, Guardian, commits on the project branch, pause/resume.
builder.Services.AddExecution();

// Live project updates over SignalR (FR-46).
builder.Services.AddAideLive();
builder.Services.AddLiveOriginCheck();
builder.Services.AddOptions<SeedOptions>().BindConfiguration(SeedOptions.SectionName);
builder.Services.AddHostedService<DevSeeder>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadyHealthCheck>("database", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseLiveOriginCheck();
app.UseAideAuth();
app.UseProjectLogScope();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json");
}

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapApiTokenEndpoints();
app.MapAdminUserEndpoints();
app.MapProjectEndpoints();
app.MapProjectMemberEndpoints();
app.MapProjectSettingsEndpoints();
app.MapLifecycleEndpoints();
app.MapGateEndpoints();
app.MapActivityEndpoints();
app.MapArtifactEndpoints();
app.MapRunEndpoints();
app.MapRefinementEndpoints();
app.MapExecutionEndpoints();
app.MapLiveEndpoints();

await app.RunAsync();

/// <summary>Entry point; public so <c>WebApplicationFactory&lt;Program&gt;</c> can reach it.</summary>
public partial class Program;
