using System.Globalization;
using System.Text;
using System.Text.Json;
using Aide.Domain.GitHub;
using Aide.Domain.Trackers;
using Aide.Infrastructure.GitHub;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Platforms;

namespace Aide.Infrastructure.Trackers;

/// <summary>
/// FR-167: the token that reads a repository's issues. The user's token decides which repositories they can see; reading goes through
/// the installation token of an installation that token can see. No Issues permission on it is
/// <c>TRACKER_PERMISSION_MISSING</c>, with the App settings link. A project's PAT is never used.
/// </summary>
public sealed class GitHubTrackerAccess(GitHubApi api, IGitHubInstallationTokens tokens, TimeProvider time)
{
    public async Task<string> ReadTokenAsync(string userToken, GitHubAppCredentials app, string owner, string repo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        try
        {
            var repository = await api.FindRepositoryAsync(userToken, owner, repo, cancellationToken)
                ?? throw new TrackerApiException(TrackerFailure.NotFound, 404, "GitHub couldn't find that repository, or your account can't see it.");
            var visible = await api.ListUserInstallationsAsync(userToken, cancellationToken);
            var jwt = GitHubAppJwt.Create(app.AppId, app.PrivateKeyPem, time.GetUtcNow());
            var installation = await api.FindRepoInstallationAsync(jwt, owner, repo, cancellationToken);
            if (installation is not { } id || visible.All(i => i.Id != id))
            {
                throw new TrackerApiException(TrackerFailure.NotConnected, 404, "The Tevrin GitHub App isn't installed on that repository, or your account can't see the installation.");
            }

            var token = await tokens.GetAsync(app, id, repository.Id, cancellationToken);
            return GitHubRules.IssuesPermission(token.Permissions) == GitHubPagesAccess.Granted
                ? token.Token
                : throw PermissionMissing(app);
        }
        catch (GitHubRateLimitedException ex)
        {
            throw GitHubFailures.RateLimited(ex);
        }
        catch (GitHubApiException ex)
        {
            throw GitHubFailures.From(ex);
        }
    }

    /// <summary>FR-167: the failure for an App or installation without the permission, with the App settings link.</summary>
    public static TrackerApiException PermissionMissing(GitHubAppCredentials app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new TrackerApiException(TrackerFailure.AppPermissionMissing, 403, "The Tevrin GitHub App needs the Issues permission.")
        {
            Link = app.HtmlUrl ?? GitHubRules.AppHtmlUrl(app.Slug),
        };
    }
}

internal static class GitHubFailures
{
    public static TrackerApiException RateLimited(GitHubRateLimitedException ex) => new(TrackerFailure.RateLimited, 429, "GitHub is rate limiting requests.", ex.ResetAt);

    public static TrackerApiException From(GitHubApiException ex) => new(
        ex.Kind switch
        {
            GitHubFailureKind.Unauthorized => TrackerFailure.Rejected,
            GitHubFailureKind.Forbidden => TrackerFailure.MissingPermissions,
            GitHubFailureKind.NotFound => TrackerFailure.NotFound,
            GitHubFailureKind.Unprocessable => TrackerFailure.QueryInvalid,
            _ => TrackerFailure.Upstream,
        },
        ex.Status,
        ex.Message);
}

/// <summary>
/// FR-167, §4.26: GitHub Issues through the REST API on the configured GitHub host. A container is <c>owner/repo</c>. Pull requests come back
/// from the issues API and are always excluded. Containers use the user's token; search and fetch use the token from
/// <see cref="GitHubTrackerAccess"/>.
/// </summary>
public sealed class GitHubIssueTrackerProvider(GitHubApi api) : IIssueTrackerProvider
{
    private const int PageSize = 50;
    private const int MaxDepth = 3;
    private const int MaxInstallationRepos = 100;
    private const int CommentsPerPage = 100;

    public TrackerProvider Provider => TrackerProvider.Github;

    public async Task<TrackerTokenCheck> ValidateAsync(TrackerCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var user = await Call(() => api.GetAuthenticatedUserAsync(credential.Token, cancellationToken));
        return new TrackerTokenCheck(user.Id.ToString(CultureInfo.InvariantCulture), user.Login, []);
    }

