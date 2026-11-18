using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Jobs;

namespace Tevrin.Engine.Runtime.Pipelines;

/// <summary>
/// FR-290: builds the <see cref="PipelineRegistry"/> exactly once (singleton) and keeps a construction failure (a pack
/// whose <c>Register</c> throws, a duplicate pack or binding) instead of rethrowing it on every resolve, so the readiness
/// check can report it (<see cref="PipelineDefinitionsHealthCheck"/>). <see cref="PipelineRegistryStartup"/> builds it
/// at startup and logs the outcome.
/// </summary>
public sealed class PipelineRegistryHost
{
    public PipelineRegistryHost(
        IEnumerable<IPipelinePack> packs,
        IEnumerable<PipelineComponentRegistration> components,
        IEnumerable<JobHandlerRegistration> jobHandlers)
    {
#pragma warning disable CA1031 // A pack's Register can throw anything; the failure is kept and reported by readiness, not thrown.
        try
        {
            Registry = new PipelineRegistry(packs, components, jobHandlers);
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
#pragma warning restore CA1031
    }

    /// <summary>The registry, or <c>null</c> when building it failed (<see cref="Failure"/>).</summary>
    public PipelineRegistry? Registry { get; }

    /// <summary>Why the registry couldn't be built, or <c>null</c>.</summary>
    public Exception? Failure { get; }

    /// <summary>The registry; throws <see cref="InvalidOperationException"/> (wrapping <see cref="Failure"/>) when it
    /// couldn't be built.</summary>
    public PipelineRegistry GetRegistry() =>
        Registry ?? throw new InvalidOperationException($"The pipeline registry failed to build: {Failure?.Message}", Failure);
}

/// <summary>
/// FR-290: builds the pipeline registry when the host starts and logs every definition error at <c>Error</c> (pack,
/// source, pipeline id, code and path) or the construction failure. It never stops the app: readiness reports unhealthy.
/// </summary>
public sealed partial class PipelineRegistryStartup(PipelineRegistryHost host, ILogger<PipelineRegistryStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (host.Failure is { } failure)
        {
            LogBuildFailed(logger, failure, failure.Message);
            return Task.CompletedTask;
        }

        var registry = host.Registry!;
        foreach (var (source, error) in registry.Errors)
        {
            LogDefinitionError(logger, source.PackId, source.Name, source.Definition?.Pipeline ?? "?", error.Code, error.Path, error.Message);
        }

        if (registry.IsValid)
        {
            LogLoaded(logger, registry.Definitions.Count);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Error, Message = "The pipeline registry failed to build: {Reason}")]
    private static partial void LogBuildFailed(ILogger logger, Exception exception, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipeline definition {PackId}/{Source} (pipeline {PipelineId}) is invalid: {Code} at {Path}: {Detail}")]
    private static partial void LogDefinitionError(ILogger logger, string packId, string source, string pipelineId, string code, string path, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} pipeline definition(s) loaded and valid")]
    private static partial void LogLoaded(ILogger logger, int count);
}
