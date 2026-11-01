using Aide.Api.Features.Activity;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Features.Versions;
using Aide.Api.Jobs;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Trackers;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Configurations;
using Aide.Infrastructure.Trackers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aide.Api.Features.Trackers;

/// <summary>Payload of the <see cref="JobTypes.TrackerImport"/> job.</summary>
public sealed record TrackerImportJobPayload(Guid ProjectId, Guid ImportId);

/// <summary>An import checked and ready to queue.</summary>
public sealed record PlannedImport(TrackerConnectionRef Connection, TrackerSelection Selection, TrackerImportOptions Options);

/// <summary>
/// FR-170, FR-171, FR-173: a version's imports. Queues <c>tracker_import</c> jobs (the fetch never runs in a request), lists imports and
/// snapshots, re-imports, removes and checks drift. Rules are in <see cref="TrackerRules"/>, <see cref="SourceItemLimits"/> and <see cref="SourceItemDriftRules"/>.
/// </summary>
public sealed partial class SourceImportService(
    AideDbContext db,
    TrackerConnectionResolver connections,
    SourceSnapshotWriter snapshots,
    JobEnqueuer jobs,
    IIssueTrackers trackers,
    TimeProvider time,
    ILogger<SourceImportService> logger)
{
    /// <summary>
    /// FR-171: the imports of a request, checked with no network and no secret: selections (<c>VALIDATION</c> on
    /// <paramref name="prefix"/><c>[i].selection.*</c>, <c>SOURCE_ITEMS_TOO_LARGE</c>) and connections (<c>CREDENTIAL_NOT_OWNER</c>, <c>NOT_FOUND</c>).
    /// With <paramref name="connectionRequired"/> false, having no connection doesn't stop creation: the import fails later and shows it (FR-171).
    /// </summary>
    public async Task<Result<IReadOnlyList<PlannedImport>>> PlanAsync(Guid userId, IReadOnlyList<SourceImportRequest>? requests, string prefix, bool connectionRequired, CancellationToken cancellationToken)
    {
        if (requests is not { Count: > 0 })
        {
            return Result.Success<IReadOnlyList<PlannedImport>>([]);
        }

        if (requests.Count > TrackerRules.MaxImportsPerRequest)
        {
            return Result.Failure<IReadOnlyList<PlannedImport>>(new DomainError(ErrorCodes.Validation, "The imports are invalid.")
            {
                Details = [$"{prefix}: At most {TrackerRules.MaxImportsPerRequest} imports."],
            });
        }

        var planned = new List<PlannedImport>(requests.Count);
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var selection = request.Selection.ToDomain();
            var valid = TrackerRules.ValidateSelection(request.Provider, selection, $"{prefix}[{i}].selection.");
            if (valid.IsFailure)
            {
                return Result.Failure<IReadOnlyList<PlannedImport>>(valid.Error!);
            }

            var connection = await connections.FindOwnAsync(userId, request.Provider, request.ConnectionId, connectionRequired, cancellationToken);
            if (connection.IsFailure)
            {
                return Result.Failure<IReadOnlyList<PlannedImport>>(connection.Error!);
            }

            planned.Add(new PlannedImport(connection.Value, selection, request.Options.ToDomain()));
        }

        return Result.Success<IReadOnlyList<PlannedImport>>(planned);
    }

    /// <summary>FR-170, FR-171: adds the import row and its job to the unit of work (the caller saves), so they commit with what started them.</summary>
    public TrackerImport Queue(Guid projectId, Guid releaseVersionId, PlannedImport planned, Guid userId, DateTimeOffset now, Guid? supersedes = null)
    {
        ArgumentNullException.ThrowIfNull(planned);
        var import = new TrackerImport
        {
            ProjectId = projectId,
            ReleaseVersionId = releaseVersionId,
            Provider = planned.Connection.Provider,
            PlatformConnectionId = planned.Connection.PlatformConnectionId,
            GitConnectionId = planned.Connection.GitConnectionId,
            GithubUserId = planned.Connection.GithubUserId,
            SupersedesImportId = supersedes,
            Query = planned.Selection,
            Options = planned.Options,
            Status = TrackerImportStatus.Queued,
            CreatedBy = userId,
            CreatedAt = now,
        };
        db.TrackerImports.Add(import);
        jobs.Enqueue(JobTypes.TrackerImport, new TrackerImportJobPayload(projectId, import.Id), projectId, now);
        return import;
    }

    /// <summary>§6 <c>GET /sources</c>; <c>null</c> for an unknown project.</summary>
    public async Task<Result<ProjectSourcesDto>> GetAsync(Guid projectId, Guid? releaseVersionId, CancellationToken cancellationToken)
    {
        var scope = await ReleaseVersionScope.ResolveAsync(db, projectId, releaseVersionId, cancellationToken);
        if (scope.IsFailure)
        {
            return Result.Failure<ProjectSourcesDto>(scope.Error!);
        }

        var project = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.CurrentPhase, p.CurrentReleaseVersionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return Result.Failure<ProjectSourcesDto>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        var versionId = scope.Value;
        var imports = await db.TrackerImports.AsNoTracking()
            .Where(i => i.ProjectId == projectId && i.ReleaseVersionId == versionId)
            .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .ToListAsync(cancellationToken);
        var names = await ActorResolver.NamesAsync(db, imports.Select(i => i.CreatedBy).Distinct(), cancellationToken);
        var snapshot = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == versionId && a.Type == ArtifactType.SourceItems && a.Status == ArtifactStatus.Locked)
            .OrderByDescending(a => a.Version)
            .Select(a => new SourceSnapshotDto(a.Id, a.Version, a.ContentHash, a.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);
        return Result.Success(new ProjectSourcesDto(
            versionId,
            [.. imports.Select(i => ToDto(i, names))],
            snapshot,
            await DraftStateAsync(projectId, versionId, snapshot, cancellationToken),
            versionId == project.CurrentReleaseVersionId && TrackerRules.CanChangeImports(project.CurrentPhase).IsSuccess));
    }

    /// <summary>§6 <c>POST /sources</c>: <c>WRONG_PHASE</c>, <c>CREDENTIAL_NOT_OWNER</c>, <c>TRACKER_NOT_CONNECTED</c>, <c>SOURCE_ITEMS_TOO_LARGE</c> (ticked keys over the limit with what's active); else a queued import.</summary>
    public async Task<Result<SourceImportDto>> AddAsync(Guid projectId, SourceImportRequest request, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = await WritableAsync(projectId, cancellationToken);
        if (project.IsFailure)
        {
            return Result.Failure<SourceImportDto>(project.Error!);
        }

        var planned = await PlanAsync(userId, [request], "sourceImports", connectionRequired: true, cancellationToken);
        if (planned.IsFailure)
        {
            return Result.Failure<SourceImportDto>(planned.Error!);
        }

        var versionId = project.Value.CurrentReleaseVersionId!.Value;
        var keys = request.Selection.Keys?.Count ?? 0;
        var active = await db.TrackerImports.AsNoTracking()
            .Where(i => i.ReleaseVersionId == versionId && i.Status == TrackerImportStatus.Completed)
            .SumAsync(i => i.ItemCount, cancellationToken);
        var limit = SourceItemLimits.CheckItemCount(keys + active);
        if (limit.IsFailure)
        {
            return Result.Failure<SourceImportDto>(limit.Error!);
        }

        var now = time.GetUtcNow();
        var import = Queue(projectId, versionId, planned.Value[0], userId, now);
        await db.SaveChangesAsync(cancellationToken);
        LogQueued(logger, projectId, import.Id, import.Provider);
        return await DtoAsync(import, cancellationToken);
    }

    /// <summary>
    /// FR-173: repeats an import's query (also a failed one's: the retry), with the importer's connection when the caller is the importer,
    /// else the caller's own on that tracker (connections are personal). When it completes it supersedes its whole retry chain. A re-import
    /// already queued or running is returned as it is: no second job.
    /// </summary>
    public async Task<Result<SourceImportDto>> ReimportAsync(Guid projectId, Guid importId, Guid userId, CancellationToken cancellationToken)
    {
        var project = await WritableAsync(projectId, cancellationToken);
        if (project.IsFailure)
        {
            return Result.Failure<SourceImportDto>(project.Error!);
        }

        var versionId = project.Value.CurrentReleaseVersionId!.Value;
        var plan = TrackerImportChain.PlanReimport(await LinksAsync(versionId, cancellationToken), importId);
        if (plan.NotFound)
        {
            return Result.Failure<SourceImportDto>(new DomainError(ErrorCodes.NotFound, "The import was not found."));
        }

        if (plan.Pending is { } pending)
        {
            return await ExistingAsync(pending, cancellationToken);
        }

        var previous = await db.TrackerImports.AsNoTracking().FirstAsync(i => i.Id == plan.Supersedes, cancellationToken);
        var own = previous.CreatedBy == userId ? previous.PlatformConnectionId ?? previous.GitConnectionId : null;
        var connection = await connections.FindOwnAsync(userId, previous.Provider, own, required: true, cancellationToken);
        if (connection.IsFailure)
        {
            return Result.Failure<SourceImportDto>(connection.Error!);
        }

        var import = Queue(projectId, previous.ReleaseVersionId, new PlannedImport(connection.Value, previous.Query, previous.Options), userId, time.GetUtcNow(), supersedes: previous.Id);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: TrackerImportIndexes.OneReplacement })
        {
            // A concurrent retry queued the replacement first: hand that one back.
            db.ChangeTracker.Clear();
            return await ExistingAsync(
                await db.TrackerImports.AsNoTracking().Where(i => i.SupersedesImportId == previous.Id).Select(i => i.Id).FirstAsync(cancellationToken),
                cancellationToken);
        }

        LogQueued(logger, projectId, import.Id, import.Provider);
        return await DtoAsync(import, cancellationToken);
    }

    /// <summary>
    /// FR-173: removes an import. A completed one writes a new <c>source_items</c> version without it (earlier versions stay readable);
    /// one still queued or running is dropped by its job. <c>SOURCE_ITEMS_REMOVED</c> records it, with no item body.
    /// </summary>
    public async Task<Result> RemoveAsync(Guid projectId, Guid importId, Guid userId, string actor, CancellationToken cancellationToken)
    {
        var writable = await WritableAsync(projectId, cancellationToken);
        if (writable.IsFailure)
        {
            return Result.Failure(writable.Error!);
        }

        var import = await db.TrackerImports.FirstOrDefaultAsync(i => i.Id == importId && i.ProjectId == projectId && i.ReleaseVersionId == writable.Value.CurrentReleaseVersionId, cancellationToken);
        if (import is null || import.Status is TrackerImportStatus.Superseded or TrackerImportStatus.Removed)
        {
            return Result.Failure(new DomainError(ErrorCodes.NotFound, "The import was not found."));
        }

        var now = time.GetUtcNow();
        var wasActive = import.Status == TrackerImportStatus.Completed;
        var alsoRemoved = TrackerImportChain.AlsoRemoved(await LinksAsync(import.ReleaseVersionId, cancellationToken), importId);
        import.Status = TrackerImportStatus.Removed;
        if (alsoRemoved.Count > 0)
        {
            foreach (var related in await db.TrackerImports.Where(i => alsoRemoved.Contains(i.Id)).ToListAsync(cancellationToken))
            {
                related.Status = TrackerImportStatus.Removed;
            }
        }

        Artifact? artifact = null;
        if (wasActive)
        {
            var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
            var active = await db.TrackerImports.AsNoTracking()
                .Where(i => i.ReleaseVersionId == import.ReleaseVersionId && i.Status == TrackerImportStatus.Completed && i.Id != importId)
                .ToListAsync(cancellationToken);
            var rendered = SourceItemsRenderer.Render(active.Select(SourceSnapshotWriter.ToSnapshot));
            artifact = await snapshots.AddAsync(project, import.ReleaseVersionId, rendered.Markdown, userId, now, cancellationToken);
        }

        db.AddEvent(
            EventTypes.SourceItemsRemoved,
            projectId,
            new { importId, provider = import.Provider.ToWire(), itemCount = import.ItemCount, releaseVersionId = import.ReleaseVersionId, artifactId = artifact?.Id, version = artifact?.Version },
            actor,
            now);
        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        return saved;
    }

    /// <summary>
    /// FR-173: per-item drift, comparing <c>updatedAt</c>, using the importer's connection, else the caller's own on that tracker, else
    /// <c>unknown</c>. Results of a full check are cached for 5 minutes; the whole check is capped at 10 seconds, with <c>unknown</c> for what
    /// remains (§8). Never changes a snapshot.
    /// </summary>
    public async Task<Result<SourcesCheckDto>> CheckAsync(Guid projectId, Guid callerId, CancellationToken cancellationToken)
    {
        var versionId = await db.CurrentReleaseVersionIdAsync(projectId, cancellationToken);
        var active = await db.TrackerImports
            .Where(i => i.ProjectId == projectId && i.ReleaseVersionId == versionId && i.Status == TrackerImportStatus.Completed)
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        var items = new List<SourceDriftItemDto>();
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cap.CancelAfter(SourceItemDriftRules.CheckTimeout);
        foreach (var import in active)
        {
            IReadOnlyList<SourceItemDriftResult> drift;
            if (SourceItemDriftRules.IsCacheFresh(import.DriftCheckedAt, now) && import.Drift is { } cached)
            {
                drift = cached;
            }
            else
            {
                var current = await CurrentItemsAsync(import, callerId, cap.Token, cancellationToken);
                drift = SourceItemDriftRules.Compare(import.Items, current);
                if (current is not null)
                {
                    import.Drift = drift;
                    import.DriftCheckedAt = now;
                }
            }

            var byKey = drift.ToDictionary(d => d.Key, d => d.Drift, StringComparer.Ordinal);
            items.AddRange(import.Items.Select(i => new SourceDriftItemDto(import.Id, import.Provider, i.Key, i.Title, i.Url, byKey.GetValueOrDefault(i.Key, SourceItemDrift.Unknown))));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The import changed meanwhile (a removal, a concurrent check): the cache isn't worth a failure.
            db.ChangeTracker.Clear();
        }

        var hash = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == versionId && a.Type == ArtifactType.SourceItems && a.Status == ArtifactStatus.Locked)
            .OrderByDescending(a => a.Version)
            .Select(a => a.ContentHash)
            .FirstOrDefaultAsync(cancellationToken);
        return Result.Success(new SourcesCheckDto(items, hash, now));
    }

    /// <summary>The import's items in the tracker now, or <c>null</c> (no usable connection, or the tracker didn't answer in time).</summary>
    private async Task<IReadOnlyList<SourceItem>?> CurrentItemsAsync(TrackerImport import, Guid callerId, CancellationToken capped, CancellationToken cancellationToken)
    {
        var selection = new TrackerSelection { Keys = [.. import.Items.Select(i => i.Key)] };
        var options = new TrackerImportOptions(IncludeChildren: false);
        var repository = selection.Keys.Count > 0 ? TrackerRules.RepositoryOf(selection.Keys[0]) : null;

        // FR-173: "the importer left": someone no longer on the project isn't read through.
        var importerStays = await db.ProjectMembers.AsNoTracking().AnyAsync(m => m.ProjectId == import.ProjectId && m.UserId == import.CreatedBy, cancellationToken);
        var candidates = new List<(Guid User, Guid? Connection)>();
        if (importerStays)
        {
            candidates.Add((import.CreatedBy, import.PlatformConnectionId ?? import.GitConnectionId));
        }

        if (callerId != import.CreatedBy || !importerStays)
        {
            candidates.Add((callerId, null));
        }

        foreach (var (user, connection) in candidates)
        {
            var resolved = await connections.ResolveAsync(user, import.Provider, connection, cancellationToken);
            if (resolved.IsFailure)
            {
                continue;
            }

            try
            {
                var reading = await connections.ForReadingAsync(resolved.Value, repository, capped);
                return await trackers.ForProvider(import.Provider).GetItemsAsync(reading.Credential, selection, options, capped);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (TrackerApiException ex)
            {
                LogCheckFailed(logger, import.Id, ex.Failure, ex.Status);
            }
        }

        return null;
    }

    private async Task<List<TrackerImportLink>> LinksAsync(Guid releaseVersionId, CancellationToken cancellationToken) =>
        await db.TrackerImports.AsNoTracking()
            .Where(i => i.ReleaseVersionId == releaseVersionId)
            .Select(i => new TrackerImportLink(i.Id, i.SupersedesImportId, i.Status))
            .ToListAsync(cancellationToken);

    private async Task<Result<SourceImportDto>> ExistingAsync(Guid importId, CancellationToken cancellationToken) =>
        await DtoAsync(await db.TrackerImports.AsNoTracking().FirstAsync(i => i.Id == importId, cancellationToken), cancellationToken);

    private async Task<SourceDraftStateDto?> DraftStateAsync(Guid projectId, Guid? versionId, SourceSnapshotDto? snapshot, CancellationToken cancellationToken)
    {
        var draft = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.ReleaseVersionId == versionId && a.Type == ArtifactType.ProjectRequest)
            .OrderByDescending(a => a.Version)
            .Select(a => new { a.Id, a.Version, a.AgentRunId, a.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (draft is null)
        {
            return null;
        }

        int? builtFrom = null;
        if (draft.AgentRunId is { } runId)
        {
            var inputs = await db.AgentRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.InputArtifacts).FirstOrDefaultAsync(cancellationToken);
            builtFrom = inputs?.FirstOrDefault(i => i.Type == ArtifactType.SourceItems)?.Version;
        }

        return new SourceDraftStateDto(
            draft.Id,
            draft.Version,
            builtFrom,
            SourceItemDriftRules.IsDraftBehind(snapshot?.Version, builtFrom, draft.AgentRunId is not null, draft.CreatedAt, snapshot?.CreatedAt));
    }

    private async Task<Result<Project>> WritableAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project?.CurrentReleaseVersionId is null)
        {
            return Result.Failure<Project>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        var phase = TrackerRules.CanChangeImports(project.CurrentPhase);
        return phase.IsFailure ? Result.Failure<Project>(phase.Error!) : Result.Success(project);
    }

    private async Task<Result<SourceImportDto>> DtoAsync(TrackerImport import, CancellationToken cancellationToken)
    {
        var names = await ActorResolver.NamesAsync(db, [import.CreatedBy], cancellationToken);
        return Result.Success(ToDto(import, names));
    }

    private static SourceImportDto ToDto(TrackerImport i, Dictionary<Guid, string> names) => new(
        i.Id,
        i.ReleaseVersionId,
        i.Provider,
        i.PlatformConnectionId ?? i.GitConnectionId,
        i.Status,
        i.ErrorCode,
        i.ErrorMessage,
        i.Query.ToDto(),
        i.Options.ToDto(),
        i.ItemCount,
        i.RenderedBytes,
        [.. i.Items.Select(x => x.ToSummary())],
        i.SourceArtifactId,
        new ActorDto(i.CreatedBy, names.GetValueOrDefault(i.CreatedBy, ActorResolver.DeletedUserName), false),
        i.CreatedAt,
        i.CompletedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId}: tracker import {ImportId} ({Provider}) queued")]
    private static partial void LogQueued(ILogger logger, Guid projectId, Guid importId, TrackerProvider provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "A drift check of import {ImportId} failed: {Failure} (HTTP {StatusCode})")]
    private static partial void LogCheckFailed(ILogger logger, Guid importId, TrackerFailure failure, int? statusCode);
}

