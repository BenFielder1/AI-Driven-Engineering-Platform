using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aide.Infrastructure.GitLab;

/// <summary>
/// <c>GET /projects/:id</c> as a project binding needs it (FR-136): the canonical path, default branch, web URL, whether the
/// repository is empty, and the caller's access levels (<c>permissions.project_access</c>, <c>permissions.group_access</c>).
/// </summary>
public sealed record GitLabProjectDetails(
    long Id,
    string PathWithNamespace,
    string? DefaultBranch,
    string? WebUrl,
    bool EmptyRepo,
    int? ProjectAccess,
    int? GroupAccess)
{
    public string ExternalId => Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>FR-140: <c>merge_method</c> (<c>merge</c>, <c>rebase_merge</c>, <c>ff</c>).</summary>
    public string? MergeMethod { get; init; }

    /// <summary>FR-140: <c>squash_option</c> (<c>never</c>, <c>always</c>, <c>default_on</c>, <c>default_off</c>).</summary>
    public string? SquashOption { get; init; }

    /// <summary>FR-139: <c>only_allow_merge_if_pipeline_succeeds</c> ("pipelines must succeed").</summary>
    public bool PipelinesMustSucceed { get; init; }
}

/// <summary>A tag and the commit it points at.</summary>
public sealed record GitLabTag(string Name, string CommitSha);

/// <summary>A GitLab Release (<c>upcoming_release</c>: released in the future, not offered as released).</summary>
public sealed record GitLabRelease(string TagName, string? Name, DateTimeOffset? ReleasedAt, bool Upcoming);

/// <summary>A merge request (FR-137): <see cref="Iid"/> is the project-scoped number shown as <c>!iid</c>.</summary>
public sealed record GitLabMergeRequest(int Iid, string Title, string? WebUrl, string? State, string? SourceBranch, string? TargetBranch);

/// <summary>A file of a repository tree listing (FR-118 detection).</summary>
public sealed record GitLabTreeEntry(string Path, string Type);

/// <summary>A page of a recursive tree listing; <see cref="Next"/> is the keyset link (<c>null</c> on the last page).</summary>
public sealed record GitLabTreePage(IReadOnlyList<GitLabTreeEntry> Entries, Uri? Next);

/// <summary>FR-136–FR-141, §4.19: repository, tag, release and merge request calls.</summary>
public sealed partial class GitLabClient
{
    /// <summary>Tag and release lists read at most this many pages of 100.</summary>
    public const int MaxRefPages = 5;

