using System.Text;
using System.Text.Json;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Platforms;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// Render deploys (§4.20): preview = <c>PATCH</c> the <c>-preview</c> service's branch, then
/// <c>POST /v1/services/{id}/deploys {commitId}</c> (SHA-pinned); production adopts the auto-deploy of the merge SHA, else
/// the same <c>POST</c>. Rollback <c>POST /v1/services/{id}/rollback {deployId}</c>; logs <c>GET /v1/logs</c> (build).
/// AIDE deployment ids are <c>service|deploy</c> (<see cref="RenderRules.DeploymentId"/>).
/// </summary>
public sealed partial class RenderDeploymentProvider
{
    public async Task<ProviderDeployment> DeployAsync(DeploymentAuth auth, DeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var serviceId = ServiceOf(request.Target, request.Environment);
        if (request.Environment == DeploymentEnvironment.Preview)
        {
            // FR-147: the preview service follows the version branch; the deploy itself is pinned to the PR head.
            using var _ = await SendAsync(auth, HttpMethod.Patch, $"services/{Uri.EscapeDataString(serviceId)}", new { branch = request.GitRef }, cancellationToken);
        }

        using var created = await SendAsync(
            auth, HttpMethod.Post, $"services/{Uri.EscapeDataString(serviceId)}/deploys", new { commitId = request.GitSha, clearCache = "do_not_clear" }, cancellationToken);
        var deployId = Text(created.RootElement, "id")
            ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Render started the deploy but returned no id.", platform: Render);
        return ToDeployment(created.RootElement, serviceId, deployId, null, request.Environment == DeploymentEnvironment.Production);
    }

    public async Task<ProviderDeployment> GetDeploymentAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        var (serviceId, deployId) = RenderRules.ParseDeploymentId(deploymentId, target.ResourceId);
        using var deploy = await SendAsync(auth, HttpMethod.Get, $"services/{Uri.EscapeDataString(serviceId)}/deploys/{Uri.EscapeDataString(deployId)}", null, cancellationToken);
        string? url = null;
        if (RenderRules.StateOf(Text(deploy.RootElement, "status")) == DeploymentStatus.Ready)
        {
            url = (await GetServiceAsync(auth, serviceId, cancellationToken)).Url;
        }

