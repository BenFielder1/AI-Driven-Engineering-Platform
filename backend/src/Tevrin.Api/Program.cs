using System.Text.Json.Serialization;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tevrin.Api.Agents;
using Tevrin.Api.Auth;
using Tevrin.Api.Errors;
using Tevrin.Api.Features.Activity;
using Tevrin.Api.Features.Admin;
using Tevrin.Api.Features.Analysis;
using Tevrin.Api.Features.Approvals;
using Tevrin.Api.Features.Artifacts;
using Tevrin.Api.Features.Auth;
using Tevrin.Api.Features.Bitbucket;
using Tevrin.Api.Features.BuildChecks;
using Tevrin.Api.Features.Connections;
using Tevrin.Api.Features.Databases;
using Tevrin.Api.Features.Deployments;
using Tevrin.Api.Features.Execution;
using Tevrin.Api.Features.Gates;
using Tevrin.Api.Features.Git;
using Tevrin.Api.Features.GitHub;
using Tevrin.Api.Features.GitLab;
using Tevrin.Api.Features.Lifecycle;
using Tevrin.Api.Features.Live;
using Tevrin.Api.Features.Llm;
using Tevrin.Api.Features.Notifications;
using Tevrin.Api.Features.PipelineRuns;
using Tevrin.Api.Features.Projects;
using Tevrin.Api.Features.Refinement;
using Tevrin.Api.Features.Release;
using Tevrin.Api.Features.Review;
using Tevrin.Api.Features.Rollback;
using Tevrin.Api.Features.Runs;
using Tevrin.Api.Features.Stack;
using Tevrin.Api.Features.Tokens;
using Tevrin.Api.Features.Trackers;
using Tevrin.Api.Features.Versions;
using Tevrin.Api.Health;
using Tevrin.Api.Hosting;
using Tevrin.Api.Jobs;
using Tevrin.Api.Json;
using Tevrin.Api.Logging;
using Tevrin.Api.Pipelines;
using Tevrin.Api.Seed;
using Tevrin.Engine.Api;
using Tevrin.Engine.Runtime.Jobs;
using Tevrin.Engine.Runtime.Notifications;
using Tevrin.Engine.Runtime.Pipelines;
using Tevrin.Infrastructure;
using Tevrin.Infrastructure.Configuration;
using Tevrin.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// Logging: structured JSON to stdout (SPEC §8 NFR-OBS).
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});
// The request scope (RequestId, RequestPath) comes from UseRequestLogScope, with approval tokens redacted (NFR-SEC).
builder.Logging.AddRedactedRequestScope();

// Tracing and metrics (OpenTelemetry). Console exporter only when Telemetry:ConsoleExporter=true (Development).
var consoleExporter = builder.Configuration.GetValue("Telemetry:ConsoleExporter", false);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("tevrin-api"))
    .WithTracing(tracing =>
    {
        tracing
            // NFR-OBS: job and agent-run spans, tagged tevrin.project_id / tevrin.job_id / tevrin.run_id like their log scopes.
            .AddSource(TevrinTelemetry.SourceName)
            // §8: stage-execution and agent-stage spans from the engine.
            .AddSource(Tevrin.Engine.Runtime.EngineTelemetry.SourceName)
            .AddAspNetCoreInstrumentation(o =>
            {
                o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/api/health");
                // Approval links carry their token in the path (FR-48, NFR-SEC): spans record the redacted path.
                o.EnrichWithHttpRequest = TracePaths.Redact;
                o.EnrichWithHttpResponse = (activity, response) => TracePaths.Redact(activity, response.HttpContext.Request);
            })
            // Notification requests are left out: Slack and Teams webhook URLs are credentials (NFR-SEC).
            .AddHttpClientInstrumentation(o => o.FilterHttpRequestMessage = request => !NotificationHttp.IsNotificationRequest(request))
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
builder.Services.AddTevrinProblemDetails();
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
        document.Info.Title = "Tevrin API";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
    options.AddSchemaTransformer<ProblemDetailsSchemaTransformer>();
    options.AddSchemaTransformer<WireEnumSchemaTransformer>();
    options.AddSchemaTransformer<WireAliasEnumSchemaTransformer>();
    options.AddSchemaTransformer<FreeFormObjectSchemaTransformer>();
    // SignalR message schemas (FR-46) aren't reachable from any operation; add them to components.
    options.AddDocumentTransformer<LiveContractDocumentTransformer>();
    // XML-doc descriptions carry the source file's line endings; normalise every description on the finished document
    // (component schemas, property wrappers, parameters, bodies, responses) so api.json is identical on Windows and CI.
    options.AddDocumentTransformer<LineEndingDocumentTransformer>();
});
builder.Services.AddSingleton(TimeProvider.System);

// Client IP, scheme and host behind one or more proxies (FR-105); the middleware itself is the
// ASPNETCORE_FORWARDEDHEADERS_ENABLED switch.
builder.Services.AddTevrinForwardedHeaders();

builder.Services.AddTevrinInfrastructure();
builder.Services.AddTevrinDataProtection(builder.Configuration);
builder.AddTevrinAuth();
builder.Services.AddScoped<ProjectCreator>();

// The Tevrin GitHub App (FR-80–FR-83, FR-86): connections, the repo picker, project repositories and the manifest flow.
builder.Services.AddScoped<GitHubConnections>();
builder.Services.AddScoped<GitHubRepoService>();
builder.Services.AddSingleton<GitHubManifestStates>();
builder.Services.AddScoped<GitHubAppAdmin>();
builder.Services.AddScoped<DeadLetterService>();

// GitLab instances, sign-in and connect, connections and the repo picker (FR-130–FR-132).
builder.Services.AddGitLab();

