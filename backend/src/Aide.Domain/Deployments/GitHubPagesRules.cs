using System.Globalization;
using Aide.Domain.Common;
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

    /// <summary>GitHub refuses repository variables with this prefix (422).</summary>
    public const string ReservedVariablePrefix = "GITHUB_";

    /// <summary>Tevrin's id for the Pages deployment of a commit (Pages has no deployment Tevrin creates).</summary>
    public static string DeploymentId(string sha)
    {
        ArgumentNullException.ThrowIfNull(sha);
        return $"pages:{sha.ToLowerInvariant()}";
    }

    /// <summary>The commit of a <see cref="DeploymentId"/>, or <c>null</c>.</summary>
    public static string? ShaOf(string deploymentId) =>
        deploymentId is not null && deploymentId.StartsWith("pages:", StringComparison.Ordinal) && deploymentId.Length > 6 ? deploymentId[6..] : null;

    /// <summary>Tevrin's id of a synced (key, environment): <c>pages:&lt;environment&gt;:&lt;key&gt;</c>. Only production has a variable.</summary>
    public static string EnvId(string key, DeploymentEnvironment environment) =>
        string.Create(CultureInfo.InvariantCulture, $"pages:{environment.ToWire()}:{key}");

    /// <summary>The repository variable an <see cref="EnvId"/> stands for: its key when production, else <c>null</c> (Pages has no previews).</summary>
    public static string? ProductionKeyOf(string? id)
    {
        const string prefix = "pages:production:";
        return id is not null && id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length ? id[prefix.Length..] : null;
    }

    /// <summary>
    /// FR-145, FR-146, §4.22: why a variable can't become a repository Actions variable, or <c>null</c> when it can. Those
    /// variables are readable by anyone who can read the repository and end up in a public bundle, so only public,
    /// non-sensitive values that don't look secret go there.
    /// </summary>
    public static string? EnvRefusal(string key, string? value, bool isPublic, bool sensitive)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (sensitive)
        {
            return "GitHub Pages has no runtime and no place for secrets, so this sensitive variable isn't sent to it.";
        }

        if (!isPublic)
        {
            return "GitHub Pages only takes public build-time variables, so this variable isn't sent to it. Mark it public if the frontend's build needs it.";
        }

        if (key.StartsWith(ReservedVariablePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub reserves variable names starting with GITHUB_, so this variable isn't sent to GitHub Pages. Rename it.";
        }

        return EnvInventoryRules.LooksSecret(key, value)
            ? "This public variable looks like a secret, so it isn't sent to GitHub Pages: secret values can't be public."
            : null;
    }

    /// <summary>
    /// FR-145, FR-146: whether the Pages workflow run on the merge commit started before the newest variable sync, so its
    /// build may not hold those values (the run reads <c>vars.X</c> when its job starts). A run still queued reads them when
    /// it starts. A run already re-run isn't re-run again: GitHub's and Tevrin's clocks differ, and that must not loop.
    /// </summary>
    public static bool BuildPredatesVariables(string? runStatus, DateTimeOffset? runStartedAt, int runAttempt, DateTimeOffset variablesSyncedAt) =>
        runStartedAt is { } started
        && started < variablesSyncedAt
        && runAttempt <= 1
        && runStatus is not ("queued" or "requested" or "pending");

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
