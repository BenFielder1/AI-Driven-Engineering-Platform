using Aide.Domain.Common;
using Aide.Domain.Entities;

namespace Aide.Domain.Deployments;

/// <summary>FR-150, §4.20: how a platform makes an earlier deployment current.</summary>
public enum RollbackMethod
{
    /// <summary>The platform can't (GitHub Pages): <c>DEPLOYMENT_ACTION_UNSUPPORTED</c>.</summary>
    None = 0,

    /// <summary>Vercel: <c>POST /v10/projects/{id}/promote/{deploymentId}</c>.</summary>
    VercelPromote = 1,

    /// <summary>Netlify: <c>POST /sites/{id}/deploys/{deploy_id}/restore</c>.</summary>
    NetlifyRestore = 2,

    /// <summary>Cloudflare Workers: deploy the build's Worker version at 100 %.</summary>
    CloudflareWorkersVersion = 3,

    /// <summary>Cloudflare Pages: <c>POST …/deployments/{id}/rollback</c>.</summary>
    CloudflarePagesRollback = 4,

    /// <summary>Render: <c>POST /v1/services/{id}/rollback {deployId}</c> (M30).</summary>
    RenderRollback = 5,

    /// <summary>Railway: <c>deploymentRollback(id)</c> when <c>canRollback</c> (M30).</summary>
    RailwayRollback = 6,

    /// <summary>The Fake of a platform that can roll back: re-points instantly.</summary>
    Fake = 7,
}

/// <summary>
/// FR-150, FR-151: rollback per provider capability, which deployments are valid rollback targets, and the Cloudflare mode
/// of a target. Pure.
/// </summary>
public static class DeploymentRollbackRules
{
    /// <summary>The resource id prefix of a Cloudflare target in Pages mode (adopt only); a Workers target's id is the script name.</summary>
    public const string CloudflarePagesPrefix = "pages:";

    /// <summary>Reason shown when the platform has no rollback.</summary>
    public const string ReasonUnsupported = "unsupported";

    /// <summary>Reason shown for a preview deployment.</summary>
    public const string ReasonNotProduction = "not_production";

    /// <summary>Reason shown for a deployment that isn't <c>ready</c>.</summary>
    public const string ReasonNotReady = "not_ready";

    /// <summary>Reason shown for the deployment production already serves.</summary>
    public const string ReasonCurrent = "current";

    /// <summary>Reason shown when the platform never reported the deployment (no provider id).</summary>
    public const string ReasonUnknownAtPlatform = "unknown_at_platform";

    /// <summary>Whether a Cloudflare target is in Pages mode (its resource id is <c>pages:&lt;project&gt;</c>).</summary>
    public static bool IsCloudflarePages(string? resourceId) =>
        resourceId is not null && resourceId.StartsWith(CloudflarePagesPrefix, StringComparison.Ordinal) && resourceId.Length > CloudflarePagesPrefix.Length;

    /// <summary>The Pages project name of a Pages-mode resource id, else <c>null</c>.</summary>
    public static string? CloudflarePagesProject(string? resourceId) =>
        IsCloudflarePages(resourceId) ? resourceId![CloudflarePagesPrefix.Length..] : null;

    /// <summary>The resource id of the Pages project <paramref name="project"/>.</summary>
    public static string CloudflarePagesResourceId(string project)
    {
        ArgumentException.ThrowIfNullOrEmpty(project);
        return CloudflarePagesPrefix + project;
    }

