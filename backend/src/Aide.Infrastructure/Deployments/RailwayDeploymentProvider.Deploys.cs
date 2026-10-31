using System.Text.Json;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Platforms;
using static Aide.Infrastructure.Deployments.PlatformDeploymentCalls;

namespace Aide.Infrastructure.Deployments;

/// <summary>
/// Railway env sync, deploys, adoption, logs, cancel and rollback (§4.20): <c>variableCollectionUpsert {…, replace: false,
/// skipDeploys: true}</c> per environment; <c>serviceInstanceDeployV2(serviceId, environmentId, commitSha)</c> (SHA-pinned;
/// preview in <c>aide-preview</c>); production adopts the deployment whose <c>meta.commitHash</c> is the merge SHA;
/// <c>deploymentRollback(id)</c> when <c>canRollback</c>; <c>buildLogs(deploymentId, limit: 100)</c>.
/// </summary>
public sealed partial class RailwayDeploymentProvider
{
    private const string DeploymentFields = "id status staticUrl url canRollback createdAt meta";

    public async Task<IReadOnlyList<DeploymentEnvWritten>> SyncEnvAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvSyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        var resource = Resource(target);
        var production = resource.ProductionEnvironmentId ?? await ProductionEnvironmentAsync(auth, resource.ProjectId, cancellationToken);
        var written = new List<DeploymentEnvWritten>();
        var deletes = request.Deletes.Select(RailwayRules.ParseEnvId).OfType<(string Key, DeploymentEnvironment Environment)>().ToList();
        foreach (var environment in new[] { DeploymentEnvironment.Production, DeploymentEnvironment.Preview })
        {
            var environmentId = environment == DeploymentEnvironment.Production ? production : target.PreviewResourceId;
            var writes = request.Writes.Where(w => w.Target == environment).ToList();
            if (environmentId is null)
            {
                continue;
            }

            if (writes.Count > 0)
            {
                await UpsertVariablesAsync(
                    auth, resource.ProjectId, environmentId, resource.ServiceId, writes.ToDictionary(w => w.Key, w => w.Value, StringComparer.Ordinal), cancellationToken);
                written.AddRange(writes.Select(w => new DeploymentEnvWritten(w.Key, w.Target, RailwayRules.EnvId(w.Key, w.Target))));
            }

            foreach (var (key, _) in deletes.Where(d => d.Environment == environment))
            {
                try
                {
                    await QueryAsync(
                        auth,
                        "mutation($input: VariableDeleteInput!) { variableDelete(input: $input) }",
                        new { input = new { projectId = resource.ProjectId, environmentId, serviceId = resource.ServiceId, name = key } },
                        cancellationToken);
                }
                catch (DeploymentProviderException ex) when (ex.Failure == DeploymentFailure.NotFound)
                {
                    // Already gone.
                }
            }
        }

