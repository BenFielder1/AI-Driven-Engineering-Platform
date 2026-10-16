using Aide.Domain.Entities;
using Aide.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.Persistence;

/// <summary>
/// FR-78/FR-79: the <c>LlmCredentials</c> migration moves v1 project keys into <c>llm_credentials</c> as ciphertext, so
/// their <c>last4</c> can't be computed in SQL and is stored as <see cref="LlmCredential.UnknownLast4"/>. On start-up this
/// decrypts those keys once and fills <c>last4</c>. Idempotent: only <c>????</c> rows are read, and a key that can't be
/// decrypted (lost key ring) is left as it is. Failures are logged and never stop the API.
/// </summary>
public sealed partial class LlmCredentialLast4Backfill(IServiceScopeFactory scopes, ILogger<LlmCredentialLast4Backfill> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            if ((await db.Database.GetPendingMigrationsAsync(stoppingToken)).Any())
            {
                // The migration Job hasn't run yet (or this is a test host before MigrateAsync); the next start backfills.
                return;
            }

            var filled = await FillAsync(db, scope.ServiceProvider.GetRequiredService<ISecretProtector>(), stoppingToken);
            if (filled > 0)
            {
                LogFilled(logger, filled);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            LogFailed(logger, ex);
        }
    }

    /// <summary>Fills <c>last4</c> of every <c>????</c> credential whose key decrypts; returns how many were filled.
    /// Public for tests.</summary>
    public static async Task<int> FillAsync(AideDbContext db, ISecretProtector secrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(secrets);

        var pending = await db.LlmCredentials
            .Where(c => c.Last4 == LlmCredential.UnknownLast4)
            .ToListAsync(cancellationToken);
        var filled = 0;
        foreach (var credential in pending)
        {
            if (secrets.TryUnprotect(credential.SecretEncrypted, out var key) && key.Length > 0)
            {
                credential.Last4 = LlmCredential.Last4Of(key);
                filled++;
            }
        }

        if (filled > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return filled;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Filled last4 of {Count} migrated LLM credentials")]
    private static partial void LogFilled(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The LLM credential last4 backfill failed; it runs again on the next start")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
