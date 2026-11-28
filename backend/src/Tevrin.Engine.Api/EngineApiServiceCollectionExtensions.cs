using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Api;

/// <summary>
/// FR-299–FR-303: the generic API's services. The host also registers <see cref="Hosting.IRunAccessPolicy"/>,
/// <see cref="Hosting.IGateDecisionService"/>, <see cref="Hosting.IPipelineTransitionSource"/> and
/// <see cref="Hosting.IEngineProblems"/>, then maps <see cref="PipelineEndpoints.MapPipelineEndpoints"/> and
/// <see cref="PipelineRunEndpoints.MapPipelineRunEndpoints"/>.
/// </summary>
public static class EngineApiServiceCollectionExtensions
{
    public static IServiceCollection AddTevrinEngineApi(this IServiceCollection services)
    {
        services.TryAddSingleton<PipelineDefinitionCache>();
        services.TryAddSingleton<Hosting.IRunActivityPolicy, Hosting.AllJobsCount>();
        services.TryAddScoped<PipelineDefinitionResolver>();
        services.TryAddScoped<PipelineRunReader>();
        services.TryAddScoped<PipelineRunOutcomes>();
        services.TryAddScoped<PipelineTimelineReader>();
        services.TryAddScoped<PipelineGateInbox>();
        return services;
    }
}
