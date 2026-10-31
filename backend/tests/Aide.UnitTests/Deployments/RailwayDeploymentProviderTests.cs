using System.Net;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Platforms;
using static Aide.UnitTests.Deployments.PlatformFixtures;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-144, FR-145, FR-147, FR-149, FR-150, §4.20: the Railway provider against a stubbed GraphQL API (no network).</summary>
public class RailwayDeploymentProviderTests
{
    private const string Resource = "p1:s1:e-prod";

    private static readonly DeploymentAuth Auth = new("rw_token", null, "ws-1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A provider whose GraphQL answers come from the first <paramref name="routes"/> entry the query contains.</summary>
    private static (RailwayDeploymentProvider Provider, PlatformStub Stub) Create(params (string Operation, string Json)[] routes)
    {
        var stub = new PlatformStub();
        stub.On("POST", "/graphql/v2", call =>
        {
            foreach (var (operation, json) in routes)
            {
                if (call.Body.Contains(operation, StringComparison.Ordinal))
                {
                    return PlatformStub.Json(json);
                }
            }

            return PlatformStub.Json("""{"errors":[{"message":"Not Found"}]}""");
        });
        return (new RailwayDeploymentProvider(new PlatformHttp(new PlatformStubFactory(stub), new ManualTime(Now))), stub);
    }

    private static DeploymentTargetRef Backend(string? resourceId = Resource, string? previewId = "e-prev") => new(
        Guid.CreateVersion7(),
        DeploymentProvider.Railway,
        DeploymentComponent.Backend,
        resourceId,
        previewId,
        "todo-api",
        "backend",
        new DeploymentBuildConfig { DockerfilePath = "backend/Dockerfile", HealthCheckPath = "/api/health/ready", PreDeployCommand = "./efbundle" },
        GitHubRepo);

    private static IEnumerable<string> Bodies(PlatformStub stub, string operation) =>
        stub.Where("POST", "/graphql/v2").Select(c => c.Body).Where(b => b.Contains(operation, StringComparison.Ordinal));

    [Fact]
    public async Task FR144_FR147_Create_makes_the_project_service_domain_and_the_aide_preview_environment()
    {
        var (provider, stub) = Create(
            ("projects(workspaceId", """{"data":{"projects":{"edges":[]}}}"""),
            ("projectCreate", """{"data":{"projectCreate":{"id":"p1","name":"todo"}}}"""),
            ("services { edges", """{"data":{"project":{"id":"p1","services":{"edges":[]}}}}"""),
            ("environments { edges", """{"data":{"project":{"id":"p1","environments":{"edges":[{"node":{"id":"e-prod","name":"production"}}]}}}}"""),
            ("serviceCreate", """{"data":{"serviceCreate":{"id":"s1","name":"todo-api"}}}"""),
            ("serviceInstanceUpdate", """{"data":{"serviceInstanceUpdate":true}}"""),
            ("variableCollectionUpsert", """{"data":{"variableCollectionUpsert":true}}"""),
            ("environmentCreate", """{"data":{"environmentCreate":{"id":"e-prev","name":"aide-preview"}}}"""),
            ("domains(", """{"data":{"domains":{"serviceDomains":[]}}}"""),
            ("serviceDomainCreate", """{"data":{"serviceDomainCreate":{"id":"d1","domain":"todo-api.up.railway.app"}}}"""));

        var result = await provider.SetupTargetAsync(Auth, new DeploymentSetupRequest(Backend(null, null), true, "todo-api", null, null, null), Ct);

        Assert.Equal(
            ("p1:s1:e-prod", "todo-api", "https://todo-api.up.railway.app", "e-prev"),
            (result.ResourceId, result.Name, result.ProductionUrl, result.PreviewResourceId));
        var project = Assert.Single(Bodies(stub, "projectCreate"));
        Assert.Contains("\"workspaceId\":\"ws-1\"", project, StringComparison.Ordinal);
        Assert.Contains("\"prDeploys\":false", project, StringComparison.Ordinal);
        var service = Assert.Single(Bodies(stub, "serviceCreate"));
        Assert.Contains("\"source\":{\"repo\":\"acme/todo\"},\"branch\":\"main\"", service, StringComparison.Ordinal);
        var instance = Assert.Single(Bodies(stub, "serviceInstanceUpdate"));
        Assert.Contains("\"rootDirectory\":\"/backend\"", instance, StringComparison.Ordinal);
        Assert.Contains("\"dockerfilePath\":\"Dockerfile\"", instance, StringComparison.Ordinal);
        Assert.Contains("\"healthcheckPath\":\"/api/health/ready\"", instance, StringComparison.Ordinal);
        Assert.Contains("\"watchPatterns\":[\"/backend/**\"]", instance, StringComparison.Ordinal);
        Assert.Contains("\"preDeployCommand\":[\"./efbundle\"]", instance, StringComparison.Ordinal);
        var preview = Assert.Single(Bodies(stub, "environmentCreate"));
        Assert.Contains("\"name\":\"aide-preview\",\"sourceEnvironmentId\":\"e-prod\",\"skipInitialDeploys\":true", preview, StringComparison.Ordinal);
        Assert.Contains("\"PORT\":\"8080\"", Assert.Single(Bodies(stub, "variableCollectionUpsert")), StringComparison.Ordinal);
        Assert.Equal(2, Bodies(stub, "serviceDomainCreate").Count());
    }

    [Fact]
    public async Task FR147_A_second_component_in_a_project_with_aide_preview_configures_its_preview_instance_and_PORT_too()
    {
        var (provider, stub) = Create(
            ("projects(workspaceId", """{"data":{"projects":{"edges":[{"node":{"id":"p1","name":"todo"}}]}}}"""),
            ("services { edges", """{"data":{"project":{"id":"p1","services":{"edges":[]}}}}"""),
            ("environments { edges", """{"data":{"project":{"id":"p1","environments":{"edges":[{"node":{"id":"e-prod","name":"production"}},{"node":{"id":"e-prev","name":"aide-preview"}}]}}}}"""),
            ("serviceCreate", """{"data":{"serviceCreate":{"id":"s2","name":"todo-api"}}}"""),
            ("serviceInstanceUpdate", """{"data":{"serviceInstanceUpdate":true}}"""),
            ("variableCollectionUpsert", """{"data":{"variableCollectionUpsert":true}}"""),
            ("domains(", """{"data":{"domains":{"serviceDomains":[{"domain":"todo-api.up.railway.app"}]}}}"""));

        var result = await provider.SetupTargetAsync(Auth, new DeploymentSetupRequest(Backend(null, null), true, "todo-api", null, null, null), Ct);

        Assert.Equal(("p1:s2:e-prod", "e-prev"), (result.ResourceId, result.PreviewResourceId));
        Assert.Empty(Bodies(stub, "environmentCreate"));
        var instances = Bodies(stub, "serviceInstanceUpdate").ToList();
        Assert.Equal(2, instances.Count);
        Assert.Contains("\"environmentId\":\"e-prod\"", instances[0], StringComparison.Ordinal);
        Assert.Contains("\"environmentId\":\"e-prev\"", instances[1], StringComparison.Ordinal);
        Assert.All(instances, b => Assert.Contains("\"preDeployCommand\":[\"./efbundle\"]", b, StringComparison.Ordinal));
        var ports = Bodies(stub, "variableCollectionUpsert").ToList();
        Assert.Equal(2, ports.Count);
        Assert.Contains("\"environmentId\":\"e-prev\",\"serviceId\":\"s2\",\"variables\":{\"PORT\":\"8080\"}", ports[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR146_Linking_a_backend_sets_PORT_only_where_the_service_has_none()
    {
        var (provider, stub) = Create(
            ("services { edges", """{"data":{"project":{"id":"p1","services":{"edges":[{"node":{"id":"s1","name":"todo-api","serviceInstances":{"edges":[{"node":{"source":{"repo":"acme/todo"}}}]}}}]}}}}"""),
            ("environments { edges", """{"data":{"project":{"id":"p1","environments":{"edges":[{"node":{"id":"e-prod","name":"production"}},{"node":{"id":"e-prev","name":"aide-preview"}}]}}}}"""),
            ("\"environmentId\":\"e-prod\",\"serviceId\":\"s1\"}", """{"data":{"variables":{"PORT":"3000"}}}"""),
            ("variables(projectId", """{"data":{"variables":{}}}"""),
            ("variableCollectionUpsert", """{"data":{"variableCollectionUpsert":true}}"""),
            ("domains(", """{"data":{"domains":{"serviceDomains":[{"domain":"todo-api.up.railway.app"}]}}}"""));

        var result = await provider.SetupTargetAsync(Auth, new DeploymentSetupRequest(Backend(null, null), false, null, null, null, "p1"), Ct);

        Assert.Equal("p1:s1:e-prod", result.ResourceId);
        Assert.Empty(Bodies(stub, "serviceInstanceUpdate"));
        var upsert = Assert.Single(Bodies(stub, "variableCollectionUpsert"));
        Assert.Contains("\"environmentId\":\"e-prev\"", upsert, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR144_Without_the_Railway_GitHub_app_create_is_PLATFORM_GIT_NOT_CONNECTED_with_the_install_link()
    {
        var (provider, _) = Create(
            ("projects(workspaceId", """{"data":{"projects":{"edges":[{"node":{"id":"p1","name":"todo"}}]}}}"""),
            ("services { edges", """{"data":{"project":{"id":"p1","services":{"edges":[]}}}}"""),
            ("environments { edges", """{"data":{"project":{"id":"p1","environments":{"edges":[{"node":{"id":"e-prod","name":"production"}}]}}}}"""),
            ("serviceCreate", """{"errors":[{"message":"GitHub repo acme/todo not found: is the Railway app installed with access to it?"}]}"""));

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(
            () => provider.SetupTargetAsync(Auth, new DeploymentSetupRequest(Backend(null, null), true, "todo-api", null, null, null), Ct));

        Assert.Equal(DeploymentFailure.GitNotConnected, ex.Failure);
        Assert.Equal(DeploymentPlatforms.GitConnectUrl(DeploymentProvider.Railway, "github"), ex.InstallUrl);
    }

    [Fact]
    public async Task FR144_Railway_deploys_only_from_GitHub()
    {
        var (provider, _) = Create();
        var gitlab = Backend(null, null) with { Repo = new DeploymentRepoRef("gitlab", "acme", "todo", null, "9", "acme/todo", "main") };

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(
            () => provider.SetupTargetAsync(Auth, new DeploymentSetupRequest(gitlab, true, "todo-api", null, null, null), Ct));

        Assert.Equal(DeploymentFailure.NotSupported, ex.Failure);
    }

    [Fact]
    public async Task FR147_A_preview_deploys_the_PR_head_in_aide_preview()
    {
        var (provider, stub) = Create(("serviceInstanceDeployV2", """{"data":{"serviceInstanceDeployV2":"dep-1"}}"""));
        var sha = new string('a', 40);

        var deployment = await provider.DeployAsync(Auth, new DeployRequest(Guid.CreateVersion7(), Backend(), DeploymentEnvironment.Preview, "aide/todo-v1", sha), Ct);

        Assert.Equal(("dep-1", DeploymentStatus.Queued), (deployment.Id, deployment.Status));
        Assert.Contains($"\"serviceId\":\"s1\",\"environmentId\":\"e-prev\",\"commitSha\":\"{sha}\"", Assert.Single(Bodies(stub, "serviceInstanceDeployV2")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR149_Production_adopts_the_deployment_of_the_merge_commit()
    {
        var sha = new string('b', 40);
        var (provider, _) = Create((
            "deployments(input",
            """{"data":{"deployments":{"edges":[{"node":{"id":"dep-x","status":"SUCCESS","meta":{"commitHash":"cccccccc"}}},{"node":{"id":"dep-m","status":"BUILDING","meta":{"commitHash":"SHA"}}}]}}}"""
                .Replace("SHA", sha, StringComparison.Ordinal)));

        var adopted = await provider.FindDeploymentForShaAsync(Auth, Backend(), DeploymentEnvironment.Production, sha, null, null, Ct);

        Assert.Equal(("dep-m", DeploymentStatus.Building), (adopted!.Id, adopted.Status));
    }

    [Fact]
    public async Task FR150_Rollback_calls_deploymentRollback_when_canRollback()
    {
        var (provider, stub) = Create(
            ("canRollback", """{"data":{"deployment":{"id":"dep-1","canRollback":true}}}"""),
            ("deploymentRollback", """{"data":{"deploymentRollback":true}}"""));

        await provider.RollbackAsync(Auth, Backend(), "dep-1", Ct);

        Assert.Single(Bodies(stub, "deploymentRollback"));
    }

    [Fact]
    public async Task FR150_Rollback_without_canRollback_is_refused_before_calling()
    {
        var (provider, stub) = Create(("canRollback", """{"data":{"deployment":{"id":"dep-1","canRollback":false}}}"""));

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.RollbackAsync(Auth, Backend(), "dep-1", Ct));

        Assert.Equal(DeploymentFailure.Rejected, ex.Failure);
        Assert.Empty(Bodies(stub, "deploymentRollback"));
    }

    [Fact]
    public async Task FR145_Env_sync_upserts_per_environment_without_deploying()
    {
        var (provider, stub) = Create(("variableCollectionUpsert", """{"data":{"variableCollectionUpsert":true}}"""), ("variableDelete", """{"data":{"variableDelete":true}}"""));
        var writes = new[]
        {
            new DeploymentEnvWrite("CORS_ALLOWED_ORIGINS", "https://todo.app", DeploymentEnvironment.Production, false, null),
            new DeploymentEnvWrite("CORS_ALLOWED_ORIGINS", "https://preview.todo.app", DeploymentEnvironment.Preview, false, null),
        };

        await provider.SyncEnvAsync(Auth, Backend(), new DeploymentEnvSyncRequest(writes, [RailwayRules.EnvId("OLD", DeploymentEnvironment.Preview)]), Ct);

        var upserts = Bodies(stub, "variableCollectionUpsert").ToList();
        Assert.Equal(2, upserts.Count);
        Assert.Contains(
            "\"projectId\":\"p1\",\"environmentId\":\"e-prod\",\"serviceId\":\"s1\",\"variables\":{\"CORS_ALLOWED_ORIGINS\":\"https://todo.app\"},\"replace\":false,\"skipDeploys\":true",
            upserts[0],
            StringComparison.Ordinal);
        Assert.Contains("\"environmentId\":\"e-prev\"", upserts[1], StringComparison.Ordinal);
        Assert.Contains("\"environmentId\":\"e-prev\",\"serviceId\":\"s1\",\"name\":\"OLD\"", Assert.Single(Bodies(stub, "variableDelete")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task S4_20_A_Railway_rate_limit_is_honoured_as_rate_limited()
    {
        var (provider, _) = Create(("deployment(id", """{"errors":[{"message":"You have exceeded the rate limit"}]}"""));

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.GetDeploymentAsync(Auth, Backend(), "dep-1", Ct));

        Assert.Equal(DeploymentFailure.RateLimited, ex.Failure);
    }

    [Fact]
    public async Task S4_20_An_HTTP_429_from_Railway_carries_its_reset()
    {
        var stub = new PlatformStub();
        stub.On("POST", "/graphql/v2", _ =>
        {
            var response = PlatformStub.Json("""{"message":"Too Many Requests"}""", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        });
        var provider = new RailwayDeploymentProvider(new PlatformHttp(new PlatformStubFactory(stub), new ManualTime(Now)));

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.GetBuildLogTailAsync(Auth, Backend(), "dep-1", Ct));

        Assert.Equal((DeploymentFailure.RateLimited, Now.AddSeconds(90)), (ex.Failure, ex.RetryAt));
    }

    [Fact]
    public async Task FR147_Build_logs_are_read_with_a_limit_of_100()
    {
        var (provider, stub) = Create(("buildLogs", """{"data":{"buildLogs":[{"message":"Step 1/9"},{"message":"error: build failed"}]}}"""));

        var log = await provider.GetBuildLogTailAsync(Auth, Backend(), "dep-1", Ct);

        Assert.Equal("Step 1/9\nerror: build failed", log);
        Assert.Contains("limit: 100", Assert.Single(Bodies(stub, "buildLogs")), StringComparison.Ordinal);
    }
}