/// <summary>FR-170: adds a locked <c>source_items</c> version to the unit of work. Numbering is project-wide, as for every type; the project's <c>xmin</c> guards it.</summary>
public sealed class SourceSnapshotWriter(AideDbContext db)
{
    public async Task<Artifact> AddAsync(Project project, Guid releaseVersionId, string markdown, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.UpdatedAt = now;
        var existing = await db.Artifacts.AsNoTracking()
            .Where(a => a.ProjectId == project.Id && a.Type == ArtifactType.SourceItems)
            .Select(a => new ArtifactVersion(a.Version, a.Status))
            .ToListAsync(cancellationToken);
        var plan = ArtifactRules.NewDraft(existing);
        var artifact = new Artifact
        {
            ProjectId = project.Id,
            Type = ArtifactType.SourceItems,
            Version = plan.Version,
            ParentVersion = plan.ParentVersion,
            Content = markdown,
            ContentHash = ContentHash.Compute(markdown),
            Status = ArtifactRules.InitialStatus(ArtifactType.SourceItems),
            CheckpointAtCreation = project.Checkpoint,
            ReleaseVersionId = releaseVersionId,
            CreatedByUserId = userId,
            ApprovedAt = now,
            CreatedAt = now,
        };
        db.Artifacts.Add(artifact);
        return artifact;
    }

    public static SourceImportSnapshot ToSnapshot(TrackerImport import)
    {
        ArgumentNullException.ThrowIfNull(import);
        return new SourceImportSnapshot(import.Id, import.Provider, import.CreatedAt, import.Items);
    }
}