    /// <summary><c>GET /projects/:id</c> (numeric id or full path); <c>null</c> when it doesn't exist or isn't visible.</summary>
    public async Task<GitLabProjectDetails?> GetProjectAsync(string token, string idOrPath, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync($"api/v4/projects/{Id(idOrPath)}", token, cancellationToken);
            var p = json.RootElement;
            int? Access(string kind) =>
                p.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Object
                && permissions.TryGetProperty(kind, out var access) && access.ValueKind == JsonValueKind.Object
                && Long(access, "access_level") is { } level
                    ? (int)Math.Clamp(level, 0, 100)
                    : null;
            return new GitLabProjectDetails(
                Long(p, "id") ?? throw new GitLabApiException(200, "GitLab's project has no id."),
                Str(p, "path_with_namespace") ?? throw new GitLabApiException(200, "GitLab's project has no path."),
                Str(p, "default_branch"),
                Str(p, "web_url"),
                p.TryGetProperty("empty_repo", out var empty) && empty.ValueKind == JsonValueKind.True,
                Access("project_access"),
                Access("group_access"))
            {
                MergeMethod = Str(p, "merge_method"),
                SquashOption = Str(p, "squash_option"),
                PipelinesMustSucceed = p.TryGetProperty("only_allow_merge_if_pipeline_succeeds", out var must) && must.ValueKind == JsonValueKind.True,
            };
        }
        catch (GitLabApiException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary><c>GET /projects/:id/repository/branches/:branch</c>: the head commit, or <c>null</c> when there is no such branch.</summary>
    public async Task<string?> GetBranchHeadAsync(string token, string projectId, string branch, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync($"api/v4/projects/{Id(projectId)}/repository/branches/{Uri.EscapeDataString(branch)}", token, cancellationToken);
            return json.RootElement.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object ? Str(commit, "id") : null;
        }
        catch (GitLabApiException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>The names in the root of <paramref name="gitRef"/> (FR-86 "empty" check); <c>null</c> when the ref has no tree.</summary>
    public async Task<IReadOnlyList<string>?> ListRootEntriesAsync(string token, string projectId, string gitRef, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync(
                $"api/v4/projects/{Id(projectId)}/repository/tree?" + Query(("ref", gitRef), ("per_page", "100")), token, cancellationToken);
            return json.RootElement.ValueKind == JsonValueKind.Array
                ? [.. json.RootElement.EnumerateArray().Select(e => Str(e, "name")).OfType<string>()]
                : [];
        }
        catch (GitLabApiException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>FR-141: <c>GET /projects/:id/repository/tags</c>, newest first, up to <see cref="MaxRefPages"/> pages.</summary>
    public async Task<IReadOnlyList<GitLabTag>> ListTagsAsync(string token, string projectId, CancellationToken cancellationToken)
    {
        var tags = new List<GitLabTag>();
        await PagesAsync($"api/v4/projects/{Id(projectId)}/repository/tags", token, element =>
        {
            if (Str(element, "name") is { } name && element.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object
                && Str(commit, "id") is { } sha)
            {
                tags.Add(new GitLabTag(name, sha));
            }
        }, cancellationToken);
        return tags;
    }

    /// <summary>FR-61, FR-141: <c>GET /projects/:id/repository/tags/:tag</c>: its commit, or <c>null</c> when it doesn't exist.</summary>
    public async Task<string?> FindTagCommitAsync(string token, string projectId, string tag, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync($"api/v4/projects/{Id(projectId)}/repository/tags/{Uri.EscapeDataString(tag)}", token, cancellationToken);
            return json.RootElement.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object ? Str(commit, "id") : null;
        }
        catch (GitLabApiException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>FR-141: <c>GET /projects/:id/releases</c>, newest first, up to <see cref="MaxRefPages"/> pages.</summary>
    public async Task<IReadOnlyList<GitLabRelease>> ListReleasesAsync(string token, string projectId, CancellationToken cancellationToken)
    {
        var releases = new List<GitLabRelease>();
        await PagesAsync($"api/v4/projects/{Id(projectId)}/releases", token, element =>
        {
            if (Str(element, "tag_name") is { } tag)
            {
                releases.Add(new GitLabRelease(
                    tag,
                    Str(element, "name"),
                    Date(element, "released_at"),
                    element.TryGetProperty("upcoming_release", out var upcoming) && upcoming.ValueKind == JsonValueKind.True));
            }
        }, cancellationToken);
        return releases;
    }

    /// <summary>FR-137: <c>GET /projects/:id/merge_requests?source_branch=&amp;target_branch=&amp;state=opened</c>: the open MR, if any.</summary>
    public async Task<GitLabMergeRequest?> FindOpenMergeRequestAsync(string token, string projectId, string sourceBranch, string targetBranch, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(
            $"api/v4/projects/{Id(projectId)}/merge_requests?" + Query(
                ("source_branch", sourceBranch), ("target_branch", targetBranch), ("state", "opened"), ("per_page", "1")),
            token,
            cancellationToken);
        return json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 0
            ? ToMergeRequest(json.RootElement[0])
            : null;
    }

    /// <summary>FR-137: <c>GET /projects/:id/merge_requests/:iid</c>; <c>null</c> when it doesn't exist.</summary>
    public async Task<GitLabMergeRequest?> GetMergeRequestAsync(string token, string projectId, int iid, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync($"api/v4/projects/{Id(projectId)}/merge_requests/{iid.ToString(CultureInfo.InvariantCulture)}", token, cancellationToken);
            return ToMergeRequest(json.RootElement);
        }
        catch (GitLabApiException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// FR-137: <c>POST /projects/:id/merge_requests</c> with <c>remove_source_branch: false</c> (the title carries the draft
    /// marker the caller chose).
    /// </summary>
    public async Task<GitLabMergeRequest> CreateMergeRequestAsync(
        string token, string projectId, string sourceBranch, string targetBranch, string title, string description, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, Url($"api/v4/projects/{Id(projectId)}/merge_requests"), token);
        request.Content = JsonContent.Create(new Dictionary<string, object>
        {
            ["source_branch"] = sourceBranch,
            ["target_branch"] = targetBranch,
            ["title"] = title,
            ["description"] = description,
            ["remove_source_branch"] = false,
        });
        using var response = await SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        return ToMergeRequest(json.RootElement);
    }

    /// <summary>FR-137: <c>PUT /projects/:id/merge_requests/:iid</c> with a new title (ready = draft marker removed) and/or description.</summary>
    public async Task<GitLabMergeRequest> UpdateMergeRequestAsync(
        string token, string projectId, int iid, string? title, string? description, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>();
        if (title is not null)
        {
            body["title"] = title;
        }

        if (description is not null)
        {
            body["description"] = description;
        }

        using var request = Authorized(HttpMethod.Put, Url($"api/v4/projects/{Id(projectId)}/merge_requests/{iid.ToString(CultureInfo.InvariantCulture)}"), token);
        request.Content = JsonContent.Create(body);
        using var response = await SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        return ToMergeRequest(json.RootElement);
    }

    /// <summary>FR-118: a page of the recursive tree of <paramref name="gitRef"/> (keyset pagination); <c>null</c> for an unknown ref.</summary>
    public async Task<GitLabTreePage?> ListTreePageAsync(string token, string projectId, string gitRef, Uri? next, CancellationToken cancellationToken)
    {
        var url = next ?? Url($"api/v4/projects/{Id(projectId)}/repository/tree?" + Query(
            ("ref", gitRef), ("recursive", "true"), ("per_page", "100"), ("pagination", "keyset")));
        try
        {
            using var request = Authorized(HttpMethod.Get, url, token);
            using var response = await SendAsync(request, cancellationToken);
            using var json = await ReadJsonAsync(response, cancellationToken);
            IReadOnlyList<GitLabTreeEntry> entries = json.RootElement.ValueKind == JsonValueKind.Array
                ? [.. json.RootElement.EnumerateArray()
                    .Where(e => Str(e, "path") is not null)
                    .Select(e => new GitLabTreeEntry(Str(e, "path")!, Str(e, "type") ?? "blob"))]
                : [];
            return new GitLabTreePage(entries, NextLink(response));
        }
        catch (GitLabApiException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>FR-118: <c>GET /projects/:id/repository/commits/:ref</c>: the commit a ref names, or <c>null</c>.</summary>
    public async Task<string?> ResolveCommitAsync(string token, string projectId, string gitRef, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync($"api/v4/projects/{Id(projectId)}/repository/commits/{Uri.EscapeDataString(gitRef)}", token, cancellationToken);
            return Str(json.RootElement, "id");
        }
        catch (GitLabApiException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// FR-118: a file's text at <paramref name="gitRef"/>, read only when <c>HEAD …/repository/files/:path</c> reports at most
    /// <paramref name="maxBytes"/> (<c>X-Gitlab-Size</c>); <c>null</c> when it is missing, larger, or not text.
    /// </summary>
    public async Task<string?> ReadSmallFileAsync(string token, string projectId, string path, string gitRef, int maxBytes, CancellationToken cancellationToken)
    {
        var file = $"api/v4/projects/{Id(projectId)}/repository/files/{Uri.EscapeDataString(path)}";
        using (var head = Authorized(HttpMethod.Head, Url(file + "?" + Query(("ref", gitRef))), token))
        using (var sized = await SendRawAsync(head, cancellationToken))
        {
            if (sized.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                throw new GitLabApiException(429, null) { RetryAfter = RetryAfterOf(sized) };
            }

            if (!sized.IsSuccessStatusCode
                || !sized.Headers.TryGetValues("X-Gitlab-Size", out var sizes)
                || !long.TryParse(sizes.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || size > maxBytes)
            {
                return null;
            }
        }

        using var request = Authorized(HttpMethod.Get, Url(file + "/raw?" + Query(("ref", gitRef))), token);
        try
        {
            using var response = await SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return text.Length <= maxBytes && !text.Contains('\0', StringComparison.Ordinal) ? text : null;
        }
        catch (GitLabApiException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>Reads up to <see cref="MaxRefPages"/> pages of 100 (offset pagination, <c>X-Next-Page</c>).</summary>
    private async Task PagesAsync(string relative, string token, Action<JsonElement> each, CancellationToken cancellationToken)
    {
        for (var page = 1; page <= MaxRefPages; page++)
        {
            var url = Url(relative + "?" + Query(("per_page", "100"), ("page", page.ToString(CultureInfo.InvariantCulture))));
            using var request = Authorized(HttpMethod.Get, url, token);
            using var response = await SendAsync(request, cancellationToken);
            using var json = await ReadJsonAsync(response, cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var element in json.RootElement.EnumerateArray())
            {
                each(element);
            }

            if (!response.Headers.TryGetValues("X-Next-Page", out var next) || string.IsNullOrWhiteSpace(next.FirstOrDefault()))
            {
                return;
            }
        }
    }

    /// <summary>The <c>rel="next"</c> link on this instance's origin, else <c>null</c>.</summary>
    private Uri? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var links))
        {
            return null;
        }

        foreach (var part in links.SelectMany(l => l.Split(',')))
        {
            var segments = part.Split(';', StringSplitOptions.TrimEntries);
            if (segments.Length >= 2 && segments.Skip(1).Any(s => s.Replace(" ", string.Empty, StringComparison.Ordinal) == "rel=\"next\"")
                && Uri.TryCreate(segments[0].Trim('<', '>'), UriKind.Absolute, out var next)
                && Domain.GitHosts.GitHostAddressRules.SameOrigin(BaseUrl, next))
            {
                return next;
            }
        }

        return null;
    }

    private static GitLabMergeRequest ToMergeRequest(JsonElement mr) => new(
        Long(mr, "iid") is { } iid ? (int)Math.Clamp(iid, 0, int.MaxValue) : throw new GitLabApiException(200, "GitLab's merge request has no iid."),
        Str(mr, "title") ?? string.Empty,
        Str(mr, "web_url"),
        Str(mr, "state"),
        Str(mr, "source_branch"),
        Str(mr, "target_branch"));

    private static DateTimeOffset? Date(JsonElement element, string property) =>
        Str(element, property) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    /// <summary>A project id (digits) or a URL-encoded full path.</summary>
    private static string Id(string idOrPath) => Uri.EscapeDataString(idOrPath.Trim());
}
