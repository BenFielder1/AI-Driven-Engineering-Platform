using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Domain.GitHosts;

namespace Aide.Infrastructure.GitLab;

/// <summary>
/// FR-138, FR-140: <c>GET /projects/:id/merge_requests/:iid</c> as readiness, the merge and the merge poll need it.
/// <see cref="Sha"/> is the head; the merged commit comes from <see cref="MergeCommitSha"/>, <see cref="SquashCommitSha"/>,
/// else the head (fast-forward, <see cref="GitLabMergeResult.Sha"/>).
/// </summary>
public sealed record GitLabMergeRequestDetails(
    int Iid,
    string Title,
    string? WebUrl,
    string? State,
    bool Draft,
    string? DetailedMergeStatus,
    bool HasConflicts,
    bool BlockingDiscussionsResolved,
    string? Sha,
    string? MergeCommitSha,
    string? SquashCommitSha,
    string? HeadPipelineStatus);

/// <summary>FR-138: the approvals endpoint (<c>approved</c>; <c>approvals_left</c> on Premium only).</summary>
public sealed record GitLabApprovals(bool? Approved, int? ApprovalsLeft);

/// <summary>FR-140: the merge call's answer (the merged commit is <see cref="GitLabMergeResult.Sha"/> of the three).</summary>
public sealed record GitLabMerged(string? MergeCommitSha, string? SquashCommitSha, string? Sha);

/// <summary>FR-140: a GitLab Release to create (its tag is created on <see cref="Ref"/> when it doesn't exist yet).</summary>
public sealed record GitLabNewRelease(string TagName, string Ref, string Name, string Description, string TagMessage);

/// <summary>FR-138–FR-140, §4.19: merge readiness, CI statuses, the merge and the GitLab Release.</summary>
public sealed partial class GitLabClient
{
    /// <summary>FR-138: <c>GET /projects/:id/merge_requests/:iid</c> with the readiness fields; <c>null</c> when it doesn't exist.</summary>
    public async Task<GitLabMergeRequestDetails?> GetMergeRequestDetailsAsync(string token, string projectId, int iid, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync(MergeRequestPath(projectId, iid), token, cancellationToken);
            var mr = json.RootElement;
            var pipeline = mr.TryGetProperty("head_pipeline", out var head) && head.ValueKind == JsonValueKind.Object ? Str(head, "status") : null;
            return new GitLabMergeRequestDetails(
                Long(mr, "iid") is { } number ? (int)Math.Clamp(number, 0, int.MaxValue) : throw new GitLabApiException(200, "GitLab's merge request has no iid."),
                Str(mr, "title") ?? string.Empty,
                Str(mr, "web_url"),
                Str(mr, "state"),
                (mr.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                    || (mr.TryGetProperty("work_in_progress", out var wip) && wip.ValueKind == JsonValueKind.True),
                Str(mr, "detailed_merge_status"),
                mr.TryGetProperty("has_conflicts", out var conflicts) && conflicts.ValueKind == JsonValueKind.True,
                !(mr.TryGetProperty("blocking_discussions_resolved", out var resolved) && resolved.ValueKind == JsonValueKind.False),
                Str(mr, "sha"),
                Str(mr, "merge_commit_sha"),
                Str(mr, "squash_commit_sha"),
                pipeline);
        }
        catch (GitLabApiException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>FR-138: <c>GET …/merge_requests/:iid/approvals</c>; <c>null</c> when GitLab doesn't show it (403/404).</summary>
    public async Task<GitLabApprovals?> GetApprovalsAsync(string token, string projectId, int iid, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync($"{MergeRequestPath(projectId, iid)}/approvals", token, cancellationToken);
            var root = json.RootElement;
            bool? approved = root.TryGetProperty("approved", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
            return new GitLabApprovals(approved, Long(root, "approvals_left") is { } left ? (int)Math.Clamp(left, 0, 1000) : null);
        }
        catch (GitLabApiException ex) when (ex.Status is 403 or 404)
        {
            return null;
        }
    }

    /// <summary>
    /// FR-138: <c>GET /projects/:id/protected_branches/:name</c>: its <c>merge_access_levels[].access_level</c>; <c>null</c>
    /// when the branch isn't protected (404) or the caller may not read it (403: unknown, GitLab's own status decides).
    /// </summary>
    public async Task<IReadOnlyList<int>?> GetProtectedBranchMergeLevelsAsync(string token, string projectId, string branch, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync($"api/v4/projects/{Id(projectId)}/protected_branches/{Uri.EscapeDataString(branch)}", token, cancellationToken);
            if (!json.RootElement.TryGetProperty("merge_access_levels", out var levels) || levels.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. levels.EnumerateArray().Select(l => Long(l, "access_level")).OfType<long>().Select(l => (int)Math.Clamp(l, 0, 100))];
        }
        catch (GitLabApiException ex) when (ex.Status is 403 or 404)
        {
            return null;
        }
    }

    /// <summary>
    /// FR-139: <c>GET /projects/:id/repository/commits/:sha/statuses</c> (the latest status per name: pipeline jobs and
    /// external statuses), at most 100.
    /// </summary>
    public async Task<IReadOnlyList<GitLabCommitStatus>> ListCommitStatusesAsync(string token, string projectId, string sha, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync(
                $"api/v4/projects/{Id(projectId)}/repository/commits/{Uri.EscapeDataString(sha)}/statuses?" + Query(("per_page", "100")), token, cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return
            [
                .. json.RootElement.EnumerateArray()
                    .Where(s => Str(s, "name") is not null)
                    .Select(s => new GitLabCommitStatus(
                        Str(s, "name")!,
                        Str(s, "status"),
                        s.TryGetProperty("allow_failure", out var allow) && allow.ValueKind == JsonValueKind.True,
                        WebLink(Str(s, "target_url")))),
            ];
        }
        catch (GitLabApiException ex) when (ex.Status == 404)
        {
            return [];
        }
    }

    /// <summary>
    /// FR-140: <c>PUT …/merge_requests/:iid/merge {sha, squash, should_remove_source_branch: false}</c>. Throws
    /// <see cref="GitLabApiException"/> on 405 (not mergeable), 409 (the head moved), 422 (failed), 403 and others.
    /// </summary>
    public async Task<GitLabMerged> MergeAsync(string token, string projectId, int iid, string sha, bool squash, string? message, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["sha"] = sha,
            ["squash"] = squash,
            ["should_remove_source_branch"] = false,
        };
        if (!string.IsNullOrEmpty(message))
        {
            body[squash ? "squash_commit_message" : "merge_commit_message"] = message;
        }

        using var request = Authorized(HttpMethod.Put, Url($"{MergeRequestPath(projectId, iid)}/merge"), token);
        request.Content = JsonContent.Create(body);
        using var response = await SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var root = json.RootElement;
        return new GitLabMerged(Str(root, "merge_commit_sha"), Str(root, "squash_commit_sha"), Str(root, "sha"));
    }

