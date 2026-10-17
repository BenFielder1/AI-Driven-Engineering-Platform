using Aide.Infrastructure.Presets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aide.Api.Health;

/// <summary>
/// §4.11: ready only when every shipped preset loaded. A preset with a missing referenced file (or any other format
/// error) is left out of the catalogue and makes this check unhealthy, naming the preset (the reasons are logged at startup).
/// </summary>
public sealed class PresetsHealthCheck(IPresetCatalog catalog) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(catalog.Errors.Count == 0
            ? HealthCheckResult.Healthy($"{catalog.All.Count} preset(s) loaded.")
            : HealthCheckResult.Unhealthy($"{catalog.Errors.Count} preset(s) failed to load: {string.Join(", ", catalog.Errors.Select(e => e.Id))}."));
}
