using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aide.Domain.GitHosts;

namespace Aide.Infrastructure.Bitbucket;

/// <summary>A pull request (§4.19): its id, title, <c>state</c> (<c>OPEN</c>, <c>MERGED</c>, <c>DECLINED</c>, <c>SUPERSEDED</c>), draft flag, web page, branches, head and merge commits.</summary>
public sealed record BitbucketPullRequest(
    int Id,
    string? Title,
    string State,
    bool Draft,
    string? WebUrl,
    string? SourceBranch,
    string? SourceCommit,
    string? DestinationBranch,
    string? MergeCommit);

/// <summary>A tag (<c>refs/tags</c>): its name, target commit, web page and date.</summary>
public sealed record BitbucketTag(string Name, string? Hash, string? WebUrl, DateTimeOffset? Date);

/// <summary>
/// The answer to <c>POST …/merge</c> (FR-140): the merged pull request (200), or the <c>merge/task-status</c> path to poll
/// (202 with <c>Location</c>; <see cref="TaskPath"/> is relative to the API base).
/// </summary>
public sealed record BitbucketMergeAnswer(BitbucketPullRequest? PullRequest, string? TaskPath);

/// <summary>A <c>merge/task-status</c> answer: the task's state and, once it succeeded, the merged pull request.</summary>
public sealed record BitbucketMergeTask(BitbucketMergeTaskState State, BitbucketPullRequest? Result);

/// <summary>An entry of <c>…/src/{commit}/</c>: its path, <c>commit_file</c> or <c>commit_directory</c>, and size.</summary>
public sealed record BitbucketSrcEntry(string Path, string Type, long? Size);

/// <summary>A page of <c>…/src/{commit}/</c> entries; <see cref="Next"/> is the next page relative to the API base.</summary>
public sealed record BitbucketSrcPage(IReadOnlyList<BitbucketSrcEntry> Entries, string? Next);

/// <summary>
/// FR-136–FR-141, §4.19: repository, ref, pull request, mergeability, CI status, merge and tag calls. Every path is under
/// <c>/2.0/repositories/{workspace}/{slug}</c> of the fixed API host; a <c>next</c> or <c>Location</c> link Bitbucket sends is
/// followed only when it is on that host (<see cref="ToRelative"/>). A 404 on a lookup is <c>null</c>; other failures throw
/// <see cref="BitbucketApiException"/> (an answer AIDE can't use is a 200 failure with a reason).
/// </summary>
public sealed partial class BitbucketClient
{
    /// <summary>The most pages of tags (100 each) or statuses read.</summary>
    public const int MaxListPages = 10;

    /// <summary>The path of a repository given as <c>workspace/slug</c>.</summary>
    public static string RepoPath(string fullName)
    {
        var (workspace, slug) = BitbucketProjectRules.SplitFullName(fullName);
        return $"repositories/{Segment(workspace)}/{Segment(slug)}";
    }

    /// <summary>
    /// <c>GET /repositories/{ws}/{slug or uuid}</c> (a UUID in braces is accepted for the slug; <c>{}</c> for the workspace
    /// when only the UUID is known). <c>null</c> on 404.
    /// </summary>
    public async Task<BitbucketRepository?> GetRepositoryAsync(BitbucketAuth auth, string workspace, string slugOrUuid, CancellationToken cancellationToken)
    {
        var ws = string.IsNullOrWhiteSpace(workspace) ? "{}" : workspace;
        using var json = await TryGetJsonAsync($"repositories/{Segment(ws)}/{Segment(slugOrUuid)}", auth, cancellationToken);
        return json is null ? null : ToRepository(json.RootElement) ?? throw Unusable("repository");
    }

    /// <summary>
    /// FR-136: the caller's permission on the repository (<c>GET /user/permissions/repositories?q=repository.uuid="…"</c>):
    /// <c>admin</c>, <c>write</c> or <c>read</c>; <c>null</c> when none is listed.
    /// </summary>
    public async Task<string?> GetRepositoryPermissionAsync(BitbucketAuth auth, string repoUuid, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync($"user/permissions/repositories?{Query(("q", BitbucketProjectRules.PermissionQuery(repoUuid)), ("pagelen", "10"))}", auth, cancellationToken);
        return Values(json.RootElement).Select(v => Str(v, "permission")).FirstOrDefault(p => p is not null);
    }

