using System.Text.Json;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Stack;
using Aide.Infrastructure.Deployments;
using Aide.UnitTests.Stack;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-144, FR-145, FR-146, FR-147, FR-150, §4.20: Render and Railway rules (states, payloads, ids, env, limits, polling).</summary>
public class RenderRailwayRulesTests
{
    private const string MigrateCommand = "./efbundle --connection \"$DATABASE_URL\"";

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static StackComposition AspNetOnRender() => StackTestCatalog.Valid(StackTestCatalog.Sel(
        backend: "be-aspnet", data: "data-efcore-postgres", vcs: "vcs-github", backendHost: "deploy-render", databaseHost: "render-postgres"));

    [Theory]
    [InlineData("created", DeploymentStatus.Queued)]
    [InlineData("queued", DeploymentStatus.Queued)]
    [InlineData("build_in_progress", DeploymentStatus.Building)]
    [InlineData("update_in_progress", DeploymentStatus.Building)]
    [InlineData("pre_deploy_in_progress", DeploymentStatus.Building)]
    [InlineData("live", DeploymentStatus.Ready)]
    [InlineData("build_failed", DeploymentStatus.Error)]
    [InlineData("update_failed", DeploymentStatus.Error)]
    [InlineData("pre_deploy_failed", DeploymentStatus.Error)]
    [InlineData("canceled", DeploymentStatus.Canceled)]
    [InlineData("deactivated", DeploymentStatus.Canceled)]
    public void FR144_S4_20_Render_deploy_states_map_per_the_table(string status, DeploymentStatus expected) =>
        Assert.Equal(expected, RenderRules.StateOf(status));

    [Fact]
    public void FR147_A_Render_pre_deploy_failure_is_distinguishable_from_a_build_failure()
    {
        Assert.True(RenderRules.IsPreDeployFailure("pre_deploy_failed"));
        Assert.False(RenderRules.IsPreDeployFailure("build_failed"));
    }

    [Theory]
    [InlineData("QUEUED", DeploymentStatus.Queued)]
    [InlineData("WAITING", DeploymentStatus.Queued)]
    [InlineData("INITIALIZING", DeploymentStatus.Queued)]
    [InlineData("BUILDING", DeploymentStatus.Building)]
    [InlineData("DEPLOYING", DeploymentStatus.Building)]
    [InlineData("SUCCESS", DeploymentStatus.Ready)]
    [InlineData("FAILED", DeploymentStatus.Error)]
    [InlineData("CRASHED", DeploymentStatus.Error)]
    [InlineData("SKIPPED", DeploymentStatus.Canceled)]
    [InlineData("REMOVED", DeploymentStatus.Canceled)]
    public void FR144_S4_20_Railway_deployment_states_map_per_the_table(string status, DeploymentStatus expected) =>
        Assert.Equal(expected, RailwayRules.StateOf(status));

    [Fact]
    public void FR144_ASP_NET_on_Render_creates_a_Docker_web_service_with_the_ready_check_auto_deploy_from_main_and_the_EF_bundle_pre_deploy()
    {
        var plan = DeploymentBuildRules.Derive(AspNetOnRender(), DeploymentComponent.Backend, DeploymentProvider.Render, MigrateCommand);
        Assert.Equal("backend/Dockerfile", plan.Config.DockerfilePath);

        var spec = RenderRules.ServiceSpec(DeploymentComponent.Backend, plan.Config, plan.RootDirectory, "todo-api", false, "starter", "frankfurt", "main");
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(RenderPayloads.CreateService(spec, "tea-1", "https://github.com/acme/todo")));
        var root = body.RootElement;
        Assert.Equal(
            ("web_service", "todo-api", "tea-1", "https://github.com/acme/todo", "main", "commit", "backend"),
            (root.GetProperty("type").GetString(), root.GetProperty("name").GetString(), root.GetProperty("ownerId").GetString(), root.GetProperty("repo").GetString(),
                root.GetProperty("branch").GetString(), root.GetProperty("autoDeployTrigger").GetString(), root.GetProperty("rootDir").GetString()));
        var details = root.GetProperty("serviceDetails");
        Assert.Equal(
            ("docker", "/api/health/ready", MigrateCommand, "starter", "frankfurt"),
            (details.GetProperty("runtime").GetString(), details.GetProperty("healthCheckPath").GetString(), details.GetProperty("preDeployCommand").GetString(),
                details.GetProperty("plan").GetString(), details.GetProperty("region").GetString()));