        return written;
    }

    /// <summary>§4.20: <c>variableCollectionUpsert</c> merging into the service's variables without deploying.</summary>
    private async Task UpsertVariablesAsync(
        DeploymentAuth auth, string projectId, string environmentId, string serviceId, IReadOnlyDictionary<string, string> variables, CancellationToken cancellationToken) =>
        await QueryAsync(
            auth,
            "mutation($input: VariableCollectionUpsertInput!) { variableCollectionUpsert(input: $input) }",
            new { input = new { projectId, environmentId, serviceId, variables, replace = false, skipDeploys = true } },
            cancellationToken);

    public async Task<ProviderDeployment> DeployAsync(DeploymentAuth auth, DeployRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resource = Resource(request.Target);
        var environmentId = await EnvironmentOfAsync(auth, request.Target, resource, request.Environment, cancellationToken);
        var data = await QueryAsync(
            auth,
            "mutation($serviceId: String!, $environmentId: String!, $commitSha: String) { serviceInstanceDeployV2(serviceId: $serviceId, environmentId: $environmentId, commitSha: $commitSha) }",
            new { serviceId = resource.ServiceId, environmentId, commitSha = request.GitSha },
            cancellationToken);
        var id = Text(data, "serviceInstanceDeployV2")
            ?? throw new DeploymentProviderException(DeploymentFailure.Upstream, null, "Railway started the deployment but returned no id.", platform: DeploymentProvider.Railway);
        return new ProviderDeployment(id, null, Inspector(resource, environmentId, id), "QUEUED", null, State: DeploymentStatus.Queued);
    }

    public async Task<ProviderDeployment> GetDeploymentAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        var data = await QueryAsync(auth, $"query($id: String!) {{ deployment(id: $id) {{ {DeploymentFields} environmentId }} }}", new { id = deploymentId }, cancellationToken);
        var deployment = Property(data, "deployment");
        if (Text(deployment, "id") is null)
        {
            throw new DeploymentProviderException(DeploymentFailure.NotFound, 404, "Railway: the deployment no longer exists.", platform: DeploymentProvider.Railway);
        }

        var resource = Resource(target);
        var production = Text(deployment, "environmentId") is { } env ? env != target.PreviewResourceId : true;
        return ToDeployment(deployment, resource, production);
    }

    public async Task<string?> GetBuildLogTailAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        var data = await QueryAsync(auth, "query($id: String!) { buildLogs(deploymentId: $id, limit: 100) { message timestamp } }", new { id = deploymentId }, cancellationToken);
        var lines = Items(data, "buildLogs").Select(l => Text(l, "message")).OfType<string>().ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    public async Task<ProviderDeployment?> FindDeploymentForShaAsync(
        DeploymentAuth auth, DeploymentTargetRef target, DeploymentEnvironment environment, string sha, Guid? aideDeploymentId, DateTimeOffset? createdAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (aideDeploymentId is null && environment != DeploymentEnvironment.Production)
        {
            return null;
        }

        var resource = Resource(target);
        var environmentId = environment == DeploymentEnvironment.Production
            ? resource.ProductionEnvironmentId ?? await ProductionEnvironmentAsync(auth, resource.ProjectId, cancellationToken)
            : target.PreviewResourceId;
        if (environmentId is null)
        {
            return null;
        }

        var data = await QueryAsync(
            auth,
            $"query($input: DeploymentListInput!) {{ deployments(input: $input, first: 20) {{ edges {{ node {{ {DeploymentFields} }} }} }} }}",
            new { input = new { projectId = resource.ProjectId, serviceId = resource.ServiceId, environmentId } },
            cancellationToken);
        foreach (var node in Nodes(Property(data, "deployments")))
        {
            if (!RailwayRules.SameCommit(Text(Property(node, "meta"), "commitHash"), sha))
            {
                continue;
            }

            // Railway can't tag a deployment with the AIDE row: a retry adopts only one created after the row; the release
            // adopts Railway's own deployment of the merge commit (FR-149), never a skipped or removed one.
            var created = Json.Time(node, "createdAt");
            var state = RailwayRules.StateOf(Text(node, "status"));
            var matches = aideDeploymentId is not null
                ? createdAfter is null || created is null || created >= createdAfter
                : state != DeploymentStatus.Canceled;
            if (matches)
            {
                return ToDeployment(node, resource, environment == DeploymentEnvironment.Production);
            }
        }

        return null;
    }

    public async Task CancelAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        try
        {
            await QueryAsync(auth, "mutation($id: String!) { deploymentCancel(id: $id) }", new { id = deploymentId }, cancellationToken);
        }
        catch (DeploymentProviderException ex) when (ex.Failure is DeploymentFailure.NotFound or DeploymentFailure.Rejected)
        {
            // Gone or finished.
        }
    }

    /// <summary>FR-150: <c>deploymentRollback(id)</c>, only when Railway reports <c>canRollback</c> (else <c>INVALID_DEPLOYMENT</c>).</summary>
    public async Task RollbackAsync(DeploymentAuth auth, DeploymentTargetRef target, string deploymentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(deploymentId);
        var data = await QueryAsync(auth, "query($id: String!) { deployment(id: $id) { id canRollback } }", new { id = deploymentId }, cancellationToken);
        if (Property(Property(data, "deployment"), "canRollback").ValueKind != JsonValueKind.True)
        {
            throw new DeploymentProviderException(
                DeploymentFailure.Rejected, 400, "Railway can't roll back to this deployment (its image is no longer kept).", platform: DeploymentProvider.Railway);
        }

        await QueryAsync(auth, "mutation($id: String!) { deploymentRollback(id: $id) }", new { id = deploymentId }, cancellationToken);
    }

    private async Task<string> EnvironmentOfAsync(
        DeploymentAuth auth, DeploymentTargetRef target, RailwayResource resource, DeploymentEnvironment environment, CancellationToken cancellationToken) =>
        environment == DeploymentEnvironment.Production
            ? resource.ProductionEnvironmentId ?? await ProductionEnvironmentAsync(auth, resource.ProjectId, cancellationToken)
            : target.PreviewResourceId
                ?? throw new DeploymentProviderException(
                    DeploymentFailure.Rejected, 400, "The Railway aide-preview environment is missing; set the target up again in Settings → Deployment.", platform: DeploymentProvider.Railway);

    private async Task<string> ProductionEnvironmentAsync(DeploymentAuth auth, string projectId, CancellationToken cancellationToken)
    {
        var environments = await EnvironmentsAsync(auth, projectId, cancellationToken);
        return environments.FirstOrDefault(e => e.Name == RailwayRules.ProductionEnvironmentName)?.Id
            ?? throw new DeploymentProviderException(DeploymentFailure.NotFound, 404, "The Railway project has no production environment.", platform: DeploymentProvider.Railway);
    }

    private static RailwayResource Resource(DeploymentTargetRef target) =>
        RailwayRules.ParseResourceId(target.ResourceId)
        ?? throw new DeploymentProviderException(
            DeploymentFailure.Rejected, 400, "The Railway target has no service; set it up in Settings → Deployment.", platform: DeploymentProvider.Railway);

    private static string Inspector(RailwayResource resource, string? environmentId, string deploymentId) =>
        $"https://railway.com/project/{resource.ProjectId}/service/{resource.ServiceId}?id={deploymentId}"
        + (environmentId is null ? string.Empty : $"&environmentId={environmentId}");

    private static ProviderDeployment ToDeployment(JsonElement deployment, RailwayResource resource, bool production)
    {
        var status = Text(deployment, "status");
        var state = RailwayRules.StateOf(status);
        var url = RailwayRules.Url(Text(deployment, "staticUrl") ?? Text(deployment, "url"));
        return new ProviderDeployment(
            Text(deployment, "id")!,
            url,
            Inspector(resource, Text(deployment, "environmentId"), Text(deployment, "id")!),
            status,
            state == DeploymentStatus.Error ? $"Railway: the deployment failed ({status})." : null,
            production && state == DeploymentStatus.Ready ? url : null,
            state);
    }
}
