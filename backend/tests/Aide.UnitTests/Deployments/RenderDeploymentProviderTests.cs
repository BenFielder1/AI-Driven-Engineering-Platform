using System.Net;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Platforms;
using static Aide.UnitTests.Deployments.PlatformFixtures;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-144, FR-145, FR-147, FR-149, FR-150, §4.20: the Render provider against a stubbed Render API (no network).</summary>
public class RenderDeploymentProviderTests
{
    private static readonly DeploymentAuth Auth = new("rnd_key", null, "tea-1", "tea-1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (RenderDeploymentProvider Provider, PlatformStub Stub, ManualTime Time) Create()
    {
        var stub = new PlatformStub();
        var time = new ManualTime(Now);
        return (new RenderDeploymentProvider(new PlatformHttp(new PlatformStubFactory(stub), time), new RenderLogRateLimiter(time)), stub, time);
    }

    private static DeploymentTargetRef Backend(string? resourceId = "srv-p", string? previewId = "srv-v") => new(
        Guid.CreateVersion7(),
        DeploymentProvider.Render,
        DeploymentComponent.Backend,
        resourceId,
        previewId,
        "todo-api",
        "backend",
        new DeploymentBuildConfig { DockerfilePath = "backend/Dockerfile", HealthCheckPath = "/api/health/ready", PreDeployCommand = "./efbundle" },
        GitHubRepo);

    [Fact]
    public async Task FR144_FR147_Create_makes_the_production_service_and_its_dash_preview_service()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "services?", "[]")
            .On("POST", "services", call => PlatformStub.Json(call.Body.Contains("\"todo-api-preview\"", StringComparison.Ordinal)
                ? """{"service":{"id":"srv-v","name":"todo-api-preview","serviceDetails":{"url":"https://todo-api-preview.onrender.com"}}}"""
                : """{"service":{"id":"srv-p","name":"todo-api","serviceDetails":{"url":"https://todo-api.onrender.com"}},"deployId":"dep-0"}""", HttpStatusCode.Created));

        var result = await provider.SetupTargetAsync(Auth, new DeploymentSetupRequest(Backend(null, null), true, "todo-api", "frankfurt", "starter", null), Ct);

