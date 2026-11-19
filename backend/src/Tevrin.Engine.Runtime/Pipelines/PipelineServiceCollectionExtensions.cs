using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Runtime.Pipelines;

/// <summary>FR-278: DI registration of packs, named pipeline components and the <see cref="PipelineRegistry"/>.</summary>
public static class PipelineServiceCollectionExtensions
{
    /// <summary>
    /// The <see cref="PipelineRegistry"/> singleton, built once by <see cref="PipelineRegistryHost"/> (which keeps a
    /// construction failure for readiness), <see cref="PipelineRegistryStartup"/> to build it and log its errors when the
    /// host starts, and the readiness check type (registering the check with <c>AddHealthChecks()</c> is the host's
    /// choice). Resolving <see cref="PipelineRegistry"/> after a failed build throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public static IServiceCollection AddPipelineRegistry(this IServiceCollection services)
    {
        services.TryAddSingleton<PipelineRegistryHost>();
        services.TryAddSingleton(sp => sp.GetRequiredService<PipelineRegistryHost>().GetRegistry());
        services.TryAddSingleton<PipelineDefinitionsHealthCheck>();
        services.AddHostedService<PipelineRegistryStartup>();
        return services;
    }

    public static IServiceCollection AddPipelinePack<TPack>(this IServiceCollection services)
        where TPack : class, IPipelinePack
    {
        services.AddSingleton<IPipelinePack, TPack>();
        return services;
    }

    public static IServiceCollection AddPipelinePack(this IServiceCollection services, IPipelinePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        services.AddSingleton(pack);
        return services;
    }

    /// <summary>Binds guard <paramref name="name"/> (<c>&lt;pack&gt;.&lt;name&gt;</c>) to <typeparamref name="TGuard"/> (scoped).</summary>
    public static IServiceCollection AddPipelineGuard<TGuard>(this IServiceCollection services, string name)
        where TGuard : class, IGuard =>
        services.AddComponent<TGuard>(PipelineComponentKind.Guard, name);

    /// <summary>Binds stage handler <paramref name="name"/> to <typeparamref name="THandler"/> (scoped).</summary>
    public static IServiceCollection AddPipelineStageHandler<THandler>(this IServiceCollection services, string name)
        where THandler : class, IStageHandler =>
        services.AddComponent<THandler>(PipelineComponentKind.StageHandler, name);

    /// <summary>Binds tool <paramref name="name"/> to <typeparamref name="TTool"/> (scoped).</summary>
    public static IServiceCollection AddPipelineTool<TTool>(this IServiceCollection services, string name)
        where TTool : class, IAgentTool =>
        services.AddComponent<TTool>(PipelineComponentKind.Tool, name);

    /// <summary>The guard bound to <paramref name="name"/>, resolved from <paramref name="services"/> (a scope), or <c>null</c>.</summary>
    public static IGuard? GetPipelineGuard(this IServiceProvider services, string name) =>
        services.GetComponent<IGuard>(PipelineComponentKind.Guard, name);

    public static IStageHandler? GetPipelineStageHandler(this IServiceProvider services, string name) =>
        services.GetComponent<IStageHandler>(PipelineComponentKind.StageHandler, name);

    public static IAgentTool? GetPipelineTool(this IServiceProvider services, string name) =>
        services.GetComponent<IAgentTool>(PipelineComponentKind.Tool, name);

    private static IServiceCollection AddComponent<T>(this IServiceCollection services, PipelineComponentKind kind, string name)
        where T : class
    {
        PipelineNames.EnsureComponentName(name);
        services.TryAddScoped<T>();
        services.AddSingleton(new PipelineComponentRegistration(kind, name, typeof(T)));
        return services;
    }

    private static T? GetComponent<T>(this IServiceProvider services, PipelineComponentKind kind, string name)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        var type = services.GetRequiredService<PipelineRegistry>().ImplementationOf(kind, name);
        return type is null ? null : (T)services.GetRequiredService(type);
    }
}
