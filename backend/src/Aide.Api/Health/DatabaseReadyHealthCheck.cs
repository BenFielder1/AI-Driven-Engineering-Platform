using Aide.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aide.Api.Health;

/// <summary>Ready when the database is reachable and has no pending EF Core migrations (SPEC §8).</summary>
public sealed class DatabaseReadyHealthCheck(AideDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("Database is unreachable.");
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
            return pending == 0
                ? HealthCheckResult.Healthy("Database reachable; migrations applied.")
                : HealthCheckResult.Unhealthy($"Database has {pending} pending migration(s).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Database check failed.", ex);
        }
    }
}
