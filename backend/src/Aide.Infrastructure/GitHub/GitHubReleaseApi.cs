using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Domain.Releases;

namespace Aide.Infrastructure.GitHub;

/// <summary>A pull request as the merge poll and merge readiness read it (§4.14, FR-84).</summary>
public sealed record GitHubPullSnapshot(
    int Number, string? State, bool Merged, bool Draft, bool? Mergeable, string? MergeCommitSha, string? HeadSha, string? NodeId, string? HtmlUrl)
{
    public bool IsOpen => State == "open" && !Merged;

    public bool IsClosedUnmerged => State == "closed" && !Merged;
}

/// <summary>A conditional pull request read: <see cref="NotModified"/> (304, nothing changed) or the PR and its new ETag.</summary>
public sealed record GitHubPullPoll(bool NotModified, GitHubPullSnapshot? Pull, string? ETag);

/// <summary>FR-84: whether the token may push (<c>null</c>: GitHub didn't say) and the merge methods the repository allows (<c>null</c>: unknown).</summary>
public sealed record GitHubRepoMergeSettings(bool? CanPush, IReadOnlyList<string>? AllowedMergeMethods);

/// <summary>FR-84: the rules that apply to a branch (<c>GET /repos/{o}/{r}/rules/branches/{branch}</c>).</summary>
public sealed record GitHubBranchRules(int RequiredApprovingReviews, IReadOnlyList<string> RequiredChecks, IReadOnlyList<string> OtherRules)
{
    public static GitHubBranchRules None { get; } = new(0, [], []);
}

/// <summary>FR-84: the PR's GraphQL view: <c>mergeStateStatus</c>, <c>reviewDecision</c> (GitHub's values, <c>null</c> when unset).</summary>
public sealed record GitHubPullGraph(string? MergeStateStatus, string? ReviewDecision, bool? IsDraft);

/// <summary>
/// §4.14, FR-64, FR-84, FR-85: the pull request, rules, checks and releases endpoints the release work uses, with the
/// project's token (an installation token for <c>github_app</c>, the PAT for <c>github_pat</c>). Same transport rules as
/// the rest of <see cref="GitHubApi"/>: rate limits throw <see cref="GitHubRateLimitedException"/>, other failures
/// <see cref="GitHubApiException"/>; tokens never appear in exceptions or logs.
/// </summary>
public sealed partial class GitHubApi
{
    /// <summary>Check runs read per commit (one page of 100).</summary>
    public const int MaxCheckRuns = 100;

    private static readonly string[] PushPermissionLevels = ["push", "maintain", "admin"];

    private static readonly (string Property, string Method)[] MergeMethodSettings =
        [("allow_merge_commit", "merge"), ("allow_squash_merge", "squash"), ("allow_rebase_merge", "rebase")];

    private const string PullQuery =
        "query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){mergeStateStatus reviewDecision isDraft}}}";

    private const string ReadyMutation =
        "mutation($id:ID!){markPullRequestReadyForReview(input:{pullRequestId:$id}){pullRequest{isDraft}}}";

