using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;
using Tevrin.Api.Json;
using Tevrin.Infrastructure.Persistence;

namespace Tevrin.Api.Features.Live;

/// <summary>
/// Registration of the FR-46 live-update hub. Negotiate is a POST, so cookie sessions send <c>X-XSRF-TOKEN</c> like any
/// mutation (antiforgery, §8); bearer-token clients are exempt; the WebSocket upgrade is a GET.
/// </summary>
public static class LiveEndpoints
{
    /// <summary>SignalR with the same JSON wire format as the HTTP API (snake_case domain enums, camelCase names).</summary>
    public static IServiceCollection AddTevrinLive(this IServiceCollection services)
    {
        services.AddSignalR().AddJsonProtocol(o =>
        {
            o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.PayloadSerializerOptions.Converters.Add(new DomainEnumJsonConverter());
            o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            o.PayloadSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });
        services.AddSingleton<ProjectViewerRegistry>();
        services.AddSingleton<PipelineLivePublisher>();

        // FR-363: committed branch and map item changes go out as stageBranchUpdated and (throttled) mapProgress.
        services.AddSingleton<PipelineConstructLivePublisher>();
        services.AddSingleton<PipelineConstructLiveInterceptor>();
        services.ConfigureDbContext<TevrinDbContext>((sp, options) => options.AddInterceptors(sp.GetRequiredService<PipelineConstructLiveInterceptor>()));

        // Committed project events (from any writer) are pushed to the project's group.
        services.RemoveAll<IProjectEventPublisher>();
        services.AddSingleton<IProjectEventPublisher, SignalRProjectEventPublisher>();
        return services;
    }

    public static IEndpointRouteBuilder MapLiveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHub<ProjectsHub>(ProjectsHub.Path).RequireAuthorization();
        return app;
    }
}

/// <summary>
/// SignalR messages aren't HTTP operations, so OpenAPI never sees them. This adds their schemas to
/// <c>components.schemas</c> (generated the same way as response schemas), so the frontend's generated types include
/// <c>ProjectEventMessage</c>.
/// </summary>
internal sealed class LiveContractDocumentTransformer : IOpenApiDocumentTransformer
{
    private static readonly Type[] MessageTypes = [typeof(ProjectEventMessage), typeof(BuildChecks.BuildCheckUpdatedMessage),
        typeof(Tevrin.Engine.Api.PipelineRunDto), typeof(Tevrin.Engine.Api.PipelineStageDto), typeof(Tevrin.Engine.Api.PipelineGateDto),
        typeof(Tevrin.Engine.Api.PipelineStageBranchUpdatedMessage), typeof(Tevrin.Engine.Api.PipelineMapProgressMessage)];

    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        foreach (var type in MessageTypes)
        {
            var schema = await context.GetOrCreateSchemaAsync(type, parameterDescription: null, cancellationToken);
            document.Components.Schemas[type.Name] = schema;
        }
    }
}