    /// <summary>The repositories the user's token sees through the App's installations with Issues; the cursor is <c>installationIndex:page</c>.</summary>
    public async Task<TrackerContainerPage> ListContainersAsync(TrackerCredential credential, string? search, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var installations = await Call(() => api.ListUserInstallationsAsync(credential.Token, cancellationToken));
        var readable = installations.Where(i => GitHubRules.IssuesPermission(i.Permissions) == GitHubPagesAccess.Granted).ToList();
        if (installations.Count > 0 && readable.Count == 0)
        {
            throw new TrackerApiException(TrackerFailure.AppPermissionMissing, 403, "The Tevrin GitHub App needs the Issues permission.");
        }

        var parts = (cursor ?? "0:1").Split(':');
        var index = parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var i0) ? i0 : 0;
        var page = parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var p0) ? p0 : 1;
        if (index >= readable.Count)
        {
            return new TrackerContainerPage([], null);
        }

        var (repositories, total) = await Call(() => api.ListInstallationRepositoriesAsync(credential.Token, readable[index].Id, page, MaxInstallationRepos, cancellationToken));
        var items = repositories
            .Where(r => string.IsNullOrWhiteSpace(search) || r.FullName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(r => new TrackerContainer(r.FullName, r.FullName, r.Private ? "private" : "public", r.HtmlUrl))
            .ToList();
        string? next = page * MaxInstallationRepos < total
            ? string.Create(CultureInfo.InvariantCulture, $"{index}:{page + 1}")
            : index + 1 < readable.Count ? string.Create(CultureInfo.InvariantCulture, $"{index + 1}:1") : null;
        return new TrackerContainerPage(items, next);
    }

    public async Task<TrackerItemPage> SearchAsync(TrackerCredential credential, TrackerSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(search);
        var (owner, repo) = SplitRepo(search.ContainerId ?? throw new TrackerApiException(TrackerFailure.NotFound, null, "Choose a repository."));
        var page = int.TryParse(search.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 1;
        return await PageAsync(credential, owner, repo, search.Filter, search.Query, page, cancellationToken);
    }

    public async Task<IReadOnlyList<SourceItem>> GetItemsAsync(TrackerCredential credential, TrackerSelection selection, TrackerImportOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options);
        var byKey = new Dictionary<string, SourceItem>(StringComparer.Ordinal);
        string owner;
        string repo;
        if (selection.Keys is { Count: > 0 } keys)
        {
            (owner, repo) = SplitRepo(TrackerRules.RepositoryOf(keys[0]));
            foreach (var key in keys.Distinct(StringComparer.Ordinal))
            {
                var path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{TrackerRules.IssueNumberOf(key)}";
                using var json = await Call(() => api.GetOrNullAsync(path, credential.Token, cancellationToken));
                if (json is not null && Map(json.RootElement, owner, repo) is { } item)
                {
                    byKey.TryAdd(item.Key, item);
                }
            }
        }
        else
        {
            (owner, repo) = SplitRepo(selection.ContainerId ?? throw new TrackerApiException(TrackerFailure.NotFound, null, "Choose a repository."));
            var page = 1;
            TrackerItemPage items;
            do
            {
                items = await PageAsync(credential, owner, repo, selection.Filter, selection.Query, page++, cancellationToken);
                foreach (var item in items.Items)
                {
                    byKey.TryAdd(item.Key, item);
                }
            }
            while (items.NextCursor is not null && byKey.Count <= SourceItemLimits.MaxItems);
        }

        if (options.IncludeChildren)
        {
            await FetchSubIssuesAsync(credential, owner, repo, byKey, cancellationToken);
        }

        var result = byKey.Values.Take(SourceItemLimits.MaxItems + 1).ToList();
        if (options.IncludeComments && result.Count <= SourceItemLimits.MaxItems)
        {
            for (var i = 0; i < result.Count; i++)
            {
                result[i] = await WithCommentsAsync(credential, owner, repo, result[i], cancellationToken);
            }
        }

        return result;
    }

    public Task<IReadOnlyList<TrackerField>> ListFieldsAsync(TrackerCredential credential, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TrackerField>>([]);

    /// <summary>The sub-issues API, for items that report some (or don't say).</summary>
    private async Task FetchSubIssuesAsync(TrackerCredential credential, string owner, string repo, Dictionary<string, SourceItem> byKey, CancellationToken cancellationToken)
    {
        var frontier = byKey.Values.ToList();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < MaxDepth && frontier.Count > 0 && byKey.Count <= SourceItemLimits.MaxItems; depth++)
        {
            var next = new List<SourceItem>();
            foreach (var parent in frontier.Where(p => visited.Add(p.Key)))
            {
                var page = 1;
                int count;
                do
                {
                    var path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{TrackerRules.IssueNumberOf(parent.Key)}/sub_issues?per_page={PageSize.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}";
                    using var json = await Call(() => api.GetOrNullAsync(path, credential.Token, cancellationToken));
                    var raw = json is null ? [] : Json.Items(json.RootElement).ToList();
                    count = raw.Count;
                    page++;
                    foreach (var child in raw.Select(c => Map(c, owner, repo)).OfType<SourceItem>())
                    {
                        var withParent = child with { ParentKey = parent.Key };
                        if (byKey.TryAdd(child.Key, withParent))
                        {
                            next.Add(withParent);
                        }
                        else if (byKey[child.Key].ParentKey is null)
                        {
                            byKey[child.Key] = withParent;
                        }
                    }
                }
                while (count == PageSize && byKey.Count <= SourceItemLimits.MaxItems);
            }

            frontier = next;
        }
    }

    private async Task<SourceItem> WithCommentsAsync(TrackerCredential credential, string owner, string repo, SourceItem item, CancellationToken cancellationToken)
    {
        var issue = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{TrackerRules.IssueNumberOf(item.Key)}";
        var comments = await CommentPageAsync(credential, issue, 1, item, cancellationToken);
        if (comments.Count < CommentsPerPage)
        {
            return item with { Comments = comments };
        }

        // GitHub lists oldest first and can't sort: the newest are on the last page, and the one before when that page is short of §4.26's 20.
        using var details = await Call(() => api.SendAsync(HttpMethod.Get, issue, credential.Token, null, cancellationToken));
        var total = int.TryParse(Json.Text(details.RootElement, "comments"), NumberStyles.None, CultureInfo.InvariantCulture, out var known) ? known : comments.Count;
        var lastPage = Math.Max(1, (total + CommentsPerPage - 1) / CommentsPerPage);
        if (lastPage > 1)
        {
            comments = await CommentPageAsync(credential, issue, lastPage, item, cancellationToken);
            if (comments.Count < SourceItemLimits.MaxComments)
            {
                comments = [.. await CommentPageAsync(credential, issue, lastPage - 1, item, cancellationToken), .. comments];
            }
        }

        return item with { Comments = comments, CommentsDropped = Math.Max(0, total - comments.Count) };
    }

    private async Task<List<SourceItemComment>> CommentPageAsync(TrackerCredential credential, string issue, int page, SourceItem item, CancellationToken cancellationToken)
    {
        using var json = await Call(() => api.SendAsync(
            HttpMethod.Get,
            $"{issue}/comments?per_page={CommentsPerPage.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}",
            credential.Token,
            null,
            cancellationToken));
        return
        [
            .. Json.Items(json.RootElement).Select(c => new SourceItemComment(
                Json.Text(Json.Property(c, "user"), "login"),
                Json.Time(c, "created_at") ?? item.UpdatedAt,
                Json.Text(c, "body") ?? string.Empty)),
        ];
    }

    /// <summary>The issues API, or <c>/search/issues</c> for a native query or title text (always this repository, always issues).</summary>
    private async Task<TrackerItemPage> PageAsync(TrackerCredential credential, string owner, string repo, TrackerFilter? filter, string? query, int page, CancellationToken cancellationToken)
    {
        var size = PageSize.ToString(CultureInfo.InvariantCulture);
        var number = page.ToString(CultureInfo.InvariantCulture);
        string path;
        var useSearch = !string.IsNullOrWhiteSpace(query) || !string.IsNullOrWhiteSpace(filter?.Text);
        if (useSearch)
        {
            var q = new StringBuilder($"repo:{owner}/{repo} is:issue");
            if (!string.IsNullOrWhiteSpace(query))
            {
                q.Append(' ').Append(query.Trim());
            }
            else
            {
                q.Append(' ').Append(filter!.Text!.Trim()).Append(" in:title");
                if (!TrackerRules.ShowsDone(filter))
                {
                    q.Append(" is:open");
                }

                foreach (var label in filter.Labels ?? [])
                {
                    q.Append(" label:\"").Append(label.Replace("\"", string.Empty, StringComparison.Ordinal)).Append('"');
                }
            }

            path = $"search/issues?q={Uri.EscapeDataString(q.ToString())}&per_page={size}&page={number}";
        }
        else
        {
            var state = TrackerRules.ShowsDone(filter)
                ? (filter!.StatusCategories!.Count > 1 ? "all" : "closed")
                : "open";
            path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues?state={state}&per_page={size}&page={number}";
            if (filter?.Labels is { Count: > 0 } labels)
            {
                path += $"&labels={Uri.EscapeDataString(string.Join(',', labels))}";
            }
        }

        if (filter?.Parent is { Length: > 0 } parent && !useSearch && int.TryParse(parent.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var parentNumber))
        {
            path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{parentNumber.ToString(CultureInfo.InvariantCulture)}/sub_issues?per_page={size}&page={number}";
        }

        using var json = await Call(() => api.SendAsync(HttpMethod.Get, path, credential.Token, null, cancellationToken));
        var raw = Json.Items(json.RootElement, "items").ToList();
        return new TrackerItemPage(
            [.. raw.Select(i => Map(i, owner, repo)).OfType<SourceItem>()],
            raw.Count == PageSize ? (page + 1).ToString(CultureInfo.InvariantCulture) : null);
    }

    /// <summary>§4.26; <c>null</c> for a pull request.</summary>
    private SourceItem? Map(JsonElement issue, string owner, string repo)
    {
        if (issue.ValueKind != JsonValueKind.Object || issue.TryGetProperty("pull_request", out _) || Json.Text(issue, "number") is not { } number)
        {
            return null;
        }

        var labels = Json.Items(issue, "labels").Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : Json.Text(l, "name")).OfType<string>().ToList();
        var type = Json.Text(Json.Property(issue, "type"), "name");
        var state = Json.Text(issue, "state");
        return new SourceItem
        {
            Provider = Provider,
            ExternalId = Json.Text(issue, "id") ?? number,
            Key = $"{owner}/{repo}#{number}",
            Url = Json.Text(issue, "html_url") ?? string.Empty,
            Type = TrackerRules.GitHubType(type, labels),
            NativeType = type ?? "issue",
            Title = Json.Text(issue, "title") ?? string.Empty,
            DescriptionMarkdown = Json.Text(issue, "body") ?? string.Empty,
            Status = state ?? "open",
            StatusCategory = TrackerRules.OpenClosedCategory(state),
            Labels = labels,
            Assignee = Json.Text(Json.Property(issue, "assignee"), "login"),
            UpdatedAt = Json.Time(issue, "updated_at") ?? DateTimeOffset.UnixEpoch,
        };
    }

    private static (string Owner, string Repo) SplitRepo(string container)
    {
        var parts = container.Split('/');
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0
            ? (parts[0], parts[1])
            : throw new TrackerApiException(TrackerFailure.NotFound, null, "A GitHub repository looks like owner/repo.");
    }

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (GitHubRateLimitedException ex)
        {
            throw GitHubFailures.RateLimited(ex);
        }
        catch (GitHubApiException ex)
        {
            throw GitHubFailures.From(ex);
        }
    }
}

