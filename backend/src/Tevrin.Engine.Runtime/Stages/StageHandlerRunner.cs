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
        var (stage, handler, refused) = await ResolveAsync(request, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        // FR-360: an external handler acts outside Tevrin only through its approved external_action gate (the run driver's
        // PrepareExternalAsync, then SendExternalAsync); no other path runs it.
        if (IsExternal(request.Handler) || handler is IExternalStageHandler)
        {
            return Unavailable(request, $"handler:{request.Handler} (external: it runs only after an approved external_action gate)");
        }

        LogRunning(logger, request.RunId, stage!.Id, request.Handler);
        var context = new StageHandlerContext(request.RunId, request.ProjectId, stage.Id, request.Facts);
        var result = await handler!.ExecuteAsync(context, cancellationToken);
        return new StageHandlerDispatch.Ran(stage.Id, result);
    }

    /// <summary>FR-360: whether a pack registered <paramref name="handler"/> with <c>external: true</c>, or its DI binding
    /// is an <see cref="IExternalStageHandler"/>.</summary>
    public bool IsExternal(string handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var registry = services.GetService<PipelineRegistry>();
        return registry is not null
            && (registry.IsExternal(PipelineComponentKind.StageHandler, handler)
                || typeof(IExternalStageHandler).IsAssignableFrom(registry.ImplementationOf(PipelineComponentKind.StageHandler, handler)));
    }

    /// <summary>
    /// FR-360: the payload the external handler <paramref name="request"/> names would send, prepared with no effect outside
    /// Tevrin (<see cref="IExternalStageHandler.PreparePayloadAsync"/>). A handler bound without the external contract is
    /// <see cref="StageHandlerDispatch.Unavailable"/>: it is never run, since nothing could show what it would send.
    /// </summary>
    public async Task<ExternalPreparation> PrepareExternalAsync(StageHandlerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (stage, handler, refused) = await ResolveExternalAsync(request, cancellationToken);
        if (refused is not null)
        {
            return new ExternalPreparation(refused, null);
        }

        LogPreparing(logger, request.RunId, stage!.Id, request.Handler);
        var payload = await handler!.PreparePayloadAsync(new StageHandlerContext(request.RunId, request.ProjectId, stage.Id, request.Facts), cancellationToken);
        return new ExternalPreparation(new StageHandlerDispatch.Ran(stage.Id, new StageHandlerResult(StageHandlerOutcome.Waiting)), payload);
    }

    /// <summary>
    /// FR-360: sends <paramref name="approved"/>, the payload an Owner approved on the stage's <c>external_action</c> gate,
    /// through the external handler <paramref name="request"/> names (<see cref="IExternalStageHandler.SendAsync"/>). The
    /// handler is resolved first; then <paramref name="claim"/> commits the caller's claim, and only then is the payload sent,
    /// so a send runs at most once per gate and a handler that can't be resolved is never claimed.
    /// </summary>
    public async Task<StageHandlerDispatch> SendExternalAsync(
        StageHandlerRequest request, ExternalActionPayload approved, Func<CancellationToken, Task> claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(claim);
        var (stage, handler, refused) = await ResolveExternalAsync(request, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        await claim(cancellationToken);
        LogSending(logger, request.RunId, stage!.Id, request.Handler);
        var result = await handler!.SendAsync(new StageHandlerContext(request.RunId, request.ProjectId, stage.Id, request.Facts), approved, cancellationToken);
        return new StageHandlerDispatch.Ran(stage.Id, result);
    }

    private async Task<(StageDefinition? Stage, IExternalStageHandler? Handler, StageHandlerDispatch? Refused)> ResolveExternalAsync(
        StageHandlerRequest request, CancellationToken cancellationToken)
    {
        var (stage, handler, refused) = await ResolveAsync(request, cancellationToken);
        if (refused is not null)
        {
            return (null, null, refused);
        }

        return handler is IExternalStageHandler external
            ? (stage, external, null)
            : (null, null, Unavailable(request, $"handler:{request.Handler} (registered external, but not bound as an external handler)"));
    }

    /// <summary>The pinned definition's stage declaring the handler and the handler bound to its name, or why there are none.</summary>
    private async Task<(StageDefinition? Stage, IStageHandler? Handler, StageHandlerDispatch? Refused)> ResolveAsync(
        StageHandlerRequest request, CancellationToken cancellationToken)
    {
        var resolved = await definitions.ResolveAsync(request.DefinitionHash, cancellationToken);
        if (resolved is null)
        {
            return (null, null, Unavailable(request, "(the definition is neither registered nor stored)"));
        }

        if (!resolved.Available)
        {
            var registry = services.GetService<PipelineRegistry>();
            var missing = registry is null ? "(no pipeline registry)" : string.Join(", ", PipelineDefinitionResolver.MissingNames(registry, resolved.Definition));
            return (null, null, Unavailable(request, missing));
        }

        if (StageHandlers.StageOf(resolved.Definition, request.Handler) is not { } stage)
        {
            LogNotDeclared(logger, request.RunId, request.Handler);
            return (null, null, new StageHandlerDispatch.NotDeclared());
        }

        // A pack may declare the name without the host binding it: the definition validates, but nothing can run it.
        if (services.GetPipelineStageHandler(request.Handler) is not { } handler)
        {
            return (null, null, Unavailable(request, $"handler:{request.Handler} (not bound)"));
        }

        return (stage, handler, null);
    }

    private StageHandlerDispatch.Unavailable Unavailable(StageHandlerRequest request, string missing)
    {
        LogUnavailable(logger, request.RunId, request.DefinitionHash, request.Handler, missing);
        return new StageHandlerDispatch.Unavailable(missing);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pipeline run {PipelineRunId} stage {StageId}: running stage handler {Handler}")]
    private static partial void LogRunning(ILogger logger, Guid pipelineRunId, string stageId, string handler);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline run {PipelineRunId} stage {StageId}: preparing the payload of external handler {Handler}")]
    private static partial void LogPreparing(ILogger logger, Guid pipelineRunId, string stageId, string handler);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline run {PipelineRunId} stage {StageId}: external handler {Handler} sends its approved payload")]
    private static partial void LogSending(ILogger logger, Guid pipelineRunId, string stageId, string handler);

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
