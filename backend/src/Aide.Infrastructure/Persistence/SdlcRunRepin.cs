using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Persistence;
using Tevrin.Engine.Runtime.Pipelines;

namespace Aide.Infrastructure.Persistence;

/// <summary>
/// FR-321, D-0.7.0-41, D-0.7.0-44: non-final SDLC runs pinned to an earlier 0.7.0 SDLC definition (version 1, which left
/// plan validation to host code, or version 2, which left the other output checks and execution's handlers to it) are
/// re-pinned to the current one (version 3), which describes the same behaviour. Only
/// <see cref="PreviousHashes"/> are re-pinned; a run pinned to any other hash keeps it (an unknown one still gets
/// <c>DEFINITION_UNAVAILABLE</c>), and final runs are history and keep theirs.
/// </summary>
public static class SdlcRunRepin
{
    /// <summary>The hash of <c>sdlc.pipeline.yaml</c> version 1 (0.7.0 until FR-321).</summary>
    public const string Version1Hash = "90f1f065397fe245955c62ef5057122a6fca2ae373941a90dc37b431ecf0c9e7";

    /// <summary>The hash of <c>sdlc.pipeline.yaml</c> version 2 (FR-321 plan validation, until D-0.7.0-43/-44).</summary>
    public const string Version2Hash = "d2f1c98b44e99bd673944f36cfdfd3abb64e072f893e3b7ea6d8366c3f689873";

    /// <summary>The earlier SDLC hashes whose non-final runs move to the current definition.</summary>
    public static IReadOnlyList<string> PreviousHashes { get; } = [Version1Hash, Version2Hash];

    /// <summary>The hashes to re-pin to <paramref name="currentHash"/> (never the current one itself).</summary>
    public static IReadOnlyList<string> From(string currentHash) =>
        [.. PreviousHashes.Where(h => !string.Equals(h, currentHash, StringComparison.Ordinal))];

    /// <summary>
    /// Stores the current definition (idempotent), then re-pins in one <c>UPDATE … WHERE definition_hash IN (…)</c>, so it is
    /// idempotent and safe when several replicas start at once (a second run, or a second replica, updates nothing).
    /// Returns how many runs moved.
    /// </summary>
    public static async Task<int> RunAsync(AideDbContext db, LoadedPipelineDefinition current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(current);
        var from = From(current.Hash!);
        if (from.Count == 0)
        {
            return 0;
        }

        var pipeline = current.Definition!.Pipeline;
        var hash = current.Hash!;
        var pinned = await db.PipelineRuns.AnyAsync(r => r.PipelineId == pipeline && from.Contains(r.DefinitionHash), cancellationToken);
        if (!pinned)
        {
            return 0;
        }

        // The final generic statuses come from the engine's rule, so a status added later as final is never re-pinned.
        var finals = Enum.GetValues<GenericRunStatus>().Where(PipelineRunRules.IsFinal).ToList();
        await StoredDefinitions.StoreAsync(db, current, now, cancellationToken);
        return await db.PipelineRuns
            .Where(r => r.PipelineId == pipeline && from.Contains(r.DefinitionHash) && !finals.Contains(r.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DefinitionHash, hash), cancellationToken);
    }
}

/// <summary>
/// FR-321: the re-pin at start-up, before the run backfill and the job worker (hosted services start in order, and this one
/// finishes in <see cref="StartAsync"/>). Skipped while migrations are pending. Logged once, when runs moved. Failures are
/// logged and never stop the API; the next start tries again.
/// </summary>
public sealed partial class SdlcRunRepinService(
    IServiceScopeFactory scopes, TimeProvider time, ILogger<SdlcRunRepinService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<PipelineRegistryHost>() is not { Registry: not null } host)
            {
                return;
            }

            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                return;
            }

            var current = SdlcRunDefinition.Current(host);
            var (to, from) = (current.Hash!, string.Join(", ", SdlcRunRepin.From(current.Hash!)));
            var moved = await SdlcRunRepin.RunAsync(db, current, time.GetUtcNow(), cancellationToken);
            if (moved > 0)
            {
                // One entry for every run moved, whichever earlier version it was pinned to.
                LogRepinned(logger, moved, from, to);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            LogFailed(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "FR-321: re-pinned {Runs} SDLC run(s) from definition {From} to {To}")]
    private static partial void LogRepinned(ILogger logger, int runs, string from, string to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The SDLC run re-pin failed; it runs again on the next start")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
