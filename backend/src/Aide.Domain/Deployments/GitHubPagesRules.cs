using Aide.Domain.Entities;

namespace Aide.Domain.Deployments;

/// <summary>
/// FR-143, FR-149, §4.20 GitHub Pages: a production deployment is the Pages workflow run on the merge commit, then the Pages
/// deployment of that commit (<c>GET /repos/{o}/{r}/pages/deployments/{sha}</c>). Pure.
/// </summary>
public static class GitHubPagesRules
{
    /// <summary>The workflow the deploy-engineer writes (§4.18 ownership).</summary>
    public const string WorkflowPath = ".github/workflows/pages.yml";

    /// <summary>AIDE's id for the Pages deployment of a commit (Pages has no deployment AIDE creates).</summary>
    public static string DeploymentId(string sha)
    {
        ArgumentNullException.ThrowIfNull(sha);
        return $"pages:{sha.ToLowerInvariant()}";
    }

    /// <summary>The commit of a <see cref="DeploymentId"/>, or <c>null</c>.</summary>
    public static string? ShaOf(string deploymentId) =>
        deploymentId is not null && deploymentId.StartsWith("pages:", StringComparison.Ordinal) && deploymentId.Length > 6 ? deploymentId[6..] : null;

    /// <summary>
    /// The state from the workflow run (<paramref name="runStatus"/>/<paramref name="runConclusion"/>; <c>null</c>: no run yet)
    /// and the Pages deployment status (<paramref name="pagesStatus"/>; <c>null</c>: not registered yet). §4.20 mapping:
    /// <c>deployment_in_progress</c>, <c>syncing_files</c>, <c>updating_pages</c> → building; <c>succeed</c> → ready; other
    /// failures → error. A failed or cancelled run is an error (Pages never deploys it).
    /// </summary>
    public static DeploymentStatus StateOf(string? runStatus, string? runConclusion, string? pagesStatus)
    {
        if (runStatus is null)
        {
            return DeploymentStatus.Queued;
        }

        if (runStatus != "completed")
        {
            return runStatus is "queued" or "requested" or "waiting" or "pending" ? DeploymentStatus.Queued : DeploymentStatus.Building;
        }

        if (runConclusion is not "success")
        {
            return runConclusion is "cancelled" or "skipped" ? DeploymentStatus.Canceled : DeploymentStatus.Error;
        }

        return pagesStatus switch
        {
            null => DeploymentStatus.Building,
            "succeed" => DeploymentStatus.Ready,
            "deployment_in_progress" or "syncing_files" or "updating_pages" or "queued" => DeploymentStatus.Building,
            _ => DeploymentStatus.Error,
        };
    }
}
