using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Domain.Trackers;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Trackers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aide.Api.Features.Trackers;

/// <summary>
/// FR-165, FR-169: the Issue trackers section and the browse dialog: which trackers are ready, containers and search, each proxying the
/// provider with the caller's own connection and <c>Trackers:SearchTimeout</c> (§8).
/// </summary>
public sealed partial class TrackerBrowseService(
    AideDbContext db,
    TrackerConnectionResolver connections,
    Features.GitHub.GitHubConnections github,
    IGitHubAppStore apps,
    GitHubApi gitHubApi,
    IGitLabInstances instances,
    IIssueTrackers trackers,
    IOptions<TrackerOptions> options,
    ILogger<TrackerBrowseService> logger)
{
    public async Task<IReadOnlyList<TrackerStatusDto>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var platform = await db.PlatformConnections.AsNoTracking()
            .Where(c => c.UserId == userId && (c.Provider == PlatformProvider.Jira || c.Provider == PlatformProvider.Linear))
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .ToListAsync(cancellationToken);
        return
        [
            Platform(TrackerProvider.Jira, PlatformProvider.Jira, platform),
            Platform(TrackerProvider.Linear, PlatformProvider.Linear, platform),
            await GitHubAsync(userId, cancellationToken),
            await GitLabAsync(userId, cancellationToken),
        ];
    }

    /// <summary>§6: <c>TRACKER_NOT_CONNECTED</c>, <c>TRACKER_PERMISSION_MISSING</c>, <c>UPSTREAM_RATE_LIMITED</c>.</summary>
    public Task<Result<TrackerContainerPageDto>> ContainersAsync(Guid userId, string actor, TrackerProvider provider, Guid? connectionId, string? search, string? cursor, CancellationToken cancellationToken) =>
        CallAsync(userId, actor, provider, connectionId, null, reading: false, async (tracker, resolved, token) =>
        {
            var page = await tracker.ListContainersAsync(resolved.Credential, search, cursor, token);
            return new TrackerContainerPageDto([.. page.Items.Select(c => new TrackerContainerDto(c.Id, c.Name, c.Detail, c.Url))], page.NextCursor);
        }, cancellationToken);

    /// <summary>§6: as containers, plus <c>TRACKER_QUERY_INVALID</c> with the tracker's message; a native query on Linear or GitLab is <c>VALIDATION</c>.</summary>
    public async Task<Result<TrackerSearchResultDto>> SearchAsync(Guid userId, string actor, TrackerProvider provider, TrackerSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection = new TrackerSelection { Query = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim(), ContainerId = request.ContainerId, Filter = request.Filter.ToDomain() };
        if (selection.Query is null && selection.Filter is null && selection.ContainerId is null)
        {
            selection = selection with { Filter = new TrackerFilter() };
        }

        var valid = TrackerRules.ValidateSelection(provider, selection, prefix: string.Empty);
        if (valid.IsFailure)
        {
            return Result.Failure<TrackerSearchResultDto>(valid.Error!);
        }

        return await CallAsync(userId, actor, provider, request.ConnectionId, request.ContainerId, reading: true, async (tracker, resolved, token) =>
        {
            var page = await tracker.SearchAsync(resolved.Credential, new TrackerSearch(selection.ContainerId, selection.Filter, selection.Query, request.Cursor), token);
            return new TrackerSearchResultDto([.. page.Items.Select(i => i.ToSummary())], page.NextCursor);
        }, cancellationToken);
    }

    private async Task<Result<T>> CallAsync<T>(
        Guid userId,
        string actor,
        TrackerProvider provider,
        Guid? connectionId,
        string? repository,
        bool reading,
        Func<IIssueTrackerProvider, ResolvedTracker, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        var resolved = await connections.ResolveAsync(userId, provider, connectionId, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result.Failure<T>(resolved.Error!);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.SearchTimeout);
        try
        {
            var usable = reading ? await connections.ForReadingAsync(resolved.Value, repository, timeout.Token) : resolved.Value;
            return Result.Success(await call(trackers.ForProvider(provider), usable, timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<T>(TrackerRules.ToError(provider, TrackerFailure.Upstream));
        }
        catch (TrackerApiException ex)
        {
            LogFailed(logger, provider, ex.Failure, ex.Status);
            if (ex.Failure == TrackerFailure.Rejected)
            {
                await connections.OnRejectedAsync(resolved.Value, null, actor, cancellationToken);
            }

            var retryAfter = ex.RetryAt is { } at ? at - TimeProvider.System.GetUtcNow() : (TimeSpan?)null;
            var link = ex.Link ?? (resolved.Value.GitHubSession is { } session ? session.App.HtmlUrl ?? GitHubRules.AppHtmlUrl(session.App.Slug) : null);
            return Result.Failure<T>(TrackerRules.ToError(provider, ex.Failure, ex.Message, retryAfter, link));
        }
    }

    private static TrackerStatusDto Platform(TrackerProvider tracker, PlatformProvider kind, List<PlatformConnection> rows)
    {
        var mine = rows.Where(c => c.Provider == kind).ToList();
        var ready = mine.Any(c => c.Status is PlatformConnectionStatus.Valid or PlatformConnectionStatus.Unchecked);
        return new TrackerStatusDto(
            tracker,
            TrackerRules.DisplayName(tracker),
            ready,
            ready ? null : TrackerReadiness.NotConnected,
            null,
            [.. mine.Select(c => new TrackerConnectionSummaryDto(c.Id, c.Label, c.Status.ToWire(), c.AccountName, c.Metadata.Site ?? c.Metadata.Organization))]);
    }

    /// <summary>FR-167: ready with a connection and an App whose installations report Issues; an unreadable answer doesn't block.</summary>
    private async Task<TrackerStatusDto> GitHubAsync(Guid userId, CancellationToken cancellationToken)
    {
        var name = TrackerRules.DisplayName(TrackerProvider.Github);
        var connection = await db.GitHubConnections.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        IReadOnlyList<TrackerConnectionSummaryDto> summaries = connection is null
            ? []
            : [new TrackerConnectionSummaryDto(userId, connection.Login, connection.Status == GitHubConnectionStatus.Valid ? "valid" : "invalid", connection.Login, connection.Login)];
        var valid = connection?.Status == GitHubConnectionStatus.Valid;
        if (trackers.UsesFake)
        {
            return new TrackerStatusDto(TrackerProvider.Github, name, valid, valid ? null : TrackerReadiness.NotConnected, null, summaries);
        }

        var app = await apps.GetAsync(cancellationToken);
        if (app is null)
        {
            return new TrackerStatusDto(TrackerProvider.Github, name, false, TrackerReadiness.AppNotRegistered, null, summaries);
        }

        var settings = app.HtmlUrl ?? GitHubRules.AppHtmlUrl(app.Slug);
        if (!valid)
        {
            return new TrackerStatusDto(TrackerProvider.Github, name, false, TrackerReadiness.NotConnected, null, summaries);
        }

        try
        {
            var session = await github.SessionAsync(userId, cancellationToken);
            if (session.IsSuccess)
            {
                var installations = await gitHubApi.ListUserInstallationsAsync(session.Value.Token, cancellationToken);
                if (installations.Count > 0 && installations.All(i => GitHubRules.IssuesPermission(i.Permissions) != GitHubPagesAccess.Granted))
                {
                    return new TrackerStatusDto(TrackerProvider.Github, name, false, TrackerReadiness.IssuesPermissionMissing, settings, summaries);
                }
            }
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubRateLimitedException)
        {
            LogStatusUnreadable(logger);
        }

        return new TrackerStatusDto(TrackerProvider.Github, name, true, null, null, summaries);
    }

    /// <summary>FR-168: GitLab needs nothing beyond a valid connection on a registered instance.</summary>
    private async Task<TrackerStatusDto> GitLabAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.GitConnections.AsNoTracking()
            .Where(c => c.UserId == userId && c.Provider == GitHostKind.Gitlab)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .ToListAsync(cancellationToken);
        var known = rows.Count == 0 ? [] : await instances.ListAsync(cancellationToken);
        var ready = rows.Any(c => c.Status == GitConnectionStatus.Valid);
        return new TrackerStatusDto(
            TrackerProvider.Gitlab,
            TrackerRules.DisplayName(TrackerProvider.Gitlab),
            ready,
            ready ? null : TrackerReadiness.NotConnected,
            null,
            [.. rows.Select(c => new TrackerConnectionSummaryDto(c.Id, c.Login, c.Status.ToWire(), c.Login, known.FirstOrDefault(i => i.Id == c.InstanceId)?.Name))]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A {Provider} tracker request failed: {Failure} (HTTP {StatusCode})")]
    private static partial void LogFailed(ILogger logger, TrackerProvider provider, TrackerFailure failure, int? statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "The GitHub App's installations couldn't be read for the tracker status")]
    private static partial void LogStatusUnreadable(ILogger logger);
}

/// <summary>§6 <c>GET /api/me/trackers</c>: why a tracker isn't ready.</summary>
public static class TrackerReadiness
{
    public const string NotConnected = "not_connected";
    public const string AppNotRegistered = "app_not_registered";
    public const string IssuesPermissionMissing = "issues_permission_missing";
}