    /// <summary><c>GET …/refs/branches/{name}</c>: the branch head (<c>target.hash</c>); <c>null</c> when it doesn't exist.</summary>
    public async Task<string?> GetBranchHeadAsync(BitbucketAuth auth, string fullName, string branch, CancellationToken cancellationToken)
    {
        using var json = await TryGetJsonAsync($"{RepoPath(fullName)}/refs/branches/{Segment(branch)}", auth, cancellationToken);
        return json is null ? null : Target(json.RootElement) ?? throw Unusable("branch");
    }

    /// <summary><c>GET …/commit/{revision}</c>: the full hash of a commit, branch or tag; <c>null</c> when it doesn't exist.</summary>
    public async Task<string?> GetCommitHashAsync(BitbucketAuth auth, string fullName, string revision, CancellationToken cancellationToken)
    {
        using var json = await TryGetJsonAsync($"{RepoPath(fullName)}/commit/{Segment(revision)}", auth, cancellationToken);
        return json is null ? null : Str(json.RootElement, "hash") ?? throw Unusable("commit");
    }

    /// <summary>FR-86: the names in the repository root at <paramref name="commit"/> (first 100); <c>null</c> on 404.</summary>
    public async Task<IReadOnlyList<string>?> ListRootEntriesAsync(BitbucketAuth auth, string fullName, string commit, CancellationToken cancellationToken)
    {
        using var json = await TryGetJsonAsync($"{RepoPath(fullName)}/src/{Segment(commit)}/?pagelen=100", auth, cancellationToken);
        return json is null ? null : [.. Values(json.RootElement).Select(v => Str(v, "path")).OfType<string>().Select(p => p.Split('/')[^1])];
    }

    /// <summary>
    /// FR-118, FR-141: one page of the recursive listing of the repository at <paramref name="commit"/>
    /// (<c>…/src/{commit}/?max_depth=…</c>, 100 per page), or the page <paramref name="next"/> points at. <c>null</c> on 404.
    /// </summary>
    public async Task<BitbucketSrcPage?> ListSrcPageAsync(BitbucketAuth auth, string fullName, string commit, string? next, CancellationToken cancellationToken)
    {
        var relative = next ?? $"{RepoPath(fullName)}/src/{Segment(commit)}/?max_depth=20&pagelen=100";
        using var json = await TryGetJsonAsync(relative, auth, cancellationToken);
        if (json is null)
        {
            return null;
        }

        var entries = Values(json.RootElement)
            .Where(v => Str(v, "path") is not null)
            .Select(v => new BitbucketSrcEntry(Str(v, "path")!, Str(v, "type") ?? string.Empty, Long(v, "size")))
            .ToList();
        return new BitbucketSrcPage(entries, ToRelative(Str(json.RootElement, "next")));
    }

