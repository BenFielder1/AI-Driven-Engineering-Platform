using System.Text.Json;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.GitHub;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// FR-143, §4.20 GitHub Pages through the project's GitHub App installation token or PAT (<see cref="DeploymentAuth.Token"/>):
/// setup enables Pages with <c>POST /repos/{o}/{r}/pages {build_type: "workflow"}</c> (an existing site is switched to the
/// workflow build); there are no previews and no rollback. A production deployment is the Pages workflow run on the commit
/// (<c>GET …/actions/runs?head_sha=</c>), then <c>GET …/pages/deployments/{sha}</c>, with the URL from <c>GET …/pages</c>
/// (<c>html_url</c>). The build log is the failed run's URL. Only for GitHub repositories.
/// </summary>
public sealed class GitHubPagesDeploymentProvider(GitHubApi api) : IDeploymentProvider
{
    private const DeploymentProvider Pages = DeploymentProvider.GithubPages;

    public DeploymentProvider Kind => Pages;

    public DeploymentProvider Platform => Pages;

    public DeploymentCapabilities Capabilities => DeploymentPlatforms.Capabilities(Pages);

    public Task<DeploymentAccount> ValidateConnectionAsync(DeploymentAuth auth, CancellationToken cancellationToken) =>
        Task.FromResult(new DeploymentAccount("github", null, null, null, VercelTokenScope.Account, null));

    public Task<IReadOnlyList<DeploymentResource>> ListResourcesAsync(DeploymentAuth auth, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeploymentResource>>([]);

    public async Task<DeploymentSetupResult> SetupTargetAsync(DeploymentAuth auth, DeploymentSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(request);
        var (owner, repo) = Repo(request.Target);
        var path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pages";
        string? url;
        try
        {
            using var created = await Call(() => api.SendAsync(HttpMethod.Post, path, auth.Token, new { build_type = "workflow" }, cancellationToken));
            url = Text(created.RootElement, "html_url");
        }
        catch (DeploymentProviderException ex) when (ex.Status == 409)
        {
            // Pages is enabled already: make sure it builds from the workflow.
            using var _ = await Call(() => api.SendAsync(HttpMethod.Put, path, auth.Token, new { build_type = "workflow" }, cancellationToken));
            using var site = await Call(() => api.SendAsync(HttpMethod.Get, path, auth.Token, null, cancellationToken));
            url = Text(site.RootElement, "html_url");
        }

        return new DeploymentSetupResult($"{owner}/{repo}", repo, url?.TrimEnd('/'), null, null);
    }

    public async Task<ProviderTargetCheck> VerifyTargetAsync(DeploymentAuth auth, DeploymentTargetRef target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        var (owner, repo) = Repo(target);
        using var site = await Call(() => api.GetOrNullAsync($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pages", auth.Token, cancellationToken));
        if (site is null)
        {
            return new ProviderTargetCheck(false, "GitHub Pages isn't enabled for the repository; set the target up again.", null);
        }

        return Text(site.RootElement, "build_type") == "workflow"
            ? new ProviderTargetCheck(true, null, null)
            : new ProviderTargetCheck(false, "GitHub Pages builds from a branch, not the Pages workflow; set the target up again.", null);
    }

    /// <summary>§4.20: GitHub Pages has no environment variables of its own (build values live in the workflow).</summary>
    public Task<IReadOnlyList<DeploymentEnvWritten>> SyncEnvAsync(DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvSyncRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeploymentEnvWritten>>([]);

    /// <summary>Production only: the push of the merge commit runs the Pages workflow, so AIDE follows that commit's run.</summary>
    public Task<ProviderDeployment> DeployAsync(DeploymentAuth auth, DeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Environment != DeploymentEnvironment.Production)
        {
            throw new DeploymentProviderException(DeploymentFailure.NotSupported, null, "GitHub Pages has no preview deployments.", platform: Pages);
        }

        return Task.FromResult(new ProviderDeployment(GitHubPagesRules.DeploymentId(request.GitSha), null, null, null, null, State: DeploymentStatus.Queued));
    }

    public async Task<ProviderDeployment> GetDeploymentAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        var sha = GitHubPagesRules.ShaOf(deploymentId) ?? throw new DeploymentProviderException(DeploymentFailure.NotFound, 404, "Not a GitHub Pages deployment.", platform: Pages);
        var (owner, repo) = Repo(target);
        var basePath = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        var run = await RunAsync(auth, basePath, sha, cancellationToken);
        string? pagesStatus = null;
        string? url = null;
        if (run is { Status: "completed", Conclusion: "success" })
        {
            using var deployment = await Call(() => api.GetOrNullAsync($"{basePath}/pages/deployments/{Uri.EscapeDataString(sha)}", auth.Token, cancellationToken));
            pagesStatus = deployment is null ? null : Text(deployment.RootElement, "status");
            if (pagesStatus == "succeed")
            {
                using var site = await Call(() => api.GetOrNullAsync($"{basePath}/pages", auth.Token, cancellationToken));
                url = site is null ? null : Text(site.RootElement, "html_url")?.TrimEnd('/');
            }
        }

        var state = GitHubPagesRules.StateOf(run?.Status, run?.Conclusion, pagesStatus);
        var error = state == DeploymentStatus.Error
            ? run?.Conclusion is { } c and not "success" ? $"The Pages workflow run ended with {c}." : $"The Pages deployment failed ({pagesStatus})."
            : null;
        return new ProviderDeployment(deploymentId, url, run?.HtmlUrl, null, error, url, state);
    }

    public async Task<string?> GetBuildLogTailAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        var sha = GitHubPagesRules.ShaOf(deploymentId);
        if (sha is null)
        {
            return null;
        }

        var (owner, repo) = Repo(target);
        var run = await RunAsync(auth, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}", sha, cancellationToken);
        return run?.HtmlUrl is { } link ? $"The Pages workflow run failed. Its log: {link}" : null;
    }

    public async Task<ProviderDeployment?> FindDeploymentForShaAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvironment environment, string sha, Guid? aideDeploymentId, DateTimeOffset? createdAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (environment != DeploymentEnvironment.Production || aideDeploymentId is not null)
        {
            return null;
        }

        var (owner, repo) = Repo(target);
        var run = await RunAsync(auth, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}", sha, cancellationToken);
        return run is null ? null : new ProviderDeployment(GitHubPagesRules.DeploymentId(sha), null, run.HtmlUrl, null, null, State: GitHubPagesRules.StateOf(run.Status, run.Conclusion, null));
    }

