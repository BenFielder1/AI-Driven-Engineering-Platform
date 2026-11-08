using System.Net;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Platforms;
using static Aide.UnitTests.Deployments.PlatformFixtures;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-190, FR-192, §4.28: shared projects on Railway, Render, Vercel and the Fake, against stubbed APIs (no network).</summary>
public class SharedProjectProviderTests
{
    private static readonly DeploymentAuth RailwayAuth = new("rw_token", null, "ws-1");
    private static readonly DeploymentAuth RenderAuth = new("rnd_key", null, "tea-1", "tea-1");
    private static readonly DatabaseAuth DbRailwayAuth = new("rw_token", "ws-1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlatformHttp Http(PlatformStub stub) => new(new PlatformStubFactory(stub), new ManualTime(Now));

    private static PlatformStub Graphql(params (string Operation, string Json)[] routes) =>
        new PlatformStub().On("POST", "/graphql/v2", call =>
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

    private static IEnumerable<string> Bodies(PlatformStub stub, string operation) =>
        stub.Where("POST", "/graphql/v2").Select(c => c.Body).Where(b => b.Contains(operation, StringComparison.Ordinal));

    private static DeploymentTargetRef Target(DeploymentProvider provider, DeploymentComponent component = DeploymentComponent.Backend) => new(
        Guid.CreateVersion7(), provider, component, null, null, null, "backend", new DeploymentBuildConfig { DockerfilePath = "backend/Dockerfile" }, GitHubRepo);

    // ── Railway ──────────────────────────────────────────────────────────

    private const string NoProjects = """{"data":{"projects":{"edges":[]}}}""";
    private const string TakenProject = """{"data":{"projects":{"edges":[{"node":{"id":"p9","name":"todo"}}]}}}""";

    private static readonly (string, string)[] RailwayService =
    [
        ("services { edges", """{"data":{"project":{"id":"p1","services":{"edges":[]}}}}"""),
        ("environments { edges", """{"data":{"project":{"id":"p1","environments":{"edges":[{"node":{"id":"e-prod","name":"production"}},{"node":{"id":"e-prev","name":"aide-preview"}}]}}}}"""),
        ("serviceCreate", """{"data":{"serviceCreate":{"id":"s1","name":"todo-api"}}}"""),
        ("serviceInstanceUpdate", """{"data":{"serviceInstanceUpdate":true}}"""),
        ("variableCollectionUpsert", """{"data":{"variableCollectionUpsert":true}}"""),
        ("domains(", """{"data":{"domains":{"serviceDomains":[{"domain":"todo-api.up.railway.app"}]}}}"""),
    ];

    [Fact]
    public async Task FR190_Railway_makes_the_project_with_prDeploys_off()
    {
        var stub = Graphql(("projects(workspaceId", NoProjects), ("projectCreate", """{"data":{"projectCreate":{"id":"p1","name":"todo"}}}"""));

        var created = await new RailwayDeploymentProvider(Http(stub)).CreateSharedProjectAsync(RailwayAuth, new SharedProjectRequest("todo", null), Ct);

        Assert.Equal("p1", created.Id);
        var body = Assert.Single(Bodies(stub, "projectCreate"));
        Assert.Contains("\"workspaceId\":\"ws-1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"prDeploys\":false", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR190_An_existing_name_is_PLATFORM_RESOURCE_EXISTS()
    {
        var stub = Graphql(("projects(workspaceId", TakenProject));

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() =>
            new RailwayDeploymentProvider(Http(stub)).CreateSharedProjectAsync(RailwayAuth, new SharedProjectRequest("todo", null), Ct));

        Assert.Equal(DeploymentFailure.ResourceExists, ex.Failure);
        Assert.Equal(ErrorCodes.PlatformResourceExists, VercelErrorRules.ErrorCodeFor(ex.Failure));
        Assert.Empty(Bodies(stub, "projectCreate"));
    }

    [Fact]
    public async Task FR192_Link_makes_the_service_in_an_empty_project()
    {
        var stub = Graphql([.. RailwayService, ("project(id", """{"data":{"project":{"id":"p1","name":"todo"}}}""")]);

        var result = await new RailwayDeploymentProvider(Http(stub)).SetupTargetAsync(
            RailwayAuth, new DeploymentSetupRequest(Target(DeploymentProvider.Railway), false, null, null, null, "p1"), Ct);

        Assert.Equal("p1:s1:e-prod", result.ResourceId);
        Assert.Single(Bodies(stub, "serviceCreate"));
    }

    [Fact]
    public async Task FR192_Link_to_a_project_with_other_services_but_none_of_this_repo_is_still_refused()
    {
        var stub = Graphql(("services { edges", """{"data":{"project":{"id":"p1","services":{"edges":[{"node":{"id":"s9","name":"other"}}]}}}}"""));

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => new RailwayDeploymentProvider(Http(stub)).SetupTargetAsync(
            RailwayAuth, new DeploymentSetupRequest(Target(DeploymentProvider.Railway), false, null, null, null, "p1"), Ct));

        Assert.Equal(DeploymentFailure.NotFound, ex.Failure);
    }

    [Fact]
    public async Task FR192_A_standalone_create_never_finds_a_project_by_repository_name()
    {
        // The repository is acme/todo, and a project "todo" belongs to another AIDE project: the window's name decides.
        var stub = Graphql([("projects(workspaceId", TakenProject), ("projectCreate", """{"data":{"projectCreate":{"id":"p2","name":"mine"}}}"""), .. RailwayService]);

        var result = await new RailwayDeploymentProvider(Http(stub)).SetupTargetAsync(
            RailwayAuth, new DeploymentSetupRequest(Target(DeploymentProvider.Railway), true, "mine", null, null, null), Ct);

        Assert.StartsWith("p2:", result.ResourceId, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"mine\"", Assert.Single(Bodies(stub, "projectCreate")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR192_Two_AIDE_projects_with_one_name_dont_share_a_Railway_project()
    {
        var stub = Graphql(("projects(workspaceId", TakenProject));

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => new RailwayDeploymentProvider(Http(stub)).SetupTargetAsync(
            RailwayAuth, new DeploymentSetupRequest(Target(DeploymentProvider.Railway), true, "todo", null, null, null), Ct));

        Assert.Equal(DeploymentFailure.ResourceExists, ex.Failure);
    }

    [Fact]
    public async Task FR190_A_part_of_a_group_goes_in_the_shared_project_without_looking_for_one()
    {
        var stub = Graphql(RailwayService);

        var result = await new RailwayDeploymentProvider(Http(stub)).SetupTargetAsync(
            RailwayAuth, new DeploymentSetupRequest(Target(DeploymentProvider.Railway), true, "todo-api", null, null, null) { SharedProjectId = "p1" }, Ct);

        Assert.Equal("p1:s1:e-prod", result.ResourceId);
        Assert.Empty(Bodies(stub, "projects(workspaceId"));
        Assert.Empty(Bodies(stub, "projectCreate"));
    }

    [Fact]
    public async Task FR191_A_token_that_cant_see_the_project_is_refused()
    {
        var stub = Graphql(("project(id", """{"errors":[{"message":"Project not found"}]}"""));

        await Assert.ThrowsAsync<DeploymentProviderException>(() => new RailwayDeploymentProvider(Http(stub)).GetSharedProjectAsync(RailwayAuth, "p1", Ct));
    }

    // ── Railway Postgres ─────────────────────────────────────────────────

    private static DatabaseSetupRequest DbRequest(DatabaseHostContext host, DeploymentEnvironment environment = DeploymentEnvironment.Production, string name = "todo-db", string? password = "pw") =>
        new(Guid.CreateVersion7(), environment, DatabaseSetupMode.Create, name, null, null, null, password, host);

    private static readonly (string, string)[] PostgresRoutes =
    [
        ("environments { edges", """{"data":{"project":{"id":"p3","environments":{"edges":[{"node":{"id":"e-prod","name":"production"}}]}}}}"""),
        ("environmentCreate", """{"data":{"environmentCreate":{"id":"e-prev","name":"aide-preview"}}}"""),
        ("volumes", """{"data":{"project":{"volumes":{"edges":[]}}}}"""),
        ("volumeCreate", """{"data":{"volumeCreate":{"id":"v1"}}}"""),
        ("variableCollectionUpsert", """{"data":{"variableCollectionUpsert":true}}"""),
        ("serviceInstanceDeployV2", """{"data":{"serviceInstanceDeployV2":"dep"}}"""),
        ("serviceCreate", """{"data":{"serviceCreate":{"id":"svc-pg","name":"Postgres"}}}"""),
        ("services { edges", """{"data":{"project":{"id":"p3","services":{"edges":[]}}}}"""),
    ];

    [Fact]
    public async Task FR192_A_standalone_Railway_database_gets_its_own_project_from_the_windows_name()
    {
        var stub = Graphql([("projects(workspaceId", NoProjects), ("projectCreate", """{"data":{"projectCreate":{"id":"p3","name":"todo-db"}}}"""), .. PostgresRoutes]);
        var provider = new RailwayPostgresProvider(Http(stub));

        var project = await provider.CreateOwnProjectAsync(DbRailwayAuth, "todo-db", Ct);
        var result = await provider.SetupAsync(DbRailwayAuth, DbRequest(DatabaseHostContext.None with { RailwayStandsAlone = true, RailwayOwnProjectId = project }), Ct);

        Assert.Equal("p3:svc-pg:e-prod", result.ResourceId);
        Assert.Contains("\"name\":\"todo-db\"", Assert.Single(Bodies(stub, "projectCreate")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR192_A_standalone_Railway_database_needs_no_Railway_target_and_refuses_a_taken_name()
    {
        var stub = Graphql(("projects(workspaceId", """{"data":{"projects":{"edges":[{"node":{"id":"p9","name":"todo-db"}}]}}}"""));

        var ex = await Assert.ThrowsAsync<DatabaseProviderException>(() => new RailwayPostgresProvider(Http(stub)).CreateOwnProjectAsync(DbRailwayAuth, "todo-db", Ct));

        Assert.Equal(DeploymentFailure.ResourceExists, ex.Failure);
    }

    [Fact]
    public async Task FR192_The_standalone_preview_uses_productions_project_and_makes_its_environment()
    {
        var stub = Graphql(PostgresRoutes);

        var result = await new RailwayPostgresProvider(Http(stub)).SetupAsync(
            DbRailwayAuth, DbRequest(DatabaseHostContext.None with { RailwayStandsAlone = true, RailwayOwnProjectId = "p3" }, DeploymentEnvironment.Preview, "todo-db-preview"), Ct);

        Assert.Equal("p3:svc-pg:e-prev", result.ResourceId);
        Assert.Empty(Bodies(stub, "projectCreate"));
        Assert.Single(Bodies(stub, "environmentCreate"));
    }

    [Fact]
    public async Task FR190_A_grouped_Railway_database_goes_in_the_shared_project()
    {
        var stub = Graphql(PostgresRoutes);

        var result = await new RailwayPostgresProvider(Http(stub)).SetupAsync(DbRailwayAuth, DbRequest(DatabaseHostContext.None with { SharedProjectId = "p3" }), Ct);

        Assert.Equal("p3:svc-pg:e-prod", result.ResourceId);
        Assert.Empty(Bodies(stub, "projectCreate"));
    }

    private static DatabaseSetupRequest LinkPreview(string? password) =>
        new(Guid.CreateVersion7(), DeploymentEnvironment.Preview, DatabaseSetupMode.Link, "todo-db-preview", null, null, "svc-pg", password, DatabaseHostContext.None with { SharedProjectId = "p3" });

    private const string LinkedService = """{"data":{"project":{"id":"p3","services":{"edges":[{"node":{"id":"svc-pg","name":"Postgres"}}]}}}}""";

    [Fact]
    public async Task FR190_A_group_linking_a_Postgres_provisions_its_preview_instance()
    {
        var stub = Graphql([("services { edges", LinkedService), .. PostgresRoutes]);

        var result = await new RailwayPostgresProvider(Http(stub)).SetupAsync(DbRailwayAuth, LinkPreview("pw-preview"), Ct);

        Assert.Equal(("p3:svc-pg:e-prev", true), (result.ResourceId, result.PasswordSet));
        Assert.Contains("pw-preview", Assert.Single(Bodies(stub, "variableCollectionUpsert")), StringComparison.Ordinal);
        Assert.Contains("\"environmentId\":\"e-prev\"", Assert.Single(Bodies(stub, "volumeCreate")), StringComparison.Ordinal);
        Assert.Contains("\"environmentId\":\"e-prev\"", Assert.Single(Bodies(stub, "serviceInstanceDeployV2")), StringComparison.Ordinal);
        Assert.Empty(Bodies(stub, "serviceCreate"));
    }

    [Fact]
    public async Task FR190_A_retried_preview_instance_with_its_volume_keeps_its_password()
    {
        var stub = Graphql(
            ("environments { edges", """{"data":{"project":{"id":"p3","environments":{"edges":[{"node":{"id":"e-prod","name":"production"}},{"node":{"id":"e-prev","name":"aide-preview"}}]}}}}"""),
            ("volumes", """{"data":{"project":{"volumes":{"edges":[{"node":{"id":"v1","volumeInstances":{"edges":[{"node":{"serviceId":"svc-pg","environmentId":"e-prev"}}]}}}]}}}}"""),
            ("serviceInstanceDeployV2", """{"data":{"serviceInstanceDeployV2":"dep"}}"""),
            ("services { edges", LinkedService));

        var result = await new RailwayPostgresProvider(Http(stub)).SetupAsync(DbRailwayAuth, LinkPreview("pw-new"), Ct);

        Assert.False(result.PasswordSet);
        Assert.Empty(Bodies(stub, "variableCollectionUpsert"));
        Assert.Empty(Bodies(stub, "volumeCreate"));
    }

    [Fact]
    public async Task FR190_A_linked_production_Postgres_is_left_as_it_stands()
    {
        var stub = Graphql([("services { edges", LinkedService), .. PostgresRoutes]);

        await new RailwayPostgresProvider(Http(stub)).SetupAsync(
            DbRailwayAuth,
            new DatabaseSetupRequest(Guid.CreateVersion7(), DeploymentEnvironment.Production, DatabaseSetupMode.Link, "todo-db", null, null, "svc-pg", null, DatabaseHostContext.None with { SharedProjectId = "p3" }),
            Ct);

        Assert.Empty(Bodies(stub, "variableCollectionUpsert"));
        Assert.Empty(Bodies(stub, "serviceInstanceDeployV2"));
    }

    // ── FR-195: Postgres 18 for new databases only ───────────────────────

    private const string BothEnvironments =
        """{"data":{"project":{"id":"p3","environments":{"edges":[{"node":{"id":"e-prod","name":"production"}},{"node":{"id":"e-prev","name":"aide-preview"}}]}}}}""";

    private const string ProductionVolumeOnly =
        """{"data":{"project":{"volumes":{"edges":[{"node":{"id":"v1","volumeInstances":{"edges":[{"node":{"serviceId":"svc-pg","environmentId":"e-prod"}}]}}}]}}}}""";

    private static PlatformStub ExistingPostgres() => Graphql(
        ("environments { edges", BothEnvironments),
        ("volumes", ProductionVolumeOnly),
        ("serviceInstanceUpdate", """{"data":{"serviceInstanceUpdate":true}}"""),
        ("volumeCreate", """{"data":{"volumeCreate":{"id":"v2"}}}"""),
        ("variableCollectionUpsert", """{"data":{"variableCollectionUpsert":true}}"""),
        ("serviceInstanceDeployV2", """{"data":{"serviceInstanceDeployV2":"dep"}}"""),
        ("services { edges", LinkedService));

    [Fact]
    public async Task FR195_Re_setting_up_a_production_Postgres_created_on_17_never_changes_its_image_or_volume()
    {
        var stub = ExistingPostgres();

        var result = await new RailwayPostgresProvider(Http(stub)).SetupAsync(DbRailwayAuth, DbRequest(DatabaseHostContext.None with { SharedProjectId = "p3" }), Ct);

        Assert.Equal("p3:svc-pg:e-prod", result.ResourceId);
        Assert.Empty(Bodies(stub, "serviceCreate"));
        Assert.Empty(Bodies(stub, "serviceInstanceUpdate"));
        Assert.Empty(Bodies(stub, "volumeCreate"));
        Assert.Empty(Bodies(stub, "postgres-ssl"));
    }

    [Fact]
    public async Task FR195_A_new_preview_Postgres_next_to_a_17_production_one_starts_on_18_and_production_is_untouched()
    {
        var stub = ExistingPostgres();

        var result = await new RailwayPostgresProvider(Http(stub)).SetupAsync(
            DbRailwayAuth, DbRequest(DatabaseHostContext.None with { SharedProjectId = "p3" }, DeploymentEnvironment.Preview, "todo-db-preview"), Ct);

        Assert.Equal("p3:svc-pg:e-prev", result.ResourceId);
        var update = Assert.Single(Bodies(stub, "serviceInstanceUpdate"));
        Assert.Contains("\"environmentId\":\"e-prev\"", update, StringComparison.Ordinal);
        Assert.Contains("\"image\":\"ghcr.io/railwayapp-templates/postgres-ssl:18\"", update, StringComparison.Ordinal);
        Assert.DoesNotContain(Bodies(stub, "postgres-ssl"), b => b.Contains("e-prod", StringComparison.Ordinal));
        Assert.Contains("\"mountPath\":\"/var/lib/postgresql/data\"", Assert.Single(Bodies(stub, "volumeCreate")), StringComparison.Ordinal);
        Assert.Empty(Bodies(stub, "serviceCreate"));
    }

    [Fact]
    public async Task FR195_Adopting_an_existing_volume_less_production_Postgres_never_changes_its_image()
    {
        var stub = Graphql(
            ("environments { edges", BothEnvironments),
            ("volumes", """{"data":{"project":{"volumes":{"edges":[]}}}}"""),
            ("serviceInstanceUpdate", """{"data":{"serviceInstanceUpdate":true}}"""),
            ("volumeCreate", """{"data":{"volumeCreate":{"id":"v2"}}}"""),
            ("variableCollectionUpsert", """{"data":{"variableCollectionUpsert":true}}"""),
            ("serviceInstanceDeployV2", """{"data":{"serviceInstanceDeployV2":"dep"}}"""),
            ("services { edges", LinkedService));

        var result = await new RailwayPostgresProvider(Http(stub)).SetupAsync(DbRailwayAuth, DbRequest(DatabaseHostContext.None with { SharedProjectId = "p3" }), Ct);

        Assert.Equal("p3:svc-pg:e-prod", result.ResourceId);
        Assert.Empty(Bodies(stub, "serviceInstanceUpdate"));
        Assert.Empty(Bodies(stub, "serviceCreate"));
        Assert.Empty(Bodies(stub, "postgres-ssl"));
    }

    [Fact]
    public async Task FR195_A_linked_Postgres_keeps_its_image_in_production_and_preview()
    {
        var stub = ExistingPostgres();
        var provider = new RailwayPostgresProvider(Http(stub));

        await provider.SetupAsync(
            DbRailwayAuth,
            new DatabaseSetupRequest(Guid.CreateVersion7(), DeploymentEnvironment.Production, DatabaseSetupMode.Link, "todo-db", null, null, "svc-pg", null, DatabaseHostContext.None with { SharedProjectId = "p3" }),
            Ct);
        await provider.SetupAsync(DbRailwayAuth, LinkPreview("pw-preview"), Ct);

        Assert.Empty(Bodies(stub, "serviceInstanceUpdate"));
        Assert.Empty(Bodies(stub, "postgres-ssl"));
    }

    [Fact]
    public async Task FR192_A_standalone_Railway_database_reaches_its_apps_through_a_TCP_proxy()
    {
        var stub = Graphql(
            ("deployments(", """{"data":{"deployments":{"edges":[{"node":{"id":"d1","status":"SUCCESS"}}]}}}"""),
            ("tcpProxies(", """{"data":{"tcpProxies":[]}}"""),
            ("tcpProxyCreate", """{"data":{"tcpProxyCreate":{"domain":"gondola.proxy.rlwy.net","proxyPort":34567,"applicationPort":5432}}}"""),
            ("variables(", """{"data":{"variables":{"POSTGRES_USER":"postgres","POSTGRES_DB":"railway"}}}"""));

        var poll = await new RailwayPostgresProvider(Http(stub)).PollAsync(
            DbRailwayAuth,
            new DatabaseRef("p3:svc-pg:e-prod", DeploymentEnvironment.Production, "Postgres", "p@ss", null, DatabaseHostContext.None with { RailwayStandsAlone = true, RailwayPublicUrl = true }),
            Ct);

        Assert.Equal(ManagedDatabaseStatus.Available, poll.Status);
        Assert.Equal("postgresql://postgres:p%40ss@gondola.proxy.rlwy.net:34567/railway", poll.Connection!.Url);
        Assert.True(poll.Connection.RailwaySeparateProject);
        Assert.Contains("\"applicationPort\":5432", Assert.Single(Bodies(stub, "tcpProxyCreate")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR192_An_existing_TCP_proxy_is_reused_and_a_linked_databases_password_comes_from_its_service()
    {
        var stub = Graphql(
            ("deployments(", """{"data":{"deployments":{"edges":[{"node":{"id":"d1","status":"SUCCESS"}}]}}}"""),
            ("tcpProxies(", """{"data":{"tcpProxies":[{"domain":"a.proxy.rlwy.net","proxyPort":1234,"applicationPort":5432}]}}"""),
            ("variables(", """{"data":{"variables":{"POSTGRES_PASSWORD":"linked"}}}"""));

        var poll = await new RailwayPostgresProvider(Http(stub)).PollAsync(
            DbRailwayAuth,
            new DatabaseRef("p3:svc-pg:e-prod", DeploymentEnvironment.Production, "Postgres", null, null, DatabaseHostContext.None with { RailwayStandsAlone = true, RailwayPublicUrl = true }),
            Ct);

        Assert.Equal("postgresql://postgres:linked@a.proxy.rlwy.net:1234/railway", poll.Connection!.Url);
        Assert.Empty(Bodies(stub, "tcpProxyCreate"));
    }

    [Fact]
    public async Task FR152_A_0_4_0_Railway_database_next_to_its_target_stays_on_the_private_reference()
    {
        var stub = Graphql(("deployments(", """{"data":{"deployments":{"edges":[{"node":{"id":"d1","status":"SUCCESS"}}]}}}"""));

        var poll = await new RailwayPostgresProvider(Http(stub)).PollAsync(
            DbRailwayAuth, new DatabaseRef("p1:svc-pg:e-prod", DeploymentEnvironment.Production, "Postgres", "pw", null, DatabaseHostContext.None), Ct);

        Assert.Null(poll.Connection!.Url);
        Assert.Equal("Postgres", poll.Connection.RailwayService);
        Assert.False(poll.Connection.RailwaySeparateProject);
    }

    // ── Render ───────────────────────────────────────────────────────────

    private static RenderDeploymentProvider Render(PlatformStub stub) => new(Http(stub), new RenderLogRateLimiter(new ManualTime(Now)));

    private static PlatformStub RenderServices() => new PlatformStub()
        .On("GET", "services?", "[]")
        .On("POST", "services", call => PlatformStub.Json(call.Body.Contains("\"todo-api-preview\"", StringComparison.Ordinal)
            ? """{"service":{"id":"srv-v","name":"todo-api-preview","serviceDetails":{}}}"""
            : """{"service":{"id":"srv-p","name":"todo-api","serviceDetails":{}}}""", HttpStatusCode.Created));

    [Fact]
    public async Task FR192_Render_makes_the_project_with_production_and_preview_environments()
    {
        var stub = new PlatformStub().On("GET", "projects?", "[]").On("POST", "projects", """{"id":"prj-1","name":"todo"}""", HttpStatusCode.Created);

        var created = await Render(stub).CreateSharedProjectAsync(RenderAuth, new SharedProjectRequest("todo", "frankfurt"), Ct);

        Assert.Equal("prj-1", created.Id);
        var body = Assert.Single(stub.Where("POST", "projects")).Body;
        Assert.Contains("\"ownerId\":\"tea-1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"environments\":[{\"name\":\"production\"},{\"name\":\"preview\"}]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR190_Render_refuses_an_existing_project_name()
    {
        var stub = new PlatformStub().On("GET", "projects?", """[{"project":{"id":"prj-9","name":"todo"},"cursor":"c"}]""");

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => Render(stub).CreateSharedProjectAsync(RenderAuth, new SharedProjectRequest("todo", null), Ct));

        Assert.Equal(DeploymentFailure.ResourceExists, ex.Failure);
    }

    [Fact]
    public async Task FR192_Render_places_each_service_in_its_projects_environment()
    {
        var stub = RenderServices()
            .On("GET", "projects/prj-1", """{"id":"prj-1","name":"todo","environmentIds":["evm-p","evm-v"]}""")
            .On("GET", "environments/evm-p", """{"id":"evm-p","name":"production"}""")
            .On("GET", "environments/evm-v", """{"id":"evm-v","name":"preview"}""")
            .On("POST", "environments/", "{}");

        var result = await Render(stub).SetupTargetAsync(
            RenderAuth, new DeploymentSetupRequest(Target(DeploymentProvider.Render), true, "todo-api", "frankfurt", "starter", null) { SharedProjectId = "prj-1" }, Ct);

        Assert.False(result.PlacementUnavailable);
        Assert.Contains("srv-p", Assert.Single(stub.Where("POST", "environments/evm-p/resources")).Body, StringComparison.Ordinal);
        Assert.Contains("srv-v", Assert.Single(stub.Where("POST", "environments/evm-v/resources")).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR192_When_Render_cant_place_a_service_the_part_still_succeeds_and_says_so()
    {
        var stub = RenderServices().On("GET", "projects/prj-1", """{"error":"no"}""", HttpStatusCode.NotFound);

        var result = await Render(stub).SetupTargetAsync(
            RenderAuth, new DeploymentSetupRequest(Target(DeploymentProvider.Render), true, "todo-api", null, null, null) { SharedProjectId = "prj-1" }, Ct);

        Assert.Equal("srv-p", result.ResourceId);
        Assert.True(result.PlacementUnavailable);
    }

    [Fact]
    public async Task FR144_A_standalone_Render_service_makes_no_placement_call()
    {
        var stub = RenderServices();

        var result = await Render(stub).SetupTargetAsync(RenderAuth, new DeploymentSetupRequest(Target(DeploymentProvider.Render), true, "todo-api", null, null, null), Ct);

        Assert.False(result.PlacementUnavailable);
        Assert.Empty(stub.Where("GET", "projects/"));
    }

    [Fact]
    public async Task FR192_Render_Postgres_is_placed_in_the_projects_environment()
    {
        var stub = new PlatformStub()
            .On("GET", "postgres?", "[]")
            .On("POST", "postgres", """{"id":"dpg-1","name":"todo-db"}""", HttpStatusCode.Created)
            .On("GET", "projects/prj-1", """{"id":"prj-1","environmentIds":["evm-p"]}""")
            .On("GET", "environments/evm-p", """{"id":"evm-p","name":"production"}""")
            .On("POST", "environments/", "{}");

        var result = await new RenderPostgresProvider(Http(stub), new ManualTime(Now)).SetupAsync(
            new DatabaseAuth("rnd_key", "tea-1"), DbRequest(DatabaseHostContext.None with { SharedProjectId = "prj-1" }), Ct);

        Assert.False(result.PlacementUnavailable);
        Assert.Contains("dpg-1", Assert.Single(stub.Where("POST", "environments/evm-p/resources")).Body, StringComparison.Ordinal);
    }

    // ── Fake ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FR192_The_Fake_records_one_shared_id()
    {
        var fake = new FakeDeploymentProvider(DeploymentProvider.Railway, null!, new());
        var auth = new DeploymentAuth("fake", null);

        Assert.Equal("fake_railway_project_todo", (await fake.CreateSharedProjectAsync(auth, new SharedProjectRequest("todo", null), Ct)).Id);
        await Assert.ThrowsAsync<DeploymentProviderException>(() => fake.CreateSharedProjectAsync(auth, new SharedProjectRequest("fake-exists", null), Ct));
        Assert.Null(await fake.GetSharedProjectAsync(new DeploymentAuth("fake_no_access", null), "fake_railway_project_todo", Ct));
    }
}