        Assert.Equal(
            ("srv-p", "todo-api", "https://todo-api.onrender.com", "srv-v", "https://todo-api-preview.onrender.com"),
            (result.ResourceId, result.Name, result.ProductionUrl, result.PreviewResourceId, result.PreviewUrl));
        var creates = stub.Where("POST", "services").ToList();
        Assert.Equal(2, creates.Count);
        Assert.Contains("\"autoDeployTrigger\":\"commit\"", creates[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"ownerId\":\"tea-1\"", creates[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"preDeployCommand\":\"./efbundle\"", creates[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"healthCheckPath\":\"/api/health/ready\"", creates[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"autoDeployTrigger\":\"off\"", creates[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR144_A_repository_Render_cant_reach_is_PLATFORM_GIT_NOT_CONNECTED_with_the_Render_link()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "services?", "[]")
            .On("POST", "services", """{"message":"invalid repo: Render has no access to github.com/acme/todo"}""", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(
            () => provider.SetupTargetAsync(Auth, new DeploymentSetupRequest(Backend(null, null), true, "todo-api", null, null, null), Ct));

        Assert.Equal(DeploymentFailure.GitNotConnected, ex.Failure);
        Assert.Equal(DeploymentPlatforms.GitConnectUrl(DeploymentProvider.Render, "github"), ex.InstallUrl);
    }

    [Fact]
    public async Task FR144_Link_reuses_an_existing_dash_preview_service()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "services/srv-p", """{"id":"srv-p","name":"todo-api","repo":"https://github.com/acme/todo","branch":"main","serviceDetails":{"url":"https://todo-api.onrender.com"}}""")
            .On("GET", "services?", """[{"service":{"id":"srv-v","name":"todo-api-preview","repo":"https://github.com/acme/todo"}}]""");

        var result = await provider.SetupTargetAsync(Auth, new DeploymentSetupRequest(Backend(null, null), false, null, null, null, "srv-p"), Ct);

        Assert.Equal(("srv-p", "srv-v"), (result.ResourceId, result.PreviewResourceId));
        Assert.Empty(stub.Where("POST", "services"));
    }

    [Fact]
    public async Task FR145_Env_sync_PUTs_the_full_merged_set_per_service_and_never_deploys()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "services/srv-p/env-vars", """[{"envVar":{"key":"KEEP","value":"k"}},{"envVar":{"key":"OLD","value":"o"}}]""")
            .On("GET", "services/srv-v/env-vars", "[]")
            .On("PUT", "services/", "[]");
        var writes = new[]
        {
            new DeploymentEnvWrite("CORS_ALLOWED_ORIGINS", "https://todo.app", DeploymentEnvironment.Production, false, null),
            new DeploymentEnvWrite("CORS_ALLOWED_ORIGINS", "https://preview.todo.app", DeploymentEnvironment.Preview, false, null),
        };

        var written = await provider.SyncEnvAsync(Auth, Backend(), new DeploymentEnvSyncRequest(writes, [RenderRules.EnvId("OLD", DeploymentEnvironment.Production)]), Ct);

        var production = Assert.Single(stub.Where("PUT", "services/srv-p/env-vars"));
        Assert.Equal("""[{"key":"CORS_ALLOWED_ORIGINS","value":"https://todo.app"},{"key":"KEEP","value":"k"}]""", production.Body);
        var preview = Assert.Single(stub.Where("PUT", "services/srv-v/env-vars"));
        Assert.Equal("""[{"key":"CORS_ALLOWED_ORIGINS","value":"https://preview.todo.app"}]""", preview.Body);
        Assert.Empty(stub.Where("POST", "services/"));
        Assert.Equal(["production:CORS_ALLOWED_ORIGINS", "preview:CORS_ALLOWED_ORIGINS"], written.Select(w => w.Id));
    }

    [Fact]
    public async Task FR145_Env_sync_reads_every_page_of_variables_before_the_full_PUT()
    {
        var (provider, stub, _) = Create();
        var first = "[" + string.Join(',', Enumerable.Range(0, 100).Select(i => $$"""{"envVar":{"key":"K{{i:D3}}","value":"v"},"cursor":"c{{i}}"}""")) + "]";
        stub.On("GET", "services/srv-p/env-vars?limit=100&cursor=c99", """[{"envVar":{"key":"LAST","value":"z"},"cursor":"c100"}]""")
            .On("GET", "services/srv-p/env-vars?limit=100", first)
            .On("PUT", "services/", "[]");

        await provider.SyncEnvAsync(
            Auth, Backend(), new DeploymentEnvSyncRequest([new DeploymentEnvWrite("NEW", "n", DeploymentEnvironment.Production, false, null)], []), Ct);

        var put = Assert.Single(stub.Where("PUT", "services/srv-p/env-vars")).Body;
        Assert.Contains("\"key\":\"K099\"", put, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"LAST\"", put, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"NEW\"", put, StringComparison.Ordinal);
        Assert.Equal(2, stub.Where("GET", "services/srv-p/env-vars").Count());
    }

    [Fact]
    public async Task FR146_A_static_site_gets_the_api_rewrite_to_API_ORIGIN_with_the_env_sync()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "services/srv-w/env-vars", "[]")
            .On("GET", "services/srv-w/routes", """[{"route":{"id":"rdr-1","type":"rewrite","source":"/*","destination":"/index.html","priority":0},"cursor":"c1"},{"route":{"id":"rdr-0","type":"rewrite","source":"/api/*","destination":"https://old.onrender.com/api/*","priority":1},"cursor":"c2"}]""")
            .On("GET", "services/srv-w", """{"id":"srv-w","name":"todo-web","type":"static_site"}""")
            .On("PUT", "services/", "[]");
        var target = Target(DeploymentProvider.Render, "srv-w");

        await provider.SyncEnvAsync(
            Auth, target, new DeploymentEnvSyncRequest([new DeploymentEnvWrite("API_ORIGIN", "https://todo-api.onrender.com", DeploymentEnvironment.Production, false, null)], []), Ct);