        return ToDeployment(deploy.RootElement, serviceId, deployId, url, serviceId == target.ResourceId);
    }

    /// <summary>
    /// The deploy's build log (oldest line first) for the deploy's time span: <c>GET /v1/logs?ownerId&amp;resource&amp;type=build
    /// &amp;startTime&amp;endTime&amp;direction=backward&amp;limit=100</c>, after the per-user limit allows it. A failed
    /// pre-deploy command is named on the first line (FR-147: a migration finding, not a build one).
    /// </summary>
    public async Task<string?> GetBuildLogTailAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        var (serviceId, deployId) = RenderRules.ParseDeploymentId(deploymentId, target.ResourceId);
        string? status;
        DateTimeOffset? started;
        DateTimeOffset? finished;
        using (var deploy = await SendAsync(auth, HttpMethod.Get, $"services/{Uri.EscapeDataString(serviceId)}/deploys/{Uri.EscapeDataString(deployId)}", null, cancellationToken))
        {
            status = Text(deploy.RootElement, "status");
            started = Json.Time(deploy.RootElement, "createdAt");
            finished = Json.Time(deploy.RootElement, "finishedAt") ?? Json.Time(deploy.RootElement, "updatedAt");
        }

        if (auth.AccountId is not { } owner || started is not { } from)
        {
            return RenderRules.IsPreDeployFailure(status) ? RenderRules.PreDeployFailedMessage : null;
        }

        logLimiter.Take(auth.Token);
        var to = finished is { } end && end > from ? end : from + TimeSpan.FromMinutes(30);
        var query = $"logs?ownerId={Uri.EscapeDataString(owner)}&resource={Uri.EscapeDataString(serviceId)}&type=build"
            + $"&startTime={Uri.EscapeDataString(RenderRules.LogTime(from))}&endTime={Uri.EscapeDataString(RenderRules.LogTime(to))}&direction=backward&limit=100";
        using var logs = await SendAsync(auth, HttpMethod.Get, query, null, cancellationToken);
        var lines = Items(logs.RootElement, "logs").Select(l => Text(l, "message")).OfType<string>().Reverse().ToList();
        var text = new StringBuilder();
        if (RenderRules.IsPreDeployFailure(status))
        {
            text.AppendLine(RenderRules.PreDeployFailedMessage);
        }

        foreach (var line in lines)
        {
            text.AppendLine(line);
        }

        return text.Length == 0 ? null : text.ToString().TrimEnd();
    }

    public async Task<ProviderDeployment?> FindDeploymentForShaAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvironment environment, string sha, Guid? aideDeploymentId, DateTimeOffset? createdAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (aideDeploymentId is null && environment != DeploymentEnvironment.Production)
        {
            return null;
        }

        var serviceId = environment == DeploymentEnvironment.Production ? target.ResourceId : target.PreviewResourceId;
        if (serviceId is null)
        {
            return null;
        }

        using var list = await SendAsync(auth, HttpMethod.Get, $"services/{Uri.EscapeDataString(serviceId)}/deploys?limit=20", null, cancellationToken);
        foreach (var item in Items(list.RootElement))
        {
            var deploy = Property(item, "deploy").ValueKind == JsonValueKind.Object ? Property(item, "deploy") : item;
            if (Text(deploy, "id") is not { } deployId || !NetlifyRules.SameCommit(Text(Property(deploy, "commit"), "id"), sha))
            {
                continue;
            }

            // Render can't tag a deploy with the AIDE row: a retry adopts only an API deploy created after the row; the
            // release adopts Render's own deploy of the merge commit (FR-149), never a canceled one.
            var created = Json.Time(deploy, "createdAt");
            var trigger = Text(deploy, "trigger");
            var state = RenderRules.StateOf(Text(deploy, "status"));
            var matches = aideDeploymentId is not null
                ? trigger is "api" or "deploy_hook" && (createdAfter is null || created is null || created >= createdAfter)
                : state != DeploymentStatus.Canceled;
            if (matches)
            {
                return ToDeployment(deploy, serviceId, deployId, null, environment == DeploymentEnvironment.Production);
            }
        }

        return null;
    }

    public async Task CancelAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        var (serviceId, deployId) = RenderRules.ParseDeploymentId(deploymentId, target.ResourceId);
        try
        {
            using var _ = await SendAsync(auth, HttpMethod.Post, $"services/{Uri.EscapeDataString(serviceId)}/deploys/{Uri.EscapeDataString(deployId)}/cancel", null, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure is DeploymentFailure.NotFound or DeploymentFailure.Rejected)
        {
            // Gone or finished.
        }
    }

    /// <summary>FR-150: <c>POST /v1/services/{id}/rollback {deployId}</c>; Render starts a rollback deploy of the earlier build.</summary>
    public async Task RollbackAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        var (serviceId, deployId) = RenderRules.ParseDeploymentId(deploymentId, target.ResourceId);
        using var _ = await SendAsync(auth, HttpMethod.Post, $"services/{Uri.EscapeDataString(serviceId)}/rollback", new { deployId }, cancellationToken);
    }

    private static string ServiceOf(DeploymentTargetRef target, DeploymentEnvironment environment) =>
        (environment == DeploymentEnvironment.Production ? target.ResourceId : target.PreviewResourceId)
        ?? throw new DeploymentProviderException(
            DeploymentFailure.Rejected,
            400,
            environment == DeploymentEnvironment.Production
                ? "The Render service isn't set up; set the target up in Settings → Deployment."
                : "The Render preview service is missing; set the target up again in Settings → Deployment.",
            platform: DeploymentProvider.Render);

    private static ProviderDeployment ToDeployment(JsonElement deploy, string serviceId, string deployId, string? url, bool production)
    {
        var status = Text(deploy, "status");
        var state = RenderRules.StateOf(status);
        string? error = state == DeploymentStatus.Error
            ? RenderRules.IsPreDeployFailure(status) ? RenderRules.PreDeployFailedMessage : $"Render: the deploy failed ({status})."
            : null;
        return new ProviderDeployment(
            RenderRules.DeploymentId(serviceId, deployId),
            url,
            $"https://dashboard.render.com/web/{Uri.EscapeDataString(serviceId)}/deploys/{Uri.EscapeDataString(deployId)}",
            status,
            error,
            production ? url : null,
            state);
    }
}