    /// <summary>FR-140: <c>GET /projects/:id/releases/:tag</c>: the release's web URL (<c>_links.self</c>), or <c>null</c> when there is none.</summary>
    public async Task<(bool Exists, string? SelfLink)> FindReleaseAsync(string token, string projectId, string tag, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonAsync($"api/v4/projects/{Id(projectId)}/releases/{Uri.EscapeDataString(tag)}", token, cancellationToken);
            return (true, SelfLink(json.RootElement));
        }
        catch (GitLabApiException ex) when (ex.Status == 404)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// FR-140: <c>POST /projects/:id/releases {tag_name, ref, name, description, tag_message}</c> (the annotated tag is created
    /// on <c>ref</c> when it doesn't exist); the release's web URL (<c>_links.self</c>).
    /// </summary>
    public async Task<string?> CreateReleaseAsync(string token, string projectId, GitLabNewRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        using var request = Authorized(HttpMethod.Post, Url($"api/v4/projects/{Id(projectId)}/releases"), token);
        request.Content = JsonContent.Create(new Dictionary<string, object>
        {
            ["tag_name"] = release.TagName,
            ["ref"] = release.Ref,
            ["name"] = release.Name,
            ["description"] = release.Description,
            ["tag_message"] = release.TagMessage,
        });
        using var response = await SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        return SelfLink(json.RootElement);
    }

    private static string MergeRequestPath(string projectId, int iid) =>
        $"api/v4/projects/{Id(projectId)}/merge_requests/{iid.ToString(CultureInfo.InvariantCulture)}";

    private static string? SelfLink(JsonElement release) =>
        release.ValueKind == JsonValueKind.Object && release.TryGetProperty("_links", out var links) && links.ValueKind == JsonValueKind.Object
            ? WebLink(Str(links, "self"))
            : null;

    /// <summary>An absolute http(s) URL of at most 500 characters, else <c>null</c>.</summary>
    private static string? WebLink(string? url) =>
        url is { Length: <= 500 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? url
            : null;
}