        // The SPA fallback survives, behind the proxy; the stale /api/* rule is replaced.
        var routes = Assert.Single(stub.Where("PUT", "services/srv-w/routes"));
        Assert.Equal(
            """[{"type":"rewrite","source":"/api/*","destination":"https://todo-api.onrender.com/api/*","priority":0},{"type":"rewrite","source":"/*","destination":"/index.html","priority":1}]""",
            routes.Body);
    }

    [Fact]
    public async Task FR147_A_preview_points_the_dash_preview_service_at_the_branch_then_deploys_the_PR_head()
    {
        var (provider, stub, _) = Create();
        stub.On("PATCH", "services/srv-v", """{"id":"srv-v"}""")
            .On("POST", "services/srv-v/deploys", """{"id":"dep-9","status":"created"}""", HttpStatusCode.Created);

        var deployment = await provider.DeployAsync(
            Auth, new DeployRequest(Guid.CreateVersion7(), Backend(), DeploymentEnvironment.Preview, "aide/todo-v1", new string('a', 40)), Ct);

        Assert.Equal(("srv-v|dep-9", DeploymentStatus.Queued), (deployment.Id, deployment.Status));
        Assert.Equal("""{"branch":"aide/todo-v1"}""", Assert.Single(stub.Where("PATCH", "services/srv-v")).Body);
        Assert.Contains($"\"commitId\":\"{new string('a', 40)}\"", Assert.Single(stub.Where("POST", "services/srv-v/deploys")).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR149_Production_adopts_Renders_auto_deploy_of_the_merge_commit()
    {
        var (provider, stub, _) = Create();
        var sha = new string('b', 40);
        stub.On(
            "GET",
            "services/srv-p/deploys?",
            """[{"deploy":{"id":"dep-2","status":"canceled","commit":{"id":"SHA"}}},{"deploy":{"id":"dep-1","status":"build_in_progress","trigger":"new_commit","commit":{"id":"SHA"}}}]""".Replace("SHA", sha, StringComparison.Ordinal));

        var adopted = await provider.FindDeploymentForShaAsync(Auth, Backend(), DeploymentEnvironment.Production, sha, null, null, Ct);

        Assert.Equal(("srv-p|dep-1", DeploymentStatus.Building), (adopted!.Id, adopted.Status));
    }

    [Fact]
    public async Task FR147_A_failed_pre_deploy_is_an_error_named_as_the_pre_deploy()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "services/srv-v/deploys/dep-9", """{"id":"dep-9","status":"pre_deploy_failed"}""");

        var deployment = await provider.GetDeploymentAsync(Auth, Backend(), "srv-v|dep-9", Ct);

        Assert.Equal((DeploymentStatus.Error, RenderRules.PreDeployFailedMessage), (deployment.Status, deployment.ErrorMessage));
    }

    [Fact]
    public async Task FR150_Rollback_posts_the_deploy_to_the_service_rollback()
    {
        var (provider, stub, _) = Create();
        stub.On("POST", "services/srv-p/rollback", """{"id":"dep-r","trigger":"rollback"}""", HttpStatusCode.Created);

        await provider.RollbackAsync(Auth, Backend(), "srv-p|dep-1", Ct);

        Assert.Equal("""{"deployId":"dep-1"}""", Assert.Single(stub.Where("POST", "services/srv-p/rollback")).Body);
    }

    [Fact]
    public async Task S4_20_Build_logs_read_the_deploy_span_and_stop_at_30_a_minute()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "services/srv-p/deploys/dep-1", """{"id":"dep-1","status":"build_failed","createdAt":"2026-10-30T11:50:00Z","finishedAt":"2026-10-30T11:55:00Z"}""")
            .On("GET", "logs?", """{"logs":[{"message":"error CS1002"},{"message":"dotnet publish"}],"hasMore":false}""");

        var log = await provider.GetBuildLogTailAsync(Auth, Backend(), "srv-p|dep-1", Ct);

        Assert.Equal("dotnet publish\nerror CS1002", log!.ReplaceLineEndings("\n"));
        var query = Assert.Single(stub.Where("GET", "logs?")).Path;
        Assert.Contains("ownerId=tea-1&resource=srv-p&type=build&startTime=2026-10-30T11:50:00Z&endTime=2026-10-30T11:55:00Z&direction=backward&limit=100", query, StringComparison.Ordinal);
        for (var i = 1; i < RenderRules.LogRequestsPerMinute; i++)
        {
            await provider.GetBuildLogTailAsync(Auth, Backend(), "srv-p|dep-1", Ct);
        }

        var limited = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.GetBuildLogTailAsync(Auth, Backend(), "srv-p|dep-1", Ct));
        Assert.Equal(DeploymentFailure.RateLimited, limited.Failure);
        Assert.Equal(RenderRules.LogRequestsPerMinute, stub.Where("GET", "logs?").Count());
    }

    [Fact]
    public async Task S4_20_A_Render_429_is_rate_limited_with_its_reset()
    {
        var (provider, stub, _) = Create();
        stub.On("POST", "services/srv-p/deploys", _ =>
        {
            var response = PlatformStub.Json("""{"message":"rate limit exceeded"}""", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.DeployAsync(
            Auth, new DeployRequest(Guid.CreateVersion7(), Backend(), DeploymentEnvironment.Production, "main", new string('c', 40)), Ct));

        Assert.Equal((DeploymentFailure.RateLimited, Now.AddSeconds(42)), (ex.Failure, ex.RetryAt));
    }
}
