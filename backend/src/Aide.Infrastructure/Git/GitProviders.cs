using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aide.Infrastructure.Git;

/// <summary>
/// GitHub via its REST API (FR-10, FR-36), through the named HttpClient <see cref="HttpClientName"/> whose base address
/// is <c>GitHub:ApiBaseUrl</c> (default https://api.github.com/; set it for GitHub Enterprise or tests), and git over
/// HTTPS with the project token (FR-34, FR-35) through <see cref="GitHubRemotes"/>.
/// </summary>
internal sealed partial class GitHubGitProvider(
    IHttpClientFactory clients,
    IWorkingCopies workingCopies,
    IWorkspaceStore workspaces,
    IOptions<GitHubGitOptions> options,
    ILogger<GitHubGitProvider> logger) : IGitProvider
{
    public const string HttpClientName = "github";
    public const string ApiBaseUrlKey = "GitHub:ApiBaseUrl";
    public const string DefaultApiBaseUrl = "https://api.github.com/";
    public const string ApiVersion = "2022-11-28";

    private const int MaxReasonLength = 300;

    public GitProvider Kind => GitProvider.Github;

    public async Task<GitTestResult> TestConnectionAsync(GitConnectionSettings settings, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!ProjectSettingsRules.TryParseGitHubRepo(settings.RepoUrl, out var repo))
        {
            return GitTestResult.Failed("The repository URL isn't a GitHub repository URL (https://github.com/owner/repo).");
        }

        var client = clients.CreateClient(HttpClientName);
        var repoPath = $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}";

        bool writable;
        using (var response = await SendAsync(client, repoPath, token, cancellationToken))
        {
            if (response.Error is { } unreachable)
            {
                return GitTestResult.Failed(unreachable);
            }

            if (!response.Message!.IsSuccessStatusCode)
            {
                var reason = await DescribeFailureAsync(response.Message, cancellationToken);
                LogTestFailed(logger, settings.ProjectId, (int)response.Message.StatusCode);
                return GitTestResult.Failed(reason);
            }

            writable = await CanPushAsync(response.Message, cancellationToken);
        }

        bool branchExists;
        var branchPath = $"{repoPath}/branches/{Uri.EscapeDataString(settings.DefaultBranch)}";
        using (var response = await SendAsync(client, branchPath, token, cancellationToken))
        {
            if (response.Error is { } unreachable)
            {
                return GitTestResult.Failed(unreachable, reachable: true, writable: writable);
            }

            if (response.Message!.StatusCode == HttpStatusCode.NotFound)
            {
                branchExists = false;
            }
            else if (!response.Message.IsSuccessStatusCode)
            {
                var reason = await DescribeFailureAsync(response.Message, cancellationToken);
                return GitTestResult.Failed(reason, reachable: true, writable: writable);
            }
            else
            {
                branchExists = true;
            }
        }

        string? failure = null;
        if (!writable)
        {
            failure = string.IsNullOrEmpty(token)
                ? "No access token is saved, so AIDE can't push to the repository. Save a token with Contents: read and write."
                : "The token can't push to this repository. It needs Contents: read and write (or the repo scope).";
        }
        else if (!branchExists)
        {
            failure = $"The default branch '{settings.DefaultBranch}' doesn't exist in {repo.Owner}/{repo.Name}.";
        }

        LogTested(logger, settings.ProjectId, writable, branchExists);
        return new GitTestResult(failure is null, Reachable: true, writable, branchExists, failure);
    }

    /// <summary>
    /// FR-35: clones the repository over HTTPS with the project token into the working copy (or fetches when it is already
    /// there), then checks out the project branch, created from the remote project branch if an earlier run pushed it, else
    /// from the remote default branch.
    /// </summary>
    public async Task<GitOutcome> PrepareWorkingCopyAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Remote(settings, token) is not { } remote)
        {
            return MissingRemote(settings, token);
        }

        try
        {
            await workingCopies.SyncFromRemoteAsync(settings.ProjectId, remote, cancellationToken);
            await workingCopies.CheckoutRemoteBranchAsync(settings.ProjectId, settings.DefaultBranch, branchName, cancellationToken);
            return GitOutcome.Success;
        }
        catch (GitCommandException ex)
        {
            var reason = Scrub(remote, settings.ProjectId, ex.Message);
            LogGitFailed(logger, settings.ProjectId, "prepare", reason);
            return GitOutcome.Failed(reason, GitHubFailures.IsTransientGitError(reason));
        }
        catch (WorkingCopyException ex)
        {
            var reason = Scrub(remote, settings.ProjectId, ex.Message);
            LogGitFailed(logger, settings.ProjectId, "prepare", reason);
            return GitOutcome.Failed(reason, transient: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogGitFailed(logger, settings.ProjectId, "prepare", ex.GetType().Name);
            return GitOutcome.Failed("The working copy could not be written on the AIDE server. Ask an admin to check the server log and the Workspaces:Root volume.");
        }
    }

    /// <summary>FR-34: pushes the project branch (fast-forward only) over HTTPS with the project token.</summary>
    public async Task<GitOutcome> PublishBranchAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Remote(settings, token) is not { } remote)
        {
            return MissingRemote(settings, token);
        }

        try
        {
            var result = await workingCopies.PushBranchAsync(settings.ProjectId, branchName, remote, cancellationToken);
            if (result.Succeeded)
            {
                return GitOutcome.Success;
            }

            var reason = Scrub(remote, settings.ProjectId, $"git push failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
            LogGitFailed(logger, settings.ProjectId, "push", reason);
            return GitOutcome.Failed(reason, GitHubFailures.IsTransientGitError(reason));
        }
        catch (GitCommandException ex)
        {
            var reason = Scrub(remote, settings.ProjectId, ex.Message);
            LogGitFailed(logger, settings.ProjectId, "push", reason);
            return GitOutcome.Failed(reason, GitHubFailures.IsTransientGitError(reason));
        }
        catch (WorkingCopyException ex)
        {
            return GitOutcome.Failed(Scrub(remote, settings.ProjectId, ex.Message), transient: false);
        }
    }

    /// <summary>
    /// FR-36: reuses the open PR from the project branch to the default branch if there is one; otherwise creates it. A
    /// create that races another (422 "already exists") looks the PR up again, so exactly one PR exists.
    /// </summary>
    public async Task<PullRequestOutcome> OpenPullRequestAsync(GitConnectionSettings settings, PullRequestRequest request, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        if (!ProjectSettingsRules.TryParseGitHubRepo(settings.RepoUrl, out var repo))
        {
            return PullRequestOutcome.Failed("The repository URL isn't a GitHub repository URL (https://github.com/owner/repo).", transient: false);
        }

        if (string.IsNullOrEmpty(token))
        {
            return PullRequestOutcome.Failed("No access token is saved, so AIDE can't open the pull request. Save a token with Contents and Pull requests: read and write.", transient: false);
        }

        var client = clients.CreateClient(HttpClientName);
        var pulls = $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/pulls";
        var existing = await FindOpenPullRequestAsync(client, pulls, repo.Owner, request.HeadBranch, settings.DefaultBranch, token, cancellationToken);
        if (existing is not null)
        {
            if (!existing.Ok)
            {
                return existing;
            }

            LogPullRequestReused(logger, settings.ProjectId, existing.Number);
            return await UpdatePullRequestAsync(client, pulls, existing, request, settings.ProjectId, token, cancellationToken);
        }

        var body = new
        {
            title = request.Title,
            head = request.HeadBranch,
            @base = settings.DefaultBranch,
            body = request.Body,
            maintainer_can_modify = false,
        };
        using var sent = await SendAsync(client, HttpMethod.Post, pulls, token, cancellationToken, JsonContent.Create(body));
        if (sent.Error is { } unreachable)
        {
            return PullRequestOutcome.Failed(unreachable, transient: true);
        }

        var response = sent.Message!;
        if (response.StatusCode == HttpStatusCode.Created)
        {
            var created = await ReadPullRequestAsync(response, cancellationToken);
            LogPullRequestOpened(logger, settings.ProjectId, created.Number);
            return created;
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // Another create won the race (or GitHub already has one): use it. Anything else is GitHub's validation message.
            var raced = await FindOpenPullRequestAsync(client, pulls, repo.Owner, request.HeadBranch, settings.DefaultBranch, token, cancellationToken);
            if (raced is not null)
            {
                return raced;
            }
        }

        var reason = await DescribeFailureAsync(response, cancellationToken);
        LogPullRequestFailed(logger, settings.ProjectId, (int)response.StatusCode);
        return PullRequestOutcome.Failed(reason, GitHubFailures.IsTransientStatus(response.StatusCode, IsRateLimited(response)));
    }

    public string ComparisonBaseRef(GitConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return $"refs/remotes/{GitRemote.Name}/{settings.DefaultBranch}";
    }

    /// <summary>
    /// Brings a reused open PR's title and description up to date (they describe the plan that just completed). A transient
    /// failure retries the job; a permanent one (e.g. the token may not edit it) keeps the PR as it is.
    /// </summary>
    private async Task<PullRequestOutcome> UpdatePullRequestAsync(
        HttpClient client, string pulls, PullRequestOutcome reused, PullRequestRequest request, Guid projectId, string token, CancellationToken cancellationToken)
    {
        if (reused.Number is not { } number)
        {
            return reused;
        }

        var path = $"{pulls}/{number.ToString(CultureInfo.InvariantCulture)}";
        using var sent = await SendAsync(client, HttpMethod.Patch, path, token, cancellationToken, JsonContent.Create(new { title = request.Title, body = request.Body }));
        if (sent.Error is { } unreachable)
        {
            return PullRequestOutcome.Failed(unreachable, transient: true);
        }

        var response = sent.Message!;
        if (response.IsSuccessStatusCode)
        {
            return reused;
        }

        if (GitHubFailures.IsTransientStatus(response.StatusCode, IsRateLimited(response)))
        {
            return PullRequestOutcome.Failed(await DescribeFailureAsync(response, cancellationToken), transient: true);
        }

        LogPullRequestUpdateFailed(logger, projectId, number, (int)response.StatusCode);
        return reused;
    }

    /// <summary>The open PR for <paramref name="head"/> → <paramref name="defaultBranch"/>, a failure, or <c>null</c> when there is none.</summary>
    private static async Task<PullRequestOutcome?> FindOpenPullRequestAsync(
        HttpClient client, string pulls, string owner, string head, string defaultBranch, string token, CancellationToken cancellationToken)
    {
        var query = $"{pulls}?state=open&head={Uri.EscapeDataString($"{owner}:{head}")}&base={Uri.EscapeDataString(defaultBranch)}&per_page=1";
        using var sent = await SendAsync(client, HttpMethod.Get, query, token, cancellationToken);
        if (sent.Error is { } unreachable)
        {
            return PullRequestOutcome.Failed(unreachable, transient: true);
        }

        var response = sent.Message!;
        if (!response.IsSuccessStatusCode)
        {
            var reason = await DescribeFailureAsync(response, cancellationToken);
            return PullRequestOutcome.Failed(reason, GitHubFailures.IsTransientStatus(response.StatusCode, IsRateLimited(response)));
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var (number, url) = PullRequestFields(json.RootElement[0]);
            return PullRequestOutcome.Opened(number, url, reused: true);
        }
        catch (JsonException)
        {
            return PullRequestOutcome.Failed("GitHub returned an unreadable pull request list.", transient: true);
        }
    }

    private static async Task<PullRequestOutcome> ReadPullRequestAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var (number, url) = PullRequestFields(json.RootElement);
            return PullRequestOutcome.Opened(number, url, reused: false);
        }
        catch (JsonException)
        {
            // Created, but unreadable: the next attempt finds it as the open PR for the branch.
            return PullRequestOutcome.Failed("GitHub created the pull request but returned an unreadable reply.", transient: true);
        }
    }

    /// <summary>The PR number and its https web URL (anything else is dropped; at most 500 characters).</summary>
    private static (int? Number, string? Url) PullRequestFields(JsonElement pr)
    {
        if (pr.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        int? number = pr.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var value) ? value : null;
        string? url = pr.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String
            && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.OriginalString.Length <= 500
            ? uri.OriginalString
            : null;
        return (number, url);
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.Headers.RetryAfter is not null
        || (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.Any(v => v.Trim() == "0"));

    private GitRemote? Remote(GitConnectionSettings settings, string? token) =>
        !string.IsNullOrEmpty(token) && ProjectSettingsRules.TryParseGitHubRepo(settings.RepoUrl, out var repo)
            ? GitHubRemotes.Create(options.Value, repo, token)
            : null;

    private static GitOutcome MissingRemote(GitConnectionSettings settings, string? token) =>
        !ProjectSettingsRules.TryParseGitHubRepo(settings.RepoUrl, out _)
            ? GitOutcome.Failed("The repository URL isn't a GitHub repository URL (https://github.com/owner/repo).", transient: false)
            : GitOutcome.Failed(
                string.IsNullOrEmpty(token)
                    ? "No access token is saved, so AIDE can't clone or push. Save a token with Contents and Pull requests: read and write."
                    : "The repository can't be reached.",
                transient: false);

    /// <summary>Removes the token (in any form) and the server's working-copy paths from text shown to users or logged.</summary>
    private string Scrub(GitRemote remote, Guid projectId, string text)
    {
        var cleaned = remote.Redact(text);
        foreach (var path in new[] { workspaces.PathFor(projectId), workspaces.Root })
        {
            cleaned = cleaned.Replace(path, "<working copy>", StringComparison.OrdinalIgnoreCase)
                .Replace(path.Replace('\\', '/'), "<working copy>", StringComparison.OrdinalIgnoreCase);
        }

        return Clean(cleaned);
    }

    private static Task<Sent> SendAsync(HttpClient client, string path, string? token, CancellationToken cancellationToken) =>
        SendAsync(client, HttpMethod.Get, path, token, cancellationToken);

    private static async Task<Sent> SendAsync(HttpClient client, HttpMethod method, string path, string? token, CancellationToken cancellationToken, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            return new Sent(await client.SendAsync(request, cancellationToken), null);
        }
        catch (HttpRequestException ex)
        {
            return new Sent(null, $"Could not reach GitHub: {Clean(ex.Message)}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Sent(null, "Could not reach GitHub: the request timed out.");
        }
    }

    private static async Task<bool> CanPushAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return json.RootElement.TryGetProperty("permissions", out var permissions)
                && permissions.ValueKind == JsonValueKind.Object
                && permissions.TryGetProperty("push", out var push)
                && push.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>"GitHub: {message} (HTTP {status})" from GitHub's error body, plus a hint for the usual cases.</summary>
    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? message = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String)
            {
                message = Clean(m.GetString());
            }
        }
        catch (JsonException)
        {
        }

        var status = (int)response.StatusCode;
        var reason = $"GitHub: {message ?? response.ReasonPhrase ?? "request failed"} (HTTP {status})";
        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " The token is invalid or expired.",
            HttpStatusCode.NotFound => " The repository doesn't exist, or the token can't access it.",
            HttpStatusCode.Forbidden => " The token lacks access, or the API rate limit was hit.",
            _ => string.Empty,
        };
        return reason + hint;
    }

    private static string Clean(string? text)
    {
        var cleaned = new string((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > MaxReasonLength ? cleaned[..MaxReasonLength] : cleaned;
    }

    private sealed record Sent(HttpResponseMessage? Message, string? Error) : IDisposable
    {
        public void Dispose() => Message?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub connection test for project {ProjectId}: writable={Writable}, default branch exists={BranchExists}")]
    private static partial void LogTested(ILogger logger, Guid projectId, bool writable, bool branchExists);

    [LoggerMessage(Level = LogLevel.Information, Message = "GitHub connection test for project {ProjectId} failed with HTTP {Status}")]
    private static partial void LogTestFailed(ILogger logger, Guid projectId, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Git {Operation} for GitHub project {ProjectId} failed: {Reason}")]
    private static partial void LogGitFailed(ILogger logger, Guid projectId, string operation, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened pull request #{Number} for project {ProjectId}")]
    private static partial void LogPullRequestOpened(ILogger logger, Guid projectId, int? number);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reusing open pull request #{Number} for project {ProjectId}")]
    private static partial void LogPullRequestReused(ILogger logger, Guid projectId, int? number);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Updating the description of pull request #{Number} for project {ProjectId} failed with HTTP {Status}; it is reused as it is")]
    private static partial void LogPullRequestUpdateFailed(ILogger logger, Guid projectId, int number, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Opening the pull request for project {ProjectId} failed with HTTP {Status}")]
    private static partial void LogPullRequestFailed(ILogger logger, Guid projectId, int status);
}

/// <summary>
/// The Local provider (D12): repositories and working copies live under the workspace root on the data volume, so
/// the flow works offline. The test passes when the root is writable and the git CLI runs. The default branch is
/// created with the local repository, so it always "exists".
/// </summary>
internal sealed partial class LocalGitProvider(IWorkspaceStore workspaces, IGitCli git, IWorkingCopies workingCopies, ILogger<LocalGitProvider> logger) : IGitProvider
{
    public GitProvider Kind => GitProvider.Local;

    public async Task<GitTestResult> TestConnectionAsync(GitConnectionSettings settings, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = workspaces.Root;
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".aide-write-test-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok", cancellationToken);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotWritable(logger, ex, settings.ProjectId, root);
            // The server path stays in the log; the reason shown to the project Owner is generic.
            return GitTestResult.Failed("The workspace folder on the AIDE server isn't writable. Ask an admin to check the Workspaces:Root setting and its volume.");
        }

        if (await git.GetVersionAsync(cancellationToken) is null)
        {
            return GitTestResult.Failed(
                "The git command-line tool isn't installed on the AIDE server, so local repositories can't be created.",
                reachable: true,
                writable: true,
                defaultBranchExists: true);
        }

        return new GitTestResult(true, true, true, true, null);
    }

    /// <summary>
    /// FR-35 (D12): the repository is the working copy itself, on the data volume: created with an empty initial commit on
    /// the default branch, then the project branch is created from it and checked out.
    /// </summary>
    public async Task<GitOutcome> PrepareWorkingCopyAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            await workingCopies.InitAsync(settings.ProjectId, settings.DefaultBranch, cancellationToken);
            await workingCopies.CheckoutBranchAsync(settings.ProjectId, settings.DefaultBranch, branchName, cancellationToken);
            return GitOutcome.Success;
        }
        catch (Exception ex) when (ex is GitCommandException or WorkingCopyException or IOException or UnauthorizedAccessException)
        {
            LogPrepareFailed(logger, ex, settings.ProjectId);
            // The server path stays in the log; the reason shown to users is generic.
            return GitOutcome.Failed("The local repository could not be prepared on the AIDE server. Ask an admin to check the server log and the Workspaces:Root volume.");
        }
    }

    /// <summary>A Local repository has no remote: the commit is already where it belongs.</summary>
    public Task<GitOutcome> PublishBranchAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken) =>
        Task.FromResult(GitOutcome.Success);

    /// <summary>FR-35 (D12): a Local project has no PR to open; its "PR" is the branch comparison the caller records.</summary>
    public Task<PullRequestOutcome> OpenPullRequestAsync(GitConnectionSettings settings, PullRequestRequest request, string? token, CancellationToken cancellationToken) =>
        Task.FromResult(PullRequestOutcome.Opened(null, null, reused: false));

    public string ComparisonBaseRef(GitConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return $"refs/heads/{settings.DefaultBranch}";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preparing the local repository of project {ProjectId} failed")]
    private static partial void LogPrepareFailed(ILogger logger, Exception exception, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workspace root {Root} is not writable (project {ProjectId})")]
    private static partial void LogNotWritable(ILogger logger, Exception exception, Guid projectId, string root);
}
