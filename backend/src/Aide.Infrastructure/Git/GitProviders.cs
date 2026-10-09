using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aide.Domain.Entities;
using Aide.Domain.Projects;
using Microsoft.Extensions.Logging;

namespace Aide.Infrastructure.Git;

/// <summary>
/// GitHub via its REST API (FR-10, FR-35), through the named HttpClient <see cref="HttpClientName"/> whose base address
/// is <c>GitHub:ApiBaseUrl</c> (default https://api.github.com/; set it for GitHub Enterprise or tests).
/// </summary>
internal sealed partial class GitHubGitProvider(IHttpClientFactory clients, ILogger<GitHubGitProvider> logger) : IGitProvider
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

    /// <summary>Phase 9 (FR-35): clone over HTTPS with the project token into the working copy.</summary>
    public Task<GitOutcome> PrepareWorkingCopyAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken) =>
        Task.FromResult(GitOutcome.Failed("Execution with the GitHub provider is not available yet; use the Local provider."));

    /// <summary>Phase 9 (FR-34): push the project branch.</summary>
    public Task<GitOutcome> PublishBranchAsync(GitConnectionSettings settings, string branchName, string? token, CancellationToken cancellationToken) =>
        Task.FromResult(GitOutcome.Failed("Pushing to GitHub is not available yet."));

    private static async Task<Sent> SendAsync(HttpClient client, string path, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preparing the local repository of project {ProjectId} failed")]
    private static partial void LogPrepareFailed(ILogger logger, Exception exception, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workspace root {Root} is not writable (project {ProjectId})")]
    private static partial void LogNotWritable(ILogger logger, Exception exception, Guid projectId, string root);
}