    /// <summary>FR-118: a file's text at <paramref name="commit"/> when it is at most <paramref name="maxLength"/> bytes; <c>null</c> otherwise or on 404.</summary>
    public async Task<string?> ReadSmallFileAsync(BitbucketAuth auth, string fullName, string commit, string path, int maxLength, CancellationToken cancellationToken)
    {
        var segments = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Segment));
        using var request = Authorized(HttpMethod.Get, $"{RepoPath(fullName)}/src/{Segment(commit)}/{segments}", auth);
        using var response = await SendRawAsync(request, cancellationToken);
        if ((int)response.StatusCode == 404)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return bytes.Length > maxLength ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>FR-141: the repository's tags (<c>GET …/refs/tags</c>, newest first, up to <see cref="MaxListPages"/> pages of 100).</summary>
    public async Task<IReadOnlyList<BitbucketTag>> ListTagsAsync(BitbucketAuth auth, string fullName, CancellationToken cancellationToken)
    {
        var tags = new List<BitbucketTag>();
        for (var page = 1; page <= MaxListPages; page++)
        {
            using var json = await GetJsonAsync(
                $"{RepoPath(fullName)}/refs/tags?{Query(("pagelen", "100"), ("page", page.ToString(CultureInfo.InvariantCulture)), ("sort", "-target.date"))}", auth, cancellationToken);
            tags.AddRange(Values(json.RootElement).Select(ToTag).OfType<BitbucketTag>());
            if (!HasNext(json.RootElement))
            {
                break;
            }
        }

        return tags;
    }

    /// <summary>FR-140: <c>GET …/refs/tags/{name}</c>; <c>null</c> when the tag doesn't exist.</summary>
    public async Task<BitbucketTag?> GetTagAsync(BitbucketAuth auth, string fullName, string name, CancellationToken cancellationToken)
    {
        using var json = await TryGetJsonAsync($"{RepoPath(fullName)}/refs/tags/{Segment(name)}", auth, cancellationToken);
        return json is null ? null : ToTag(json.RootElement) ?? throw Unusable("tag");
    }

    /// <summary>FR-140: <c>POST …/refs/tags {name, target: {hash}, message}</c>: an annotated tag on the full commit hash.</summary>
    public async Task<BitbucketTag> CreateTagAsync(BitbucketAuth auth, string fullName, string name, string hash, string message, CancellationToken cancellationToken)
    {
        using var json = await SendJsonAsync(
            HttpMethod.Post,
            $"{RepoPath(fullName)}/refs/tags",
            auth,
            new Dictionary<string, object?> { ["name"] = name, ["target"] = new Dictionary<string, string> { ["hash"] = hash }, ["message"] = message },
            cancellationToken);
        return ToTag(json.RootElement) ?? new BitbucketTag(name, hash, null, null);
    }

    /// <summary>FR-137: the open pull request from <paramref name="sourceBranch"/> (<c>GET …/pullrequests?state=OPEN&amp;q=source.branch.name="…"</c>).</summary>
    public async Task<BitbucketPullRequest?> FindOpenPullRequestAsync(BitbucketAuth auth, string fullName, string sourceBranch, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(
            $"{RepoPath(fullName)}/pullrequests?{Query(("state", "OPEN"), ("q", BitbucketProjectRules.SourceBranchQuery(sourceBranch)), ("pagelen", "10"))}", auth, cancellationToken);
        return Values(json.RootElement).Select(ToPullRequest).OfType<BitbucketPullRequest>().FirstOrDefault(p => p.SourceBranch is null || p.SourceBranch == sourceBranch);
    }

    /// <summary>
    /// FR-137: <c>POST …/pullrequests {title, description, source.branch.name, destination.branch.name,
    /// close_source_branch: false, draft: true}</c>.
    /// </summary>
    public async Task<BitbucketPullRequest> CreatePullRequestAsync(
        BitbucketAuth auth, string fullName, string title, string description, string sourceBranch, string destinationBranch, CancellationToken cancellationToken)
    {
        using var json = await SendJsonAsync(
            HttpMethod.Post,
            $"{RepoPath(fullName)}/pullrequests",
            auth,
            new Dictionary<string, object?>
            {
                ["title"] = title,
                ["description"] = description,
                ["source"] = new { branch = new { name = sourceBranch } },
                ["destination"] = new { branch = new { name = destinationBranch } },
                ["close_source_branch"] = false,
                ["draft"] = true,
            },
            cancellationToken);
        return ToPullRequest(json.RootElement) ?? throw Unusable("pull request");
    }

    /// <summary>FR-137: <c>PUT …/pullrequests/{id}</c> with the fields given (title, description, <c>draft</c>).</summary>
    public async Task<BitbucketPullRequest> UpdatePullRequestAsync(
        BitbucketAuth auth, string fullName, int id, string? title, string? description, bool? draft, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>();
        if (title is not null)
        {
            body["title"] = title;
        }

        if (description is not null)
        {
            body["description"] = description;
        }

        if (draft is { } isDraft)
        {
            body["draft"] = isDraft;
        }

        using var json = await SendJsonAsync(HttpMethod.Put, $"{RepoPath(fullName)}/pullrequests/{Id(id)}", auth, body, cancellationToken);
        return ToPullRequest(json.RootElement) ?? throw Unusable("pull request");
    }

    /// <summary><c>GET …/pullrequests/{id}</c>; <c>null</c> on 404.</summary>
    public async Task<BitbucketPullRequest?> GetPullRequestAsync(BitbucketAuth auth, string fullName, int id, CancellationToken cancellationToken)
    {
        using var json = await TryGetJsonAsync($"{RepoPath(fullName)}/pullrequests/{Id(id)}", auth, cancellationToken);
        return json is null ? null : ToPullRequest(json.RootElement) ?? throw Unusable("pull request");
    }

    /// <summary>
    /// FR-138: <c>GET …/pullrequests/{id}/mergeability/checks</c> (<c>type</c>, <c>key</c>/<c>name</c>, <c>status</c>,
    /// <c>required</c>, <c>blocking</c>); <c>null</c> on 404. An answer without a check list is unusable (a failure).
    /// </summary>
    public async Task<IReadOnlyList<MergeabilityCheck>?> GetMergeabilityChecksAsync(BitbucketAuth auth, string fullName, int id, CancellationToken cancellationToken)
    {
        using var json = await TryGetJsonAsync($"{RepoPath(fullName)}/pullrequests/{Id(id)}/mergeability/checks", auth, cancellationToken);
        if (json is null)
        {
            return null;
        }

        var root = json.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? [.. root.EnumerateArray()]
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array
                ? [.. values.EnumerateArray()]
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array
                    ? checks.EnumerateArray().ToList()
                    : throw Unusable("mergeability checks");
        return [.. items.Where(c => c.ValueKind == JsonValueKind.Object).Select(c => new MergeabilityCheck(
            Str(c, "type") ?? "unknown",
            Str(c, "key") ?? Str(c, "name"),
            Str(c, "status")?.ToUpperInvariant() ?? "UNKNOWN",
            Bool(c, "required") ?? false,
            Bool(c, "blocking") ?? false))];
    }

    /// <summary>FR-139: <c>GET …/commit/{sha}/statuses</c> (<c>key</c>/<c>name</c>, <c>state</c>, <c>url</c>), up to <see cref="MaxListPages"/> pages of 100.</summary>
    public async Task<IReadOnlyList<BitbucketCommitStatus>> ListCommitStatusesAsync(BitbucketAuth auth, string fullName, string sha, CancellationToken cancellationToken)
    {
        var statuses = new List<BitbucketCommitStatus>();
        for (var page = 1; page <= MaxListPages; page++)
        {
            using var json = await GetJsonAsync(
                $"{RepoPath(fullName)}/commit/{Segment(sha)}/statuses?{Query(("pagelen", "100"), ("page", page.ToString(CultureInfo.InvariantCulture)))}", auth, cancellationToken);
            statuses.AddRange(Values(json.RootElement)
                .Where(s => (Str(s, "name") ?? Str(s, "key")) is not null)
                .Select(s => new BitbucketCommitStatus(Str(s, "name") ?? Str(s, "key")!, Str(s, "state"), Str(s, "url"))));
            if (!HasNext(json.RootElement))
            {
                break;
            }
        }

        return statuses;
    }

    /// <summary>
    /// FR-140: <c>POST …/pullrequests/{id}/merge {merge_strategy, close_source_branch: false, message}</c>. 200: the merged
    /// pull request; 202: the <c>Location</c> of <c>merge/task-status</c> to poll. Other answers throw (400 not mergeable,
    /// 403, 409 refs changed, 555 timeout…).
    /// </summary>
    public async Task<BitbucketMergeAnswer> MergePullRequestAsync(
        BitbucketAuth auth, string fullName, int id, string mergeStrategy, string message, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, $"{RepoPath(fullName)}/pullrequests/{Id(id)}/merge", auth);
        request.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["merge_strategy"] = mergeStrategy,
            ["close_source_branch"] = false,
            ["message"] = message,
        });
        using var response = await SendRawAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        if (status == 202)
        {
            var location = response.Headers.Location is { } l
                ? (l.IsAbsoluteUri ? ToRelative(l.AbsoluteUri) : ToRelative(new Uri(ApiBase, l).AbsoluteUri))
                : null;
            return new BitbucketMergeAnswer(null, location);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken);
        }

        using var json = await ReadJsonAsync(response, cancellationToken);
        return new BitbucketMergeAnswer(ToPullRequest(json.RootElement) ?? throw Unusable("merged pull request"), null);
    }

    /// <summary>FR-140: <c>GET …/merge/task-status/{task}</c> (<paramref name="taskPath"/> relative to the API base).</summary>
    public async Task<BitbucketMergeTask> GetMergeTaskAsync(BitbucketAuth auth, string taskPath, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(taskPath, auth, cancellationToken);
        var root = json.RootElement;
        var state = BitbucketProjectRules.TaskStateOf(Str(root, "task_status"));
        var result = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("merge_result", out var merged) && merged.ValueKind == JsonValueKind.Object
            ? ToPullRequest(merged)
            : null;
        return new BitbucketMergeTask(state, result);
    }

    /// <summary>
    /// A link Bitbucket sent (<c>next</c>, <c>Location</c>) as a path relative to the API base, only when it is on the API
    /// host under <c>/2.0/</c> over https; <c>null</c> otherwise (never followed).
    /// </summary>
    public static string? ToRelative(string? link)
    {
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, ApiBase.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != ApiBase.Port
            || !uri.AbsolutePath.StartsWith(ApiBase.AbsolutePath, StringComparison.Ordinal))
        {
            return null;
        }

        return uri.PathAndQuery[ApiBase.AbsolutePath.Length..];
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string relative, BitbucketAuth auth, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, relative, auth);
        using var response = await SendRawAsync(request, cancellationToken);
        if ((int)response.StatusCode == 404)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken);
        }

        return await ReadJsonAsync(response, cancellationToken);
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string relative, BitbucketAuth auth, object body, CancellationToken cancellationToken)
    {
        using var request = Authorized(method, relative, auth);
        request.Content = JsonContent.Create(body);
        using var response = await SendAsync(request, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private static BitbucketApiException Unusable(string what) => new(200, $"Bitbucket's {what} answer couldn't be read.");

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static string? Target(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object ? Str(target, "hash") : null;

    private static string? Html(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object
        && links.TryGetProperty("html", out var html) && html.ValueKind == JsonValueKind.Object ? Str(html, "href") : null;

    private static BitbucketTag? ToTag(JsonElement t)
    {
        if (Str(t, "name") is not { } name)
        {
            return null;
        }

        var date = t.ValueKind == JsonValueKind.Object && t.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object ? Str(target, "date") : null;
        return new BitbucketTag(
            name,
            Target(t),
            Html(t),
            date is not null && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUniversalTime() : null);
    }

    private static BitbucketPullRequest? ToPullRequest(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt32(out var id))
        {
            return null;
        }

        static (string? Branch, string? Commit) Side(JsonElement pr, string name)
        {
            if (!pr.TryGetProperty(name, out var side) || side.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var branch = side.TryGetProperty("branch", out var b) && b.ValueKind == JsonValueKind.Object ? Str(b, "name") : null;
            var commit = side.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.Object ? Str(c, "hash") : null;
            return (branch, commit);
        }

        var (sourceBranch, sourceCommit) = Side(p, "source");
        var (destinationBranch, _) = Side(p, "destination");
        var merge = p.TryGetProperty("merge_commit", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "hash") : null;
        return new BitbucketPullRequest(
            id,
            Str(p, "title"),
            Str(p, "state")?.ToUpperInvariant() ?? "OPEN",
            Bool(p, "draft") ?? false,
            Html(p),
            sourceBranch,
            sourceCommit,
            destinationBranch,
            merge);
    }
}
