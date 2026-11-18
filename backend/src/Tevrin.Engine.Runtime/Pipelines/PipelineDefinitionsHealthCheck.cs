using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Tevrin.Engine.Runtime.Pipelines;

/// <summary>
/// FR-290: readiness fails while any registered pipeline definition is invalid (like the host's stack catalog check), or
/// when the registry couldn't be built at all (the failure's message). The description lists the first errors;
/// <see cref="HealthCheckResult.Data"/> holds every one as <c>&lt;pack&gt;/&lt;name&gt;</c> → <c>CODE at path: message</c>.
/// </summary>
public sealed class PipelineDefinitionsHealthCheck(PipelineRegistryHost host) : IHealthCheck
{
    private const int ErrorsInDescription = 5;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (host.Registry is not { } registry)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"The pipeline registry failed to build: {host.Failure?.Message}", host.Failure));
        }

        if (registry.IsValid)
        {
            return Task.FromResult(HealthCheckResult.Healthy($"{registry.Definitions.Count} pipeline definition(s) loaded and valid."));
        }

        var errors = registry.Errors.Select(e => $"{e.Source.PackId}/{e.Source.Name}: {e.Error}").ToList();
        var data = errors
            .Select((text, i) => (Key: $"error{i + 1}", Value: (object)text))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return Task.FromResult(HealthCheckResult.Unhealthy(
            $"{errors.Count} pipeline definition error(s): {string.Join(" | ", errors.Take(ErrorsInDescription))}",
            data: data));
    }
}