    public Task CancelAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RollbackAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken) =>
        throw new DeploymentProviderException(DeploymentFailure.NotSupported, null, "GitHub Pages has no rollback.", platform: Pages);

    private sealed record WorkflowRun(string? Status, string? Conclusion, string? HtmlUrl);

    /// <summary>The Pages workflow's newest run on <paramref name="sha"/> (by its path, else a run named like Pages), or <c>null</c>.</summary>
    private async Task<WorkflowRun?> RunAsync(DeploymentAuth auth, string basePath, string sha, CancellationToken cancellationToken)
    {
        using var runs = await Call(() => api.SendAsync(
            HttpMethod.Get, $"{basePath}/actions/runs?head_sha={Uri.EscapeDataString(sha)}&per_page=20", auth.Token, null, cancellationToken));
        if (!runs.RootElement.TryGetProperty("workflow_runs", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = list.EnumerateArray().ToList();
        var run = items.FirstOrDefault(r => Text(r, "path") == GitHubPagesRules.WorkflowPath);
        if (run.ValueKind != JsonValueKind.Object)
        {
            run = items.FirstOrDefault(r => Text(r, "name")?.Contains("pages", StringComparison.OrdinalIgnoreCase) ?? false);
        }

        return run.ValueKind == JsonValueKind.Object ? new WorkflowRun(Text(run, "status"), Text(run, "conclusion"), Text(run, "html_url")) : null;
    }

    private static (string Owner, string Repo) Repo(DeploymentTargetRef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Repo is { Host: "github", Owner: { } owner, Name: { } name }
            ? (owner, name)
            : throw new DeploymentProviderException(DeploymentFailure.Rejected, null, "GitHub Pages needs a GitHub repository.", platform: Pages);
    }

    /// <summary>GitHub's failures as deployment failures: 403 is the missing Pages permission, a rate limit waits for its reset.</summary>
    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (GitHubRateLimitedException ex)
        {
            throw new DeploymentProviderException(DeploymentFailure.RateLimited, 429, "GitHub's rate limit was reached.", retryAt: ex.ResetAt, platform: Pages);
        }
        catch (GitHubApiException ex)
        {
            var failure = ex.Kind switch
            {
                GitHubFailureKind.Unauthorized => DeploymentFailure.CredentialRejected,
                GitHubFailureKind.Forbidden => DeploymentFailure.PermissionMissing,
                GitHubFailureKind.NotFound => DeploymentFailure.NotFound,
                GitHubFailureKind.Upstream => DeploymentFailure.Upstream,
                _ => DeploymentFailure.Rejected,
            };
            throw new DeploymentProviderException(failure, ex.Status, ex.Message, platform: Pages);
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}