// Bitbucket consumer, sign-in and connect, connections and the repo picker (FR-133–FR-135).
builder.Services.AddBitbucket();

// Lifecycle, gates and background work (FR-11–FR-14, §4.9).
builder.Services.AddScoped<LifecycleSnapshotLoader>();
builder.Services.AddScoped<LifecycleWriter>();
builder.Services.AddScoped<LifecycleService>();
builder.Services.AddScoped<RollbackWriter>();
builder.Services.AddScoped<GateService>();
builder.Services.AddScoped<GateReader>();
builder.Services.AddPipelineApiHost(); // FR-299–FR-303: the engine's generic API (Tevrin.Engine.Api)
builder.Services.AddTevrinJobs();
builder.Services.AddSdlcJobHandlers();

// Pipeline definitions (FR-275–FR-278, FR-290): the SDLC pack, its guards and providers. Loaded, validated and
// health-checked only; lifecycle decisions still go through the SDLC state machine (M58).
builder.Services.AddSdlcPipeline();

// Artifacts: versions, approval, cascade, integrity, diff (FR-16–FR-21).
builder.Services.AddScoped<ArtifactIntegrityGuard>();
builder.Services.AddScoped<ArtifactReader>();
builder.Services.AddScoped<ArtifactService>();
builder.Services.AddScoped<AgentDraftWriter>();

// Agents (FR-22–FR-26, FR-320): job handlers run agents on the engine runner (AddAgentJobs → AddSdlcAgentExtensions);
// providers and prompts come from Tevrin.Infrastructure.
builder.Services.AddScoped<AgentRepoContext>();
builder.Services.AddScoped<IJobEndedHook, AgentRunJobEndedHook>();

// Refinement, specification and planning agents (FR-27–FR-30): job handlers, refinement rounds and execution steps.
builder.Services.AddAgentJobs();
builder.Services.AddScoped<RefinementService>();

// Execution runner (FR-31–FR-37): step jobs, Guardian, commits on the project branch, pause/resume.
builder.Services.AddExecution();
builder.Services.AddPullRequests();
builder.Services.AddBuildChecks();
builder.Services.AddReview();

// Release and rollback (FR-42–FR-45): the release job, retries, rollback, history and versions.
builder.Services.AddReleases();
builder.Services.AddMergeReadiness();
builder.Services.AddScoped<RollbackService>();
builder.Services.AddReleaseVersions();

// FR-180–FR-185: the Stack Analyst, its trigger after the Repository Analyst, and the Analysis page.
builder.Services.AddStackAnalysis();

// Vercel deployments (FR-87–FR-91, §4.15): settings, env sync, previews, production and the poll job.
builder.Services.AddDeployments();

// Platform connections (FR-142): Vercel, Netlify, Cloudflare, Render, Railway and Supabase tokens and picker data.
builder.Services.AddScoped<PlatformConnectionService>();

// Issue trackers (FR-165–FR-173): browsing with personal connections, project imports and the import job.
builder.Services.AddTrackers();


// Stack compositions on projects and versions, Settings → Stack and import detection (FR-114–FR-119).
builder.Services.AddProjectStacks();

// Notifications (FR-47–FR-49, FR-15): fan-out job, channels, delivery retries, gate reminders, one-click approval links.
builder.Services.AddNotifications();

// Live project updates over SignalR (FR-46).
builder.Services.AddTevrinLive();
builder.Services.AddLiveOriginCheck();
builder.Services.AddOptions<SeedOptions>().BindConfiguration(SeedOptions.SectionName);
builder.Services.AddHostedService<DevSeeder>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadyHealthCheck>("database", tags: [HealthEndpoints.ReadyTag])
    .AddCheck<StackCatalogHealthCheck>("stack-catalog", tags: [HealthEndpoints.ReadyTag])
    .AddCheck<PipelineDefinitionsHealthCheck>("pipeline-definitions", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

// FR-325 (§4.77): one warning per pre-0.8.0 config name still set (ConnectionStrings:Aide), read until 0.9.0.
LegacyConfigNames.LogWarnings(app.Configuration, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tevrin.Api.Configuration"));

app.UseRequestLogScope();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseLiveOriginCheck();
app.UseTevrinAuth();
app.UseProjectLogScope();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json");
}

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapApiTokenEndpoints();
app.MapLlmConnectionEndpoints();
app.MapLlmProviderEndpoints();
app.MapPlatformConnectionEndpoints();
app.MapTrackerEndpoints();
app.MapGitHubEndpoints();
app.MapGitLabEndpoints();
app.MapBitbucketEndpoints();
app.MapAdminUserEndpoints();
app.MapAdminOperationsEndpoints();
app.MapAdminIntegrationEndpoints();
app.MapProjectEndpoints();
app.MapProjectMemberEndpoints();
app.MapProjectSettingsEndpoints();
app.MapDeploymentEndpoints();
app.MapDatabaseEndpoints();
app.MapLifecycleEndpoints();
app.MapGateEndpoints();
app.MapActivityEndpoints();
app.MapArtifactEndpoints();
app.MapRunEndpoints();
app.MapRefinementEndpoints();
app.MapAnalysisEndpoints();
app.MapExecutionEndpoints();
app.MapBuildCheckEndpoints();
app.MapGitEndpoints();
app.MapReviewEndpoints();
app.MapReleaseEndpoints();
app.MapRollbackEndpoints();
app.MapVersionEndpoints();
app.MapStackEndpoints();
app.MapNotificationEndpoints();
app.MapApprovalEndpoints();
app.MapPipelineEndpoints();
app.MapPipelineRunEndpoints();
app.MapLiveEndpoints();

await app.RunAsync();

/// <summary>Entry point; public so <c>WebApplicationFactory&lt;Program&gt;</c> can reach it.</summary>
public partial class Program;
