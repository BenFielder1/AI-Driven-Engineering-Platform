using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Runtime.Stages;

/// <summary>
/// FR-280, FR-319, D-0.7.0-43: dispatches a code stage's work. It resolves the run's pinned definition as the agent runner
/// does (FR-277: registered, or stored with every name registered), finds the stage that declares the requested handler
/// (<c>handler:</c> or <c>handlers:</c>), resolves the <see cref="IStageHandler"/> bound to that name from the scope, and
/// invokes it with a <see cref="StageHandlerContext"/> carrying the request's facts. The runner never catches the handler's
/// exceptions: the job layer's own (<c>JobFailedException</c>, <c>JobDeferredException</c>) reach the worker unchanged, so a
/// job that runs its work through a handler keeps its queueing and retries.
/// </summary>
public sealed partial class StageHandlerRunner(
    PipelineDefinitionResolver definitions,
    IServiceProvider services,
    ILogger<StageHandlerRunner> logger)
{
    public async Task<StageHandlerDispatch> RunAsync(StageHandlerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = await definitions.ResolveAsync(request.DefinitionHash, cancellationToken);
        if (resolved is null)
        {
            return Unavailable(request, "(the definition is neither registered nor stored)");
        }

        if (!resolved.Available)
        {
            var registry = services.GetService<PipelineRegistry>();
            var missing = registry is null ? "(no pipeline registry)" : string.Join(", ", PipelineDefinitionResolver.MissingNames(registry, resolved.Definition));
            return Unavailable(request, missing);
        }

        if (StageHandlers.StageOf(resolved.Definition, request.Handler) is not { } stage)
        {
            LogNotDeclared(logger, request.RunId, request.Handler);
            return new StageHandlerDispatch.NotDeclared();
        }

        // A pack may declare the name without the host binding it: the definition validates, but nothing can run it.
        if (services.GetPipelineStageHandler(request.Handler) is not { } handler)
        {
            return Unavailable(request, $"handler:{request.Handler} (not bound)");
        }

        LogRunning(logger, request.RunId, stage.Id, request.Handler);
        var context = new StageHandlerContext(request.RunId, request.ProjectId, stage.Id, request.Facts);
        var result = await handler.ExecuteAsync(context, cancellationToken);
        return new StageHandlerDispatch.Ran(stage.Id, result);
    }

    private StageHandlerDispatch.Unavailable Unavailable(StageHandlerRequest request, string missing)
    {
        LogUnavailable(logger, request.RunId, request.DefinitionHash, request.Handler, missing);
        return new StageHandlerDispatch.Unavailable(missing);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pipeline run {PipelineRunId} stage {StageId}: running stage handler {Handler}")]
    private static partial void LogRunning(ILogger logger, Guid pipelineRunId, string stageId, string handler);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pipeline run {PipelineRunId}'s definition declares no stage with handler {Handler}")]
    private static partial void LogNotDeclared(ILogger logger, Guid pipelineRunId, string handler);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline run {PipelineRunId} is pinned to definition {DefinitionHash}, which can't run stage handler {Handler} (missing: {Missing})")]
    private static partial void LogUnavailable(ILogger logger, Guid pipelineRunId, string definitionHash, string handler, string missing);
}

/// <summary>D-0.7.0-43: DI registration of the stage handler runner.</summary>
public static class StageHandlerServiceCollectionExtensions
{
    /// <summary>
    /// <see cref="StageHandlerRunner"/> and <see cref="PipelineDefinitionResolver"/> (scoped) and the definition cache
    /// (singleton). Needs the pipeline registry and the host's <see cref="Persistence.IEngineDbContext"/>; handlers are bound
    /// with <c>AddPipelineStageHandler&lt;T&gt;(name)</c>.
    /// </summary>
    public static IServiceCollection AddStageHandlers(this IServiceCollection services)
    {
        services.AddLogging();
        services.TryAddSingleton<PipelineDefinitionCache>();
        services.TryAddScoped<PipelineDefinitionResolver>();
        services.TryAddScoped<StageHandlerRunner>();
        return services;
    }
}