        // Render reads Docker paths from the root directory: backend/Dockerfile in a `backend` root is ./Dockerfile.
        var docker = details.GetProperty("envSpecificDetails");
        Assert.Equal(("./Dockerfile", "."), (docker.GetProperty("dockerfilePath").GetString(), docker.GetProperty("dockerContext").GetString()));
    }

    [Fact]
    public void FR147_The_Render_preview_service_is_named_dash_preview_with_auto_deploy_off()
    {
        var plan = DeploymentBuildRules.Derive(AspNetOnRender(), DeploymentComponent.Backend, DeploymentProvider.Render, MigrateCommand);

        var spec = RenderRules.ServiceSpec(DeploymentComponent.Backend, plan.Config, plan.RootDirectory, "todo-api", true, null, null, "main");

        Assert.Equal(("todo-api-preview", "off", MigrateCommand), (spec.Name, spec.AutoDeployTrigger, spec.PreDeployCommand));
    }

    [Fact]
    public void FR144_A_Vite_frontend_on_Render_is_a_static_site_and_Next_js_a_Node_web_service()
    {
        var vite = RenderRules.ServiceSpec(
            DeploymentComponent.Frontend, new DeploymentBuildConfig { BuildCommand = "npm ci && npm run build", OutputDirectory = "dist" }, "frontend", "todo-web", false, "free", "oregon", "trunk");
        Assert.Equal(("static_site", "dist", (string?)null, (string?)null, "trunk"), (vite.Type, vite.PublishPath, vite.Plan, vite.Runtime, vite.Branch));
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(RenderPayloads.CreateService(vite, "tea-1", "https://github.com/acme/todo")));
        Assert.False(body.RootElement.GetProperty("serviceDetails").TryGetProperty("plan", out _));

        var next = RenderRules.ServiceSpec(
            DeploymentComponent.Frontend, new DeploymentBuildConfig { BuildCommand = "npm ci && npm run build", StartCommand = "npm run start" }, ".", "todo", false, null, null, "main");
        Assert.Equal(("web_service", "node", "npm run start", string.Empty), (next.Type, next.Runtime, next.StartCommand, next.RootDir));
    }

    [Fact]
    public void FR146_The_Render_static_site_proxy_rewrites_api_to_API_ORIGIN()
    {
        var route = RenderRules.ApiProxyRoute("https://todo-api.onrender.com/");

        Assert.Equal(new RenderRoute("rewrite", "/api/*", "https://todo-api.onrender.com/api/*"), route);
    }

    [Fact]
    public void FR146_The_proxy_route_goes_ahead_of_the_SPA_fallback_and_replaces_a_stale_one()
    {
        var fallback = new RenderRoute("rewrite", "/*", "/index.html");
        var proxy = RenderRules.ApiProxyRoute("https://todo-api.onrender.com");

        var merged = RenderRules.MergeRoutes([fallback, new RenderRoute("rewrite", "/api/*", "https://old.example/api/*")], proxy);

        Assert.Equal([proxy, fallback], merged);
    }

    [Fact]
    public void FR149_S4_20_The_Railway_adoption_recheck_waits_at_least_20_seconds()
    {
        Assert.Equal(Now.AddSeconds(20), ProductionDeploymentRules.LookAgainAt(Now, Now, RailwayRules.MinPollInterval));
        Assert.Equal(Now.AddSeconds(40), ProductionDeploymentRules.LookAgainAt(Now, Now.AddSeconds(20), RailwayRules.MinPollInterval));
        Assert.Null(ProductionDeploymentRules.LookAgainAt(Now, Now.AddSeconds(40), RailwayRules.MinPollInterval));
        Assert.Equal(Now.AddSeconds(30), ProductionDeploymentRules.LookAgainAt(Now, Now.AddSeconds(25)));
    }

    [Fact]
    public void FR145_Render_env_sync_sends_the_full_merged_set()
    {
        var current = new Dictionary<string, string> { ["KEEP"] = "1", ["OLD"] = "x", ["CHANGE"] = "a" };

        var merged = RenderRules.MergeEnv(current, new Dictionary<string, string> { ["CHANGE"] = "b", ["NEW"] = "n" }, ["OLD"]);

        Assert.Equal(["CHANGE=b", "KEEP=1", "NEW=n"], merged.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    [Fact]
    public void FR145_Render_and_Railway_env_ids_round_trip_per_environment()
    {
        Assert.Equal(("API_ORIGIN", DeploymentEnvironment.Preview), RenderRules.ParseEnvId(RenderRules.EnvId("API_ORIGIN", DeploymentEnvironment.Preview)));
        Assert.Equal(("DB:URL", DeploymentEnvironment.Production), RailwayRules.ParseEnvId(RailwayRules.EnvId("DB:URL", DeploymentEnvironment.Production)));
        Assert.Null(RenderRules.ParseEnvId("env_abc"));
    }

    [Fact]
    public void FR150_Render_deployment_ids_carry_the_service()
    {
        Assert.Equal(("srv-p", "dep-1"), RenderRules.ParseDeploymentId(RenderRules.DeploymentId("srv-p", "dep-1"), "srv-x"));
        Assert.Equal(("srv-x", "dep-1"), RenderRules.ParseDeploymentId("dep-1", "srv-x"));
    }

    [Fact]
    public void S4_20_Render_logs_allow_30_requests_a_minute_per_user()
    {
        var recent = Enumerable.Range(0, 30).Select(i => Now.AddSeconds(-50 + i)).ToList();

        var refused = RenderRules.DecideLogRequest(recent, Now);
        Assert.False(refused.Allowed);
        Assert.Equal(Now.AddSeconds(10), refused.RetryAt);
        Assert.True(RenderRules.DecideLogRequest(recent.Skip(1).ToList(), Now).Allowed);
    }

    [Fact]
    public void FR144_Git_refusals_on_create_are_PLATFORM_GIT_NOT_CONNECTED()
    {
        Assert.True(RenderRules.IsGitNotConnected(400, "Render: invalid repo: no access to github.com/acme/todo"));
        Assert.False(RenderRules.IsGitNotConnected(400, "Render: name is required"));
        Assert.True(RailwayRules.IsGitNotConnected("Railway: GitHub repo not found or the Railway app isn't installed"));
        Assert.False(RailwayRules.IsGitNotConnected("Railway: Problem processing request"));
    }

    [Fact]
    public void FR144_Railway_resource_ids_hold_project_service_and_production_environment()
    {
        var resource = new RailwayResource("p1", "s1", "e1");

        Assert.Equal("p1:s1:e1", RailwayRules.ResourceId(resource));
        Assert.Equal(resource, RailwayRules.ParseResourceId("p1:s1:e1"));
        Assert.Equal(new RailwayResource("p1", "s1", null), RailwayRules.ParseResourceId("p1:s1"));
        Assert.Null(RailwayRules.ParseResourceId("p1"));
    }

    [Fact]
    public void FR144_Railway_instance_settings_root_the_service_watch_its_folder_and_run_the_pre_deploy()
    {
        var plan = DeploymentBuildRules.Derive(AspNetOnRender(), DeploymentComponent.Backend, DeploymentProvider.Railway, MigrateCommand);

        var settings = RailwayRules.InstanceSettings(plan.Config, plan.RootDirectory);

        Assert.Equal(("/backend", "Dockerfile", "/api/health/ready"), (settings.RootDirectory, settings.DockerfilePath, settings.HealthcheckPath));
        Assert.Equal(["/backend/**"], settings.WatchPatterns!);
        Assert.Equal([MigrateCommand], settings.PreDeployCommand!);
        Assert.Null(settings.BuildCommand);
    }

    [Fact]
    public void S4_20_Railway_is_polled_at_most_every_20_seconds()
    {
        var since = Now.AddSeconds(-30);

        var railway = DeploymentPollRules.Decide(since, Now, DeploymentStatus.Building, DeploymentPollRules.MinInterval(DeploymentProvider.Railway));
        var render = DeploymentPollRules.Decide(since, Now, DeploymentStatus.Building, DeploymentPollRules.MinInterval(DeploymentProvider.Render));

        Assert.Equal(Now.AddSeconds(20), railway.NextPollAt);
        Assert.Equal(Now.AddSeconds(10), render.NextPollAt);
    }

    [Fact]
    public void FR146_A_same_origin_SPA_on_Railway_proxies_over_the_private_network()
    {
        var pages = StackTestCatalog.Valid(StackTestCatalog.Sel(
            frontend: "fe-react-vite", backend: "be-fastapi", vcs: "vcs-github", frontendHost: "deploy-github-pages", backendHost: "deploy-railway"));
        var composition = pages with { OriginModel = OriginModels.SameOrigin, Deploy = pages.Deploy with { FrontendHost = "deploy-railway" } };
        var backend = new DerivedEnvTarget(DeploymentComponent.Backend, DeploymentProvider.Railway, "todo-api", "https://todo-api.up.railway.app", null);

        var origin = Assert.Single(DerivedEnvRules.Derive(new DerivedEnvInput(composition, "VITE_", [backend], [])));

        Assert.Equal(DerivedEnvRules.ApiOrigin, origin.Key);
        Assert.Equal("http://${{todo-api.RAILWAY_PRIVATE_DOMAIN}}:${{todo-api.PORT}}", origin.Values[DeploymentEnvironment.Production]);
        Assert.Equal(origin.Values[DeploymentEnvironment.Production], origin.Values[DeploymentEnvironment.Preview]);
        Assert.False(origin.Public);
    }
}