/// <summary>
/// FR-168, §4.26: GitLab Issues on the connection's Admin-registered instance, under the 0.4.0 SSRF guard (the <c>gitlab</c> handler). A
/// container is the project's path. Keyset pagination. Epics aren't read: items group by milestone (a <c>milestone:…</c> label) and label.
/// </summary>
public sealed class GitLabIssueTrackerProvider(IGitLabClients clients) : IIssueTrackerProvider
{
    private const int PageSize = 50;

    public TrackerProvider Provider => TrackerProvider.Gitlab;

    public async Task<TrackerTokenCheck> ValidateAsync(TrackerCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var user = await Call(() => Client(credential).GetUserAsync(credential.Token, cancellationToken));
        return new TrackerTokenCheck(user.ExternalId, user.Username, []);
    }

    public async Task<TrackerContainerPage> ListContainersAsync(TrackerCredential credential, string? search, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var page = await Call(() => Client(credential).ListProjectsAsync(credential.Token, search, cursor, cancellationToken));
        return new TrackerContainerPage([.. page.Items.Select(p => new TrackerContainer(p.PathWithNamespace, p.PathWithNamespace, null, p.WebUrl))], page.NextCursor);
    }

    public async Task<TrackerItemPage> SearchAsync(TrackerCredential credential, TrackerSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(search);
        return await PageAsync(credential, search.ContainerId ?? throw new TrackerApiException(TrackerFailure.NotFound, null, "Choose a project."), search.Filter, search.Cursor, cancellationToken);
    }

