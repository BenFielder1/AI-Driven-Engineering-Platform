using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Trackers;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Llm;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.Infrastructure.Trackers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aide.Api.Features.Trackers;

/// <summary>
/// FR-170: one import. Fetches the items once, normalises them, stores them on the import, and writes a new auto-locked
/// <c>source_items</c> version of all the version's active imports, with <c>TRACKER_IMPORT_COMPLETED</c>, all in one transaction. Idempotent:
/// an import that already ended is left alone.
/// <list type="bullet">
/// <item>A rate limit re-queues the job for <c>Retry-After</c> without using an attempt (<c>TRACKER_RATE_LIMITED</c> on the import while it waits).</item>
/// <item>Over a §4.26 limit: <c>SOURCE_ITEMS_TOO_LARGE</c>, nothing written. Other permanent failures are recorded with their code; an unreachable tracker retries, then
/// <see cref="TrackerImportJobEndedHook"/> records <c>TRACKER_IMPORT_FAILED</c>.</item>
/// <item>A concurrent snapshot (another import of the version: a concurrency conflict or unique violation) re-queues without using an attempt. Any other
/// save failure can't be fixed by trying again, so the import fails.</item>
/// </list>
/// </summary>
public sealed partial class TrackerImportJobHandler(
    AideDbContext db,
    TrackerConnectionResolver connections,
    SourceSnapshotWriter snapshots,
    IIssueTrackers trackers,
    ILlmResolver llm,
    ILlmProviderCatalog catalog,
    TimeProvider time,
    ILogger<TrackerImportJobHandler> logger) : IJobHandler
{
    private static readonly TimeSpan ConflictRetry = TimeSpan.FromSeconds(2);

    public async Task HandleAsync(JobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var payload = Payload(job);
        var import = await db.TrackerImports.FirstOrDefaultAsync(i => i.Id == payload.ImportId, cancellationToken);
        if (import is null || import.Status is not (TrackerImportStatus.Queued or TrackerImportStatus.Running))
        {
            return;
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == payload.ProjectId, cancellationToken);
        if (project is null)
        {
            return;
        }

        var phase = TrackerRules.CanChangeImports(project.CurrentPhase);
        if (phase.IsFailure || project.CurrentReleaseVersionId != import.ReleaseVersionId)
        {
            // D-V5-10: downstream artifacts never change silently.
            await FailAsync(import, ErrorCodes.WrongPhase, phase.Error?.Message ?? "The project moved on to another version.", cancellationToken);
            return;
        }

        import.Status = TrackerImportStatus.Running;
        import.ErrorCode = null;
        import.ErrorMessage = null;
        await SaveOrDeferAsync(import.Id, cancellationToken);

        IReadOnlyList<SourceItem> fetched;
        try
        {
            fetched = await FetchAsync(import, cancellationToken);
        }
        catch (TrackerFetchFailedException ex)
        {
            await FailAsync(import, ex.Code, ex.Message, cancellationToken);
            throw new JobFailedException(ex.Message, transient: false, ex.Code);
        }

        // §4.26: the limits come first, on what the snapshot would hold; nothing is written when one is exceeded.
        var items = SourceItemLimits.LinkChildren([.. fetched.Select(i => SourceItemLimits.Normalize(i, import.Options))]);
        var links = await db.TrackerImports.AsNoTracking()
            .Where(i => i.ReleaseVersionId == import.ReleaseVersionId)
            .Select(i => new TrackerImportLink(i.Id, i.SupersedesImportId, i.Status))
            .ToListAsync(cancellationToken);
        var replaced = TrackerImportChain.Ancestors(links, import.Id).Where(l => l.Status != TrackerImportStatus.Removed).Select(l => l.Id).ToList();
        var others = await db.TrackerImports.AsNoTracking()
            .Where(i => i.ReleaseVersionId == import.ReleaseVersionId && i.Status == TrackerImportStatus.Completed && i.Id != import.Id && !replaced.Contains(i.Id))
            .ToListAsync(cancellationToken);
        var current = new SourceImportSnapshot(import.Id, import.Provider, import.CreatedAt, items);
        var rendered = SourceItemsRenderer.Render([.. others.Select(SourceSnapshotWriter.ToSnapshot), current]);
        var count = SourceItemLimits.CheckItemCount(Math.Max(items.Count, rendered.Items.Count));
        var size = count.IsSuccess ? SourceItemLimits.CheckRenderedSize(rendered.ByteCount, await BudgetAsync(project.Id, cancellationToken)) : count;
        if (size.IsFailure)
        {
            await FailAsync(import, size.Error!.Code, size.Error.Message, cancellationToken);
            throw new JobFailedException(size.Error.Message, transient: false, size.Error.Code);
        }

        var now = time.GetUtcNow();
        var artifact = await snapshots.AddAsync(project, import.ReleaseVersionId, rendered.Markdown, import.CreatedBy, now, cancellationToken);
        import.Items = items;
        import.ItemCount = items.Count;
        import.RenderedBytes = rendered.ByteCount;
        import.Status = TrackerImportStatus.Completed;
        import.CompletedAt = now;
        import.SourceArtifactId = artifact.Id;
        foreach (var superseded in await db.TrackerImports.Where(i => replaced.Contains(i.Id)).ToListAsync(cancellationToken))
        {
            superseded.Status = TrackerImportStatus.Superseded;
        }

        db.AddEvent(
            EventTypes.TrackerImportCompleted,
            project.Id,
            new
            {
                importId = import.Id,
                provider = import.Provider.ToWire(),
                itemCount = items.Count,
                renderedBytes = rendered.ByteCount,
                releaseVersionId = import.ReleaseVersionId,
                artifactId = artifact.Id,
                version = artifact.Version,
                contentHash = artifact.ContentHash,
            },
            SystemActor.Name,
            now);
        await SaveOrDeferAsync(import.Id, cancellationToken);
        LogCompleted(logger, import.Id, import.Provider, items.Count, artifact.Version);
    }

    private async Task<IReadOnlyList<SourceItem>> FetchAsync(TrackerImport import, CancellationToken cancellationToken)
    {
        var resolved = await connections.ResolveAsync(import.CreatedBy, import.Provider, import.PlatformConnectionId ?? import.GitConnectionId, cancellationToken);
        if (resolved.IsFailure)
        {
            if (resolved.Error!.Code is ErrorCodes.TrackerNotConnected or ErrorCodes.ConnectionScopeInsufficient or ErrorCodes.CredentialNotOwner or ErrorCodes.NotFound)
            {
                throw new TrackerFetchFailedException(resolved.Error.Code, resolved.Error.Message);
            }

            throw new JobFailedException(resolved.Error.Message, transient: true, ErrorCodes.TrackerImportFailed);
        }

        var repository = import.Query.Keys is { Count: > 0 } keys ? TrackerRules.RepositoryOf(keys[0]) : import.Query.ContainerId;
        try
        {
            var reading = await connections.ForReadingAsync(resolved.Value, repository, cancellationToken);
            return await trackers.ForProvider(import.Provider).GetItemsAsync(reading.Credential, import.Query, import.Options, cancellationToken);
        }
        catch (TrackerApiException ex) when (ex.Failure == TrackerFailure.RateLimited)
        {
            // FR-170: wait for Retry-After; the worker doesn't count the attempt.
            var runAfter = ex.RetryAt ?? time.GetUtcNow() + TimeSpan.FromMinutes(1);
            import.Status = TrackerImportStatus.Queued;
            import.ErrorCode = ErrorCodes.TrackerRateLimited;
            import.ErrorMessage = $"{TrackerRules.DisplayName(import.Provider)} is rate limiting requests. AIDE will retry.";
            await db.SaveChangesAsync(cancellationToken);
            throw new JobDeferredException($"{ErrorCodes.TrackerRateLimited}: waiting until {runAfter:u}.", runAfter);
        }
        catch (TrackerApiException ex) when (ex.Failure == TrackerFailure.Upstream)
        {
            throw new JobFailedException(ex.Message, transient: true, ErrorCodes.TrackerImportFailed);
        }
        catch (TrackerApiException ex)
        {
            if (ex.Failure == TrackerFailure.Rejected)
            {
                await connections.OnRejectedAsync(resolved.Value, import.ProjectId, SystemActor.Name, cancellationToken);
            }

            var error = TrackerRules.ToError(import.Provider, ex.Failure, ex.Message);
            throw new TrackerFetchFailedException(
                error.Code == ErrorCodes.UpstreamError ? ErrorCodes.TrackerImportFailed : error.Code,
                $"The import from {TrackerRules.DisplayName(import.Provider)} failed: {error.Message}");
        }
    }

    /// <summary>§4.26: the budget of the refinement model's catalog entry.</summary>
    private async Task<int> BudgetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var model = await llm.ResolveAsync(projectId, AgentKind.IdeaRefinement, cancellationToken);
        var entry = LlmOutputLimits.Find(catalog.Catalog(model.Provider), model.Model);
        return SourceItemLimits.MaxRenderedBytes(entry?.ContextTokens);
    }

    private async Task FailAsync(TrackerImport import, string code, string message, CancellationToken cancellationToken)
    {
        TrackerImportFailure.Mark(db, import, code, message, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        LogFailed(logger, import.Id, import.Provider, code);
    }

    /// <summary>
    /// Another writer (the snapshot of another import, a removal) got there first: run again shortly, attempt not counted. Any other save failure
    /// would repeat forever, so the unsaved changes are dropped and the import fails.
    /// </summary>
    private async Task SaveOrDeferAsync(Guid importId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsConflict(ex))
        {
            throw new JobDeferredException($"The import collided with a concurrent change: {ex.GetType().Name}.", time.GetUtcNow() + ConflictRetry);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var import = await db.TrackerImports.FirstAsync(i => i.Id == importId, cancellationToken);
            var message = $"The import from {TrackerRules.DisplayName(import.Provider)} failed: its items couldn't be stored.";
            await FailAsync(import, ErrorCodes.TrackerImportFailed, message, cancellationToken);
            throw new JobFailedException(message, transient: false, ErrorCodes.TrackerImportFailed);
        }
    }

    /// <summary>Whether trying again can help: a concurrency conflict or a unique violation (23505).</summary>
    public static bool IsConflict(DbUpdateException exception) =>
        exception is DbUpdateConcurrencyException || exception?.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static TrackerImportJobPayload Payload(JobExecution job)
    {
        try
        {
            return AideJson.Deserialize<TrackerImportJobPayload>(job.Payload);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new JobFailedException($"The {job.Type} job payload can't be read: {ex.Message}", transient: false, ErrorCodes.Validation);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tracker import {ImportId} ({Provider}) completed: {ItemCount} items, source_items v{ArtifactVersion}")]
    private static partial void LogCompleted(ILogger logger, Guid importId, TrackerProvider provider, int itemCount, int artifactVersion);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tracker import {ImportId} ({Provider}) failed: {ErrorCode}")]
    private static partial void LogFailed(ILogger logger, Guid importId, TrackerProvider provider, string errorCode);

    private sealed class TrackerFetchFailedException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}

/// <summary>FR-170: marks an import failed and adds <c>TRACKER_IMPORT_FAILED</c>, with no body or token. The caller saves.</summary>
internal static class TrackerImportFailure
{
    public const int MaxMessageLength = 1_000;

    public static void Mark(AideDbContext db, TrackerImport import, string code, string message, DateTimeOffset now)
    {
        import.Status = TrackerImportStatus.Failed;
        import.ErrorCode = code;
        var clean = SourceItemLimits.StripNul(message);
        import.ErrorMessage = clean.Length > MaxMessageLength ? clean[..MaxMessageLength] : clean;
        import.CompletedAt = now;
        db.AddEvent(
            EventTypes.TrackerImportFailed,
            import.ProjectId,
            new { importId = import.Id, provider = import.Provider.ToWire(), errorCode = code, releaseVersionId = import.ReleaseVersionId },
            SystemActor.Name,
            now);
    }
}

/// <summary>A job that ended without succeeding (retries used up, or the lease expired): its import becomes <c>failed</c> if it hasn't already.</summary>
public sealed class TrackerImportJobEndedHook(TimeProvider time) : IJobEndedHook
{
    public async Task OnJobEndedAsync(AideDbContext db, JobEnded job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        if (job.JobType != JobTypes.TrackerImport)
        {
            return;
        }

        var payload = await db.Jobs.AsNoTracking().Where(j => j.Id == job.JobId).Select(j => j.Payload).FirstOrDefaultAsync(cancellationToken);
        if (payload is null)
        {
            return;
        }

        Guid importId;
        try
        {
            importId = AideJson.Deserialize<TrackerImportJobPayload>(payload).ImportId;
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        var import = await db.TrackerImports.FirstOrDefaultAsync(i => i.Id == importId, cancellationToken);
        if (import?.Status is TrackerImportStatus.Queued or TrackerImportStatus.Running)
        {
            var name = TrackerRules.DisplayName(import.Provider);
            TrackerImportFailure.Mark(db, import, ErrorCodes.TrackerImportFailed, $"The import from {name} failed: {name} returned an error or couldn't be reached.", time.GetUtcNow());
        }
    }
}
