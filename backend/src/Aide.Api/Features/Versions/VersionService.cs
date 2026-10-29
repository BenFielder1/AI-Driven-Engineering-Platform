using Aide.Api.Features.Activity;
using Aide.Api.Features.Lifecycle;
using Aide.Api.Features.Stack;
using Aide.Api.Jobs;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.Domain.Rollback;
using Aide.Domain.Stack;
using Aide.Infrastructure.Git;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Security;
using Aide.Infrastructure.Stack;
using Microsoft.EntityFrameworkCore;

namespace Aide.Api.Features.Versions;

/// <summary>Payload of the <see cref="JobTypes.RepositoryAnalysis"/> job (FR-70): the project and the version to analyse.</summary>
public sealed record RepositoryAnalysisJobPayload(Guid ProjectId, Guid ReleaseVersionId);

/// <summary>
/// §4.10, FR-60–FR-63: release versions — listing them, their checkpoints, and starting a new version. The rules live in
/// <see cref="ReleaseVersionRules"/> and the lifecycle state machine; this service loads the facts and writes the outcome
/// (version row, project state, transition, event, analyst job) in one transaction guarded by the project's <c>xmin</c>.
/// </summary>
public sealed partial class VersionService(
    AideDbContext db,
    LifecycleWriter writer,
    JobEnqueuer jobs,
    IGitProviderResolver providers,
    ISecretProtector secrets,
    IStackCatalog catalog,
    ProjectStackService stacks,
    TimeProvider time,
    ILogger<VersionService> logger)
{
    /// <summary>The longest request text kept in the <c>VERSION_STARTED</c> event payload.</summary>
    public const int EventRequestMaxLength = 500;

    /// <summary>
    /// FR-60, FR-61, FR-117: starts <paramref name="request"/>'s version. Checks, in order: <c>VERSION_INVALID</c>, <c>VERSION_IN_PROGRESS</c>, <c>RELEASE_NOT_MERGED</c>, <c>NOT_RELEASED</c>,
    /// <c>VERSION_NOT_GREATER</c>, then <c>TAG_EXISTS</c> through the Git provider (<c>UPSTREAM_ERROR</c> when the repository
    /// can't be read). On success: the version is <c>in_development</c> (base = the released tag and merge commit, branch
    /// <c>aide/&lt;slug&gt;/v&lt;version&gt;</c>), the project is in refinement / active at checkpoint 1 with the request as its idea,
    /// <c>VERSION_STARTED</c> is recorded and the Repository Analyst job is queued. The stack is the sent composition or the
    /// base version's re-pinned to the current catalog (FR-129: no preset is ever pinned).
    /// </summary>
    public async Task<Result<ReleaseVersion>> StartAsync(Guid projectId, StartReleaseVersionRequest request, Guid userId, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return Fail(ErrorCodes.NotFound, "The project was not found.");
        }

        // FR-117: a sent stack is validated (R5 against the repository's host) before anything else about the version.
        var repoVcs = await stacks.RepoVcsAsync(projectId, cancellationToken);
        var repoHost = await stacks.RepoHostAsync(projectId, cancellationToken);
        StackComposition? sentStack = null;
        if (request.Stack is { } stackRequest)
        {
            var resolved = stacks.Resolve(stackRequest, repoHost, "stack.");
            if (resolved.IsFailure)
            {
                return Result.Failure<ReleaseVersion>(resolved.Error!);
            }

            sentStack = resolved.Value;
        }

        var versions = await db.ReleaseVersions.Where(v => v.ProjectId == projectId).ToListAsync(cancellationToken);
        var rows = versions.Select(v => new ReleaseVersionRow(v.Version, v.Status)).ToList();
        var allowed = ReleaseVersionRules.CanStart(project.Status, rows, request.Version);
        if (allowed.IsFailure)
        {
            return Result.Failure<ReleaseVersion>(allowed.Error!);
        }

        var version = allowed.Value;
        var transition = LifecycleStateMachine.Apply(
            new LifecycleState(project.CurrentPhase, project.Status, project.Checkpoint),
            TransitionRequest.NewVersion($"Started version {version}"),
            new LifecycleSnapshot { DevelopmentVersionExists = ReleaseVersionRules.DevelopmentVersion(rows) is not null });
        if (transition.IsFailure)
        {
            return Result.Failure<ReleaseVersion>(transition.Error!);
        }

        var tag = ReleaseVersionRules.Tag(project.TagPrefix, version);
        var lookup = await FindTagAsync(projectId, tag, cancellationToken);
        if (lookup.IsFailure)
        {
            return Result.Failure<ReleaseVersion>(lookup.Error!);
        }

        if (lookup.Value)
        {
            return Fail(ErrorCodes.TagExists, $"The tag {tag} already exists in the repository. Choose another version.");
        }

        // The base is the released version the new one builds on: the highest released (or imported) version.
        var released = ReleaseVersionRules.ReleasedVersion(rows);
        var baseVersion = released is null
            ? null
            : versions.FirstOrDefault(v => ReleaseVersionRules.IsReleased(v.Status) && SemVer.TryParse(v.Version, out var parsed) && parsed == released);

        var now = time.GetUtcNow();
        var cleanRequest = request.Request.Trim();
        var started = new ReleaseVersion
        {
            ProjectId = project.Id,
            Version = version.ToString(),
            Status = ReleaseVersionStatus.InDevelopment,
            TagPrefix = project.TagPrefix,
            BaseTag = baseVersion?.Tag,
            BaseCommitSha = baseVersion?.MergeCommitSha,
            // FR-82: the branch (aide/<slug>/v<version>) is stored when the working copy is first prepared: a stored
            // branch_name is the "working copy prepared" fact the lifecycle snapshot reads (§4.1).
            Request = cleanRequest,
            StartedBy = userId,
            CreatedAt = now,
        };
        // FR-117: the sent stack, else the base version's composition re-pinned to the current catalog (FR-129: a version
        // migrated from a preset has its equivalent composition, so it is inherited like any other).
        var stack = sentStack;
        if (stack is null && StackCompositionJson.Deserialize(baseVersion?.StackComposition) is { } inheritedStack)
        {
            var repinned = stacks.Repin(inheritedStack, repoVcs);
            if (repinned.IsFailure)
            {
                return Result.Failure<ReleaseVersion>(repinned.Error!);
            }

            stack = repinned.Value;
        }

        ProjectStackService.Pin(started, stack);
        stacks.SyncPacks(project, stack);
        db.ReleaseVersions.Add(started);
        versions.Add(started);

        project.CurrentReleaseVersionId = started.Id;
        project.Idea = cleanRequest;
        ReleaseVersionScope.Refresh(project, versions);
        await writer.ApplyAsync(project, transition.Value, actor, now, cancellationToken);

        db.AddEvent(
            EventTypes.VersionStarted,
            project.Id,
            new
            {
                releaseVersionId = started.Id,
                version = started.Version,
                baseTag = started.BaseTag,
                baseCommitSha = started.BaseCommitSha,
                branchName = ReleaseVersionRules.BranchName(project.Slug, project.Id, version),
                stackPacks = project.StackPacks,
                catalogVersion = started.CatalogVersion,
                request = cleanRequest.Length > EventRequestMaxLength ? cleanRequest[..EventRequestMaxLength] : cleanRequest,
            },
            actor,
            now);
        await jobs.EnqueueAsync(JobTypes.RepositoryAnalysis, new RepositoryAnalysisJobPayload(project.Id, started.Id), project.Id, cancellationToken, now);

        var saved = await LifecyclePersistence.SaveAsync(db, cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<ReleaseVersion>(saved.Error!);
        }

        LogStarted(logger, project.Id, started.Version, started.BaseTag);
        return Result.Success(started);
    }

    /// <summary>FR-62: the project's release versions, highest version first (unparseable migrated rows last, newest first).</summary>
    public async Task<IReadOnlyList<ReleaseVersionDto>> ListAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Checkpoint, p.CurrentReleaseVersionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return [];
        }

        var versions = await db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == projectId).ToListAsync(cancellationToken);
        var checkpoints = await CheckpointsAsync(projectId, cancellationToken);
        var actors = await ActorResolver.ForActorsAsync(db, versions.Where(v => v.StartedBy is not null).Select(v => v.StartedBy!.Value.ToString()), cancellationToken);
        return
        [
            .. versions
                .OrderByDescending(v => SemVer.TryParse(v.Version, out var parsed) ? parsed : null)
                .ThenByDescending(v => v.CreatedAt)
                .Select(v => ToDto(v, Checkpoints(v.Id, project.CurrentReleaseVersionId, project.Checkpoint, checkpoints), actors, catalog)),
        ];
    }

    /// <summary>One release version as a DTO (with its checkpoints), or <c>null</c>.</summary>
    public async Task<ReleaseVersionDto?> GetAsync(Guid projectId, Guid versionId, CancellationToken cancellationToken) =>
        (await ListAsync(projectId, cancellationToken)).FirstOrDefault(v => v.Id == versionId);

    /// <summary>
    /// FR-44, FR-63: the checkpoints of <paramref name="releaseVersionId"/> (default: the project's current version), newest
    /// first, with whether each is a rollback target now. Every checkpoint of a version that isn't in development is blocked
    /// (<c>version_released</c>); <c>null</c> for an unknown project or a version of another project.
    /// </summary>
    public async Task<Result<IReadOnlyList<CheckpointDto>>> CheckpointListAsync(Guid projectId, Guid? releaseVersionId, CancellationToken cancellationToken)
    {
        var scope = await ReleaseVersionScope.ResolveAsync(db, projectId, releaseVersionId, cancellationToken);
        if (scope.IsFailure)
        {
            return Result.Failure<IReadOnlyList<CheckpointDto>>(scope.Error!);
        }

        var project = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Checkpoint, p.CurrentPhase, p.CurrentReleaseVersionId, p.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (project is null || scope.Value is not { } versionId)
        {
            return Result.Failure<IReadOnlyList<CheckpointDto>>(new DomainError(ErrorCodes.NotFound, "The project was not found."));
        }

        var version = await db.ReleaseVersions.AsNoTracking().FirstAsync(v => v.Id == versionId, cancellationToken);
        var transitions = await db.StateTransitions.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.ReleaseVersionId == versionId)
            .Select(t => new RecordedTransition(t.Checkpoint, t.ToPhase, t.CreatedAt))
            .ToListAsync(cancellationToken);
        var isCurrent = versionId == project.CurrentReleaseVersionId;
        var closed = !ReleaseVersionRules.IsDevelopment(version.Status);
        var current = isCurrent ? project.Checkpoint : transitions.OrderBy(t => t.CreatedAt).Select(t => t.ProjectVersion).LastOrDefault(1);
        var phase = isCurrent
            ? project.CurrentPhase
            : RollbackRules.RestoredPhase(transitions, current) ?? Phase.Release;
        return Result.Success<IReadOnlyList<CheckpointDto>>(
        [
            .. RollbackRules.Versions(transitions, current, phase, version.CreatedAt, versionReleased: closed)
                .Select(v => new CheckpointDto(
                    v.Version,
                    v.Phase,
                    v.EnteredAt,
                    v.RollbackAllowed,
                    v.Block switch
                    {
                        // A closed version blocks every checkpoint, the latest included.
                        VersionBlock.CurrentVersion when closed => CheckpointBlock.VersionReleased,
                        VersionBlock.CurrentVersion => CheckpointBlock.CurrentCheckpoint,
                        VersionBlock.ReleasePhase => CheckpointBlock.ReleasePhase,
                        VersionBlock.VersionReleased => CheckpointBlock.VersionReleased,
                        _ => null,
                    })),
        ]);
    }

    /// <summary>Whether <paramref name="tag"/> exists in the project's repository (FR-61), through its Git provider.</summary>
    private async Task<Result<bool>> FindTagAsync(Guid projectId, string tag, CancellationToken cancellationToken)
    {
        var settings = await db.ProjectGitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);
        if (settings is null)
        {
            return Result.Success(false);
        }

        string? token = null;
        if (settings.TokenEncrypted is { } encrypted && !secrets.TryUnprotect(encrypted, out token))
        {
            return Result.Failure<bool>(new DomainError(ErrorCodes.UpstreamError, "The saved Git token can't be decrypted, so the repository's tags can't be checked. An Owner needs to save it again."));
        }

        var lookup = await providers.Resolve(settings.Provider)
            .FindTagAsync(GitConnectionSettings.From(settings), tag, token, cancellationToken);
        if (!lookup.Ok)
        {
            LogTagLookupFailed(logger, projectId, settings.Provider);
            return Result.Failure<bool>(new DomainError(ErrorCodes.UpstreamError, $"The repository's tags could not be checked: {lookup.Reason}"));
        }

        return Result.Success(lookup.Exists);
    }

    private async Task<Dictionary<Guid, (int Latest, int Max)>> CheckpointsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var transitions = await db.StateTransitions.AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Select(t => new { t.ReleaseVersionId, t.Checkpoint, t.CreatedAt, t.Id })
            .ToListAsync(cancellationToken);
        return transitions
            .GroupBy(t => t.ReleaseVersionId)
            .ToDictionary(
                g => g.Key,
                g => (g.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Last().Checkpoint, g.Max(t => t.Checkpoint)));
    }

    private static ReleaseVersionCheckpointsDto Checkpoints(Guid versionId, Guid? currentVersionId, int projectCheckpoint, Dictionary<Guid, (int Latest, int Max)> recorded)
    {
        var (latest, max) = recorded.TryGetValue(versionId, out var found) ? found : (1, 1);
        var current = versionId == currentVersionId ? projectCheckpoint : latest;
        return new ReleaseVersionCheckpointsDto(current, Math.Max(max, current));
    }

    internal static ReleaseVersionDto ToDto(ReleaseVersion v, ReleaseVersionCheckpointsDto checkpoints, Func<string, ActorDto> actors, IStackCatalog catalog) => new(
        v.Id,
        v.Version,
        v.Status,
        v.Tag,
        v.TagPrefix,
        v.BaseTag,
        v.BaseCommitSha,
        v.MergeCommitSha,
        v.BranchName,
        v.PullRequestUrl,
        checkpoints,
        v.PresetId,
        v.PresetVersion,
        Stack.RetiredPresets.Describe(catalog, v.PresetId, v.PresetVersion).PresetName,
        v.Request,
        v.StartedBy is { } startedBy ? actors(startedBy.ToString()) : null,
        v.CreatedAt,
        v.ReleasedAt);

    private static Result<ReleaseVersion> Fail(string code, string message) => Result.Failure<ReleaseVersion>(new DomainError(code, message));

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} started version {Version} (base {BaseTag})")]
    private static partial void LogStarted(ILogger logger, Guid projectId, string version, string? baseTag);


    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking the release tags of project {ProjectId} ({Provider}) failed")]
    private static partial void LogTagLookupFailed(ILogger logger, Guid projectId, GitProvider provider);
}

public static class VersionServiceCollectionExtensions
{
    /// <summary>§4.10: the versions service and the Repository Analyst job (FR-70).</summary>
    public static IServiceCollection AddReleaseVersions(this IServiceCollection services)
    {
        services.AddScoped<VersionService>();
        services.AddJobHandler<Agents.RepositoryAnalysisJobHandler>(JobTypes.RepositoryAnalysis);
        return services;
    }
}