    /// <summary>
    /// §4.14: <c>GET /repos/{o}/{r}/pulls/{n}</c> with <c>If-None-Match</c> when <paramref name="etag"/> is known. A 304 is
    /// "not modified" (and costs no rate limit); otherwise the PR and its ETag.
    /// </summary>
    public async Task<GitHubPullPoll> PollPullAsync(string token, string owner, string repo, int number, string? etag, CancellationToken cancellationToken)
    {
        var client = Clients.CreateClient(Git.GitHubGitProvider.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(PullPath(owner, repo, number), UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(etag) && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using var response = await SendRawAsync(client, request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new GitHubPullPoll(true, null, etag);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken);
        }

        using var json = await ReadJsonAsync(response, cancellationToken);
        return new GitHubPullPoll(false, ReadPull(json.RootElement, number), response.Headers.ETag?.ToString());
    }

    /// <summary>FR-84: <c>GET /repos/{o}/{r}/pulls/{n}</c>, or <c>null</c> when there is no such PR.</summary>
    public async Task<GitHubPullSnapshot?> FindPullAsync(string token, string owner, string repo, int number, CancellationToken cancellationToken)
    {
        using var json = await GetOrNullAsync(PullPath(owner, repo, number), token, cancellationToken);
        return json is null ? null : ReadPull(json.RootElement, number);
    }

    /// <summary>FR-84: the token's push permission and the repository's allowed merge methods (<c>GET /repos/{o}/{r}</c>); <c>null</c> when unreadable.</summary>
    public async Task<GitHubRepoMergeSettings?> GetRepoMergeSettingsAsync(string token, string owner, string repo, CancellationToken cancellationToken)
    {
        using var json = await GetOrNullAsync(RepoPath(owner, repo), token, cancellationToken);
        if (json is null || json.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = json.RootElement;
        bool? canPush = root.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Object
            ? PushPermissionLevels.Any(level => permissions.TryGetProperty(level, out var granted) && granted.ValueKind == JsonValueKind.True)
            : null;
        var methods = new List<string>();
        var known = false;
        foreach (var (property, method) in MergeMethodSettings)
        {
            if (root.TryGetProperty(property, out var allowed) && allowed.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                known = true;
                if (allowed.GetBoolean())
                {
                    methods.Add(method);
                }
            }
        }

        return new GitHubRepoMergeSettings(canPush, known ? methods : null);
    }

    /// <summary>
    /// FR-84: the rules active on <paramref name="branch"/> (rulesets): required approving reviews (<c>pull_request</c>),
    /// required status checks (contexts) and the other rule types; no rules (or 404) is <see cref="GitHubBranchRules.None"/>.
    /// </summary>
    public async Task<GitHubBranchRules> GetBranchRulesAsync(string token, string owner, string repo, string branch, CancellationToken cancellationToken)
    {
        var path = $"{RepoPath(owner, repo)}/rules/branches/{string.Join('/', branch.Split('/').Select(Uri.EscapeDataString))}?per_page=100";
        using var json = await GetOrNullAsync(path, token, cancellationToken);
        if (json is null || json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return GitHubBranchRules.None;
        }

        var reviews = 0;
        var checks = new List<string>();
        var other = new List<string>();
        foreach (var rule in json.RootElement.EnumerateArray())
        {
            var type = Text(rule, "type");
            var parameters = rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            switch (type)
            {
                case "pull_request" when parameters.ValueKind == JsonValueKind.Object:
                    reviews = Math.Max(reviews, (int)(Long(parameters, "required_approving_review_count") ?? 0));
                    break;
                case "required_status_checks" when parameters.ValueKind == JsonValueKind.Object
                    && parameters.TryGetProperty("required_status_checks", out var required) && required.ValueKind == JsonValueKind.Array:
                    checks.AddRange(required.EnumerateArray().Select(c => Text(c, "context")).OfType<string>());
                    break;
                case { } name when name is not ("pull_request" or "required_status_checks"):
                    other.Add(name);
                    break;
                default:
                    break;
            }
        }

        return new GitHubBranchRules(reviews, [.. checks.Distinct(StringComparer.Ordinal)], [.. other.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// FR-84: the PR's <c>mergeStateStatus</c> and <c>reviewDecision</c> through GraphQL; <c>null</c> when GraphQL is
    /// unavailable for the token (401/403/404, or GraphQL errors such as missing scopes) so readiness degrades gracefully.
    /// </summary>
    public async Task<GitHubPullGraph?> GetPullGraphAsync(string token, string owner, string repo, int number, CancellationToken cancellationToken)
    {
        using var json = await GraphQlAsync(token, PullQuery, new { owner, name = repo, number }, cancellationToken);
        if (json is null
            || !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.Object
            || !repository.TryGetProperty("pullRequest", out var pull) || pull.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new GitHubPullGraph(
            Text(pull, "mergeStateStatus"),
            Text(pull, "reviewDecision"),
            pull.TryGetProperty("isDraft", out var draft) && draft.ValueKind is JsonValueKind.True or JsonValueKind.False ? draft.GetBoolean() : null);
    }

    /// <summary>FR-65: takes a draft PR out of draft (GraphQL <c>markPullRequestReadyForReview</c>); false when GitHub refused.</summary>
    public async Task<bool> MarkReadyForReviewAsync(string token, string nodeId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        using var json = await GraphQlAsync(token, ReadyMutation, new { id = nodeId }, cancellationToken);
        return json is not null && json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("markPullRequestReadyForReview", out var marked) && marked.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// FR-84, FR-85: the check runs (<c>GET /commits/{sha}/check-runs</c>, latest per name) and commit statuses
    /// (<c>GET /commits/{sha}/status</c>) on <paramref name="sha"/>, not yet marked required; <c>null</c> when neither can be
    /// read (e.g. no Checks/Commit statuses permission).
    /// </summary>
    public async Task<IReadOnlyList<CheckObservation>?> ListChecksAsync(string token, string owner, string repo, string sha, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha);
        var commit = $"{RepoPath(owner, repo)}/commits/{Uri.EscapeDataString(sha)}";
        var checks = new List<CheckObservation>();
        var available = false;
        using (var runs = await GetOrForbiddenNullAsync($"{commit}/check-runs?filter=latest&per_page={MaxCheckRuns.ToString(CultureInfo.InvariantCulture)}", token, cancellationToken))
        {
            if (runs is not null && runs.RootElement.TryGetProperty("check_runs", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                available = true;
                checks.AddRange(list.EnumerateArray()
                    .Where(r => Text(r, "name") is { Length: > 0 })
                    .Select(r => new CheckObservation(
                        Text(r, "name")!,
                        MergeReadinessRules.CheckStatusOf(Text(r, "status")),
                        MergeReadinessRules.CheckConclusionOf(Text(r, "conclusion")),
                        false,
                        HttpsUrl(Text(r, "details_url")) ?? HttpsUrl(Text(r, "html_url")))));
            }
        }

        using (var status = await GetOrForbiddenNullAsync($"{commit}/status?per_page=100", token, cancellationToken))
        {
            if (status is not null && status.RootElement.TryGetProperty("statuses", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                available = true;
                foreach (var entry in list.EnumerateArray())
                {
                    if (Text(entry, "context") is not { Length: > 0 } context)
                    {
                        continue;
                    }

                    var (state, conclusion) = MergeReadinessRules.CommitStatusOf(Text(entry, "state"));
                    checks.Add(new CheckObservation(context, state, conclusion, false, HttpsUrl(Text(entry, "target_url"))));
                }
            }
        }

        return available ? checks : null;
    }

    /// <summary>FR-64: the html URL of the GitHub Release for <paramref name="tag"/> (<c>GET /releases/tags/{tag}</c>), or <c>null</c>.</summary>
    public async Task<string?> FindReleaseUrlAsync(string token, string owner, string repo, string tag, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        using var json = await GetOrNullAsync($"{RepoPath(owner, repo)}/releases/tags/{string.Join('/', tag.Split('/').Select(Uri.EscapeDataString))}", token, cancellationToken);
        return json is null ? null : HttpsUrl(Text(json.RootElement, "html_url")) ?? ReleasePageUrl(owner, repo, tag);
    }

    /// <summary>
    /// FR-64: creates the GitHub Release for the existing tag <paramref name="tag"/> (<c>POST /repos/{o}/{r}/releases</c>,
    /// published, a prerelease for a semver prerelease) and returns its html URL. A release that already exists for the tag
    /// (422 <c>already_exists</c>, e.g. a concurrent attempt) is looked up and reused.
    /// </summary>
    public async Task<string> CreateReleaseAsync(
        string token, string owner, string repo, string tag, string name, string body, bool prerelease, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await SendAsync(
                HttpMethod.Post,
                $"{RepoPath(owner, repo)}/releases",
                token,
                new { tag_name = tag, name, body, draft = false, prerelease, generate_release_notes = false },
                cancellationToken);
            return HttpsUrl(Text(json.RootElement, "html_url")) ?? ReleasePageUrl(owner, repo, tag);
        }
        catch (GitHubApiException ex) when (ex.Kind == GitHubFailureKind.Unprocessable)
        {
            if (await FindReleaseUrlAsync(token, owner, repo, tag, cancellationToken) is { } existing)
            {
                return existing;
            }

            throw;
        }
    }

    /// <summary>A GraphQL request; <c>null</c> when GraphQL is unavailable for the token (401/403/404 or a GraphQL <c>errors</c> reply).</summary>
    private async Task<JsonDocument?> GraphQlAsync(string token, string query, object variables, CancellationToken cancellationToken)
    {
        var client = Clients.CreateClient(Git.GitHubGitProvider.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("graphql", UriKind.Relative))
        {
            Content = JsonContent.Create(new { query, variables }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendRawAsync(client, request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken);
        }

        var json = await ReadJsonAsync(response, cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Object
            || (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0))
        {
            json.Dispose();
            return null;
        }

        return json;
    }

    /// <summary>A GET whose 403 and 404 are <c>null</c> (a missing permission, e.g. Checks: read, isn't a failure here).</summary>
    private async Task<JsonDocument?> GetOrForbiddenNullAsync(string path, string token, CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(HttpMethod.Get, path, token, null, cancellationToken);
        }
        catch (GitHubApiException ex) when (ex.Kind is GitHubFailureKind.NotFound or GitHubFailureKind.Forbidden or GitHubFailureKind.Unprocessable)
        {
            return null;
        }
    }

    private static async Task<GitHubApiException> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var kind = status switch
        {
            401 => GitHubFailureKind.Unauthorized,
            403 => GitHubFailureKind.Forbidden,
            404 => GitHubFailureKind.NotFound,
            409 => GitHubFailureKind.Conflict,
            422 => GitHubFailureKind.Unprocessable,
            >= 500 => GitHubFailureKind.Upstream,
            _ => GitHubFailureKind.Other,
        };
        return new GitHubApiException(kind, status, $"GitHub: {await MessageAsync(response, cancellationToken)} (HTTP {status})");
    }

    private static GitHubPullSnapshot ReadPull(JsonElement root, int number)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new GitHubApiException(GitHubFailureKind.Upstream, null, "GitHub returned an unreadable pull request.");
        }

        return new GitHubPullSnapshot(
            (int)(Long(root, "number") ?? number),
            Text(root, "state"),
            root.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True,
            root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
            root.TryGetProperty("mergeable", out var mergeable) && mergeable.ValueKind is JsonValueKind.True or JsonValueKind.False ? mergeable.GetBoolean() : null,
            CommitId(Text(root, "merge_commit_sha")),
            root.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object ? CommitId(Text(head, "sha")) : null,
            Text(root, "node_id"),
            HttpsUrl(Text(root, "html_url")));
    }

    private static string? CommitId(string? value) =>
        value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigitLower) ? value : null;

    /// <summary>An https URL of at most 500 characters, else <c>null</c> (never echo anything else into the UI).</summary>
    private static string? HttpsUrl(string? value) =>
        value is { Length: > 0 and <= 500 } && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.OriginalString : null;

    private static string ReleasePageUrl(string owner, string repo, string tag) =>
        $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/tag/{string.Join('/', tag.Split('/').Select(Uri.EscapeDataString))}";

    private static string RepoPath(string owner, string repo) => $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    private static string PullPath(string owner, string repo, int number) =>
        $"{RepoPath(owner, repo)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}";
}