    public async Task<IReadOnlyList<SourceItem>> GetItemsAsync(TrackerCredential credential, TrackerSelection selection, TrackerImportOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options);
        var byKey = new Dictionary<string, SourceItem>(StringComparer.Ordinal);
        string project;
        if (selection.Keys is { Count: > 0 } keys)
        {
            project = TrackerRules.RepositoryOf(keys[0]);
            foreach (var key in keys.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    using var json = await Call(() => Client(credential).GetJsonDocumentAsync($"{ProjectPath(project)}/issues/{TrackerRules.IssueNumberOf(key)}", credential.Token, cancellationToken));
                    if (Map(json.RootElement, project) is { } item)
                    {
                        byKey.TryAdd(item.Key, item);
                    }
                }
                catch (TrackerApiException ex) when (ex.Failure == TrackerFailure.NotFound)
                {
                    // A key GitLab doesn't return is simply absent.
                }
            }
        }
        else
        {
            project = selection.ContainerId ?? throw new TrackerApiException(TrackerFailure.NotFound, null, "Choose a project.");
            string? cursor = null;
            do
            {
                var page = await PageAsync(credential, project, selection.Filter, cursor, cancellationToken);
                foreach (var item in page.Items)
                {
                    byKey.TryAdd(item.Key, item);
                }

                cursor = page.NextCursor;
            }
            while (cursor is not null && byKey.Count <= SourceItemLimits.MaxItems);
        }

        var result = byKey.Values.Take(SourceItemLimits.MaxItems + 1).ToList();
        if (options.IncludeComments && result.Count <= SourceItemLimits.MaxItems)
        {
            for (var i = 0; i < result.Count; i++)
            {
                result[i] = await WithCommentsAsync(credential, project, result[i], cancellationToken);
            }
        }

        return result;
    }

    public Task<IReadOnlyList<TrackerField>> ListFieldsAsync(TrackerCredential credential, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TrackerField>>([]);

    private async Task<TrackerItemPage> PageAsync(TrackerCredential credential, string project, TrackerFilter? filter, string? cursor, CancellationToken cancellationToken)
    {
        var state = TrackerRules.ShowsDone(filter) ? (filter!.StatusCategories!.Count > 1 ? "all" : "closed") : "opened";
        var path = new StringBuilder(ProjectPath(project))
            .Append("/issues?pagination=keyset&order_by=created_at&sort=asc&per_page=")
            .Append(PageSize.ToString(CultureInfo.InvariantCulture))
            .Append("&state=").Append(state);
        if (filter?.Text is { Length: > 0 } text)
        {
            path.Append("&in=title&search=").Append(Uri.EscapeDataString(text));
        }

        if (filter?.Labels is { Count: > 0 } labels)
        {
            path.Append("&labels=").Append(Uri.EscapeDataString(string.Join(',', labels)));
        }

        if (filter?.Parent is { Length: > 0 } milestone)
        {
            path.Append("&milestone=").Append(Uri.EscapeDataString(milestone));
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            path.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
        }

        var (json, next) = await Call(() => Client(credential).GetJsonPageAsync(path.ToString(), credential.Token, cancellationToken));
        using (json)
        {
            return new TrackerItemPage([.. Json.Items(json.RootElement).Select(i => Map(i, project)).OfType<SourceItem>()], next);
        }
    }

    private async Task<SourceItem> WithCommentsAsync(TrackerCredential credential, string project, SourceItem item, CancellationToken cancellationToken)
    {
        using var json = await Call(() => Client(credential).GetJsonDocumentAsync(
            $"{ProjectPath(project)}/issues/{TrackerRules.IssueNumberOf(item.Key)}/notes?sort=desc&order_by=created_at&per_page={SourceItemLimits.MaxComments.ToString(CultureInfo.InvariantCulture)}",
            credential.Token,
            cancellationToken));
        return item with
        {
            Comments =
            [
                .. Json.Items(json.RootElement)
                    .Where(n => Json.Property(n, "system").ValueKind != JsonValueKind.True)
                    .Select(n => new SourceItemComment(
                        Json.Text(Json.Property(n, "author"), "name") ?? Json.Text(Json.Property(n, "author"), "username"),
                        Json.Time(n, "created_at") ?? item.UpdatedAt,
                        Json.Text(n, "body") ?? string.Empty)),
            ],
        };
    }

    private SourceItem? Map(JsonElement issue, string project)
    {
        if (Json.Text(issue, "iid") is not { } iid)
        {
            return null;
        }

        var labels = Json.Items(issue, "labels").Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : null).OfType<string>().ToList();
        var type = Json.Text(issue, "issue_type") ?? Json.Text(issue, "type");
        var state = Json.Text(issue, "state");
        var milestone = Json.Text(Json.Property(issue, "milestone"), "title");
        var assignee = Json.Property(issue, "assignee");
        return new SourceItem
        {
            Provider = Provider,
            ExternalId = Json.Text(issue, "id") ?? iid,
            Key = $"{project}#{iid}",
            Url = Json.Text(issue, "web_url") ?? string.Empty,
            Type = TrackerRules.GitLabType(type, labels),
            NativeType = type ?? "issue",
            Title = Json.Text(issue, "title") ?? string.Empty,
            DescriptionMarkdown = Json.Text(issue, "description") ?? string.Empty,
            Status = state ?? "opened",
            StatusCategory = TrackerRules.OpenClosedCategory(state),
            Labels = milestone is null ? labels : [.. labels, $"milestone:{milestone}"],
            Assignee = Json.Text(assignee, "name") ?? Json.Text(assignee, "username"),
            Estimate = Json.Text(Json.Property(issue, "time_stats"), "human_time_estimate"),
            UpdatedAt = Json.Time(issue, "updated_at") ?? DateTimeOffset.UnixEpoch,
        };
    }

    private static string ProjectPath(string project) => $"api/v4/projects/{Uri.EscapeDataString(project)}";

    private GitLabClient Client(TrackerCredential credential) =>
        clients.ForInstance(credential.InstanceBaseUrl ?? throw new TrackerApiException(TrackerFailure.NotConnected, null, "The GitLab instance is unknown."));

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (GitHostBlockedException ex)
        {
            throw new TrackerApiException(TrackerFailure.Upstream, null, ex.Message);
        }
        catch (GitLabApiException ex)
        {
            var failure = ex.Status switch
            {
                401 => TrackerFailure.Rejected,
                403 => TrackerFailure.MissingPermissions,
                404 => TrackerFailure.NotFound,
                429 => TrackerFailure.RateLimited,
                _ => TrackerFailure.Upstream,
            };
            throw new TrackerApiException(
                failure,
                ex.Status,
                ex.UpstreamMessage ?? "GitLab returned an error or couldn't be reached.",
                failure == TrackerFailure.RateLimited ? DateTimeOffset.UtcNow + (ex.RetryAfter ?? TimeSpan.FromMinutes(1)) : null);
        }
    }
}