    /// <summary>FR-150: the method of <paramref name="platform"/> (the Fake of a platform keeps its platform's capability).</summary>
    public static RollbackMethod MethodFor(DeploymentProvider platform, string? resourceId, bool fake = false)
    {
        if (!DeploymentPlatforms.Capabilities(platform).Rollback)
        {
            return RollbackMethod.None;
        }

        if (fake)
        {
            return RollbackMethod.Fake;
        }

        return platform switch
        {
            DeploymentProvider.Vercel or DeploymentProvider.Fake => RollbackMethod.VercelPromote,
            DeploymentProvider.Netlify => RollbackMethod.NetlifyRestore,
            DeploymentProvider.Cloudflare => IsCloudflarePages(resourceId) ? RollbackMethod.CloudflarePagesRollback : RollbackMethod.CloudflareWorkersVersion,
            DeploymentProvider.Render => RollbackMethod.RenderRollback,
            DeploymentProvider.Railway => RollbackMethod.RailwayRollback,
            _ => RollbackMethod.None,
        };
    }

    /// <summary>FR-150: the trigger of the recorded row: <c>promote</c> on Vercel (v2 wording), else <c>rollback</c>.</summary>
    public static DeploymentTrigger TriggerFor(DeploymentProvider platform) =>
        platform is DeploymentProvider.Vercel or DeploymentProvider.Fake ? DeploymentTrigger.Promote : DeploymentTrigger.Rollback;

    /// <summary>
    /// FR-151: why a deployment can't be rolled back to (<c>null</c>: it can). Order: the platform has no
    /// rollback, a preview, not ready, unknown at the platform, already current.
    /// </summary>
    public static string? BlockedReason(DeploymentProvider platform, DeploymentEnvironment environment, DeploymentStatus status, string? providerDeploymentId, bool isCurrent)
    {
        if (!DeploymentPlatforms.Capabilities(platform).Rollback)
        {
            return ReasonUnsupported;
        }

        if (environment != DeploymentEnvironment.Production)
        {
            return ReasonNotProduction;
        }

        if (status != DeploymentStatus.Ready)
        {
            return ReasonNotReady;
        }

        if (string.IsNullOrEmpty(providerDeploymentId))
        {
            return ReasonUnknownAtPlatform;
        }

        return isCurrent ? ReasonCurrent : null;
    }

    /// <summary>
    /// FR-150 (409): <c>DEPLOYMENT_ACTION_UNSUPPORTED</c> when the platform has no rollback, else <c>INVALID_DEPLOYMENT</c>
    /// for a deployment that isn't a valid target. Promoting the current deployment again is allowed on Vercel (v2).
    /// </summary>
    public static Result CanRollback(DeploymentProvider platform, DeploymentEnvironment environment, DeploymentStatus status, string? providerDeploymentId, bool isCurrent)
    {
        var reason = BlockedReason(platform, environment, status, providerDeploymentId, isCurrent && platform is not (DeploymentProvider.Vercel or DeploymentProvider.Fake));
        return reason switch
        {
            null => Result.Success(),
            ReasonUnsupported => Result.Failure(new DomainError(ErrorCodes.DeploymentActionUnsupported, $"{DeploymentPlatforms.DisplayName(platform)} doesn't support this action.")),
            ReasonNotProduction => Result.Failure(new DomainError(ErrorCodes.InvalidDeployment, "Only a production deployment can be rolled back to.")),
            ReasonNotReady => Result.Failure(new DomainError(ErrorCodes.InvalidDeployment, "Only a ready deployment can be rolled back to.")),
            ReasonUnknownAtPlatform => Result.Failure(new DomainError(ErrorCodes.InvalidDeployment, "The platform never reported this deployment, so it can't be rolled back to.")),
            _ => Result.Failure(new DomainError(ErrorCodes.InvalidDeployment, "This deployment is already the current production deployment.")),
        };
    }

    /// <summary>
    /// FR-150 seam (M30): whether the deployment is older than the latest migration applied to its environment's database.
    /// Databases arrive in M30, so this is <c>null</c> (unknown) until then.
    /// </summary>
    public static bool? OlderThanLatestMigration(DateTimeOffset deploymentCreatedAt, DateTimeOffset? latestMigrationAppliedAt) =>
        latestMigrationAppliedAt is { } applied ? deploymentCreatedAt < applied : null;
}
