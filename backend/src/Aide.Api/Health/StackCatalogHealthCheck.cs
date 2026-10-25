using Aide.Infrastructure.Stack;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aide.Api.Health;

/// <summary>
/// FR-122: ready only when the stack catalog loaded. A missing file, an unknown section heading or an unknown pack fails
/// the whole catalog; this check is then unhealthy and names the first problems (all of them are logged at startup).
/// </summary>
public sealed class StackCatalogHealthCheck(IStackCatalog catalog) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(catalog.Catalog is { } loaded
            ? HealthCheckResult.Healthy($"{loaded.Label} loaded with {loaded.Packs.Count} pack(s).")
            : HealthCheckResult.Unhealthy($"The stack catalog failed to load ({catalog.Errors.Count} error(s)): {string.Join(" | ", catalog.Errors.Take(3))}"));
}
