using System.Net;
using System.Text.Json;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Databases;
using Aide.Infrastructure.Platforms;
using Aide.UnitTests.Deployments;

namespace Aide.UnitTests.Databases;

/// <summary>FR-152, FR-153, §4.21: the Supabase, Render and Railway database providers against stubbed platform APIs.</summary>
public class DatabaseProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlatformHttp Http(PlatformStub stub) => new(new PlatformStubFactory(stub), new ManualTime(Now));

    private static DatabaseSetupRequest Request(DatabaseSetupMode mode, string name = "todo", string? region = "eu-west-2", string? link = null, string? password = "pw-123", DatabaseHostContext? host = null) =>
        new(Guid.CreateVersion7(), DeploymentEnvironment.Production, mode, name, region, null, link, password, host ?? DatabaseHostContext.None);

    private static DatabaseRef Ref(string id, DatabaseHostContext? host = null) =>
        new(id, DeploymentEnvironment.Production, "todo", "pw-123", null, host ?? DatabaseHostContext.None);

    // ------------------------------------------------------------------ Supabase

    [Fact]
    public async Task FR152_Supabase_create_posts_region_selection_with_the_AIDE_password_in_the_organisation()
    {
        var stub = new PlatformStub()
            .On("GET", "organizations/acme/projects", "[]")
            .On("POST", "projects", """{"ref":"abcd","name":"todo","region":"eu-west-2","status":"COMING_UP"}""", HttpStatusCode.Created);

        var result = await new SupabaseDatabaseProvider(Http(stub)).SetupAsync(new DatabaseAuth("sbp_x", "acme"), Request(DatabaseSetupMode.Create), Ct);

        Assert.Equal(("abcd", "todo", true), (result.ResourceId, result.Name, result.PasswordSet));
        using var body = JsonDocument.Parse(Assert.Single(stub.Where("POST", "projects")).Body);
        Assert.Equal(("acme", "pw-123", "specific"), (
            body.RootElement.GetProperty("organization_slug").GetString(),
            body.RootElement.GetProperty("db_pass").GetString(),
            body.RootElement.GetProperty("region_selection").GetProperty("type").GetString()));
        Assert.False(body.RootElement.TryGetProperty("region", out _));
        Assert.False(body.RootElement.TryGetProperty("plan", out _));
    }

    [Fact]
    public async Task FR152_Supabase_free_project_limit_is_a_plan_limit()
    {
        var stub = new PlatformStub()
            .On("GET", "organizations/acme/projects", "[]")
            .On("POST", "projects", """{"message":"The following organization members have reached their maximum limits for the number of active free projects"}""", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<DatabaseProviderException>(() => new SupabaseDatabaseProvider(Http(stub)).SetupAsync(new DatabaseAuth("sbp_x", "acme"), Request(DatabaseSetupMode.Create), Ct));

        Assert.Equal(DeploymentFailure.PlanLimit, ex.Failure);
    }

    [Fact]
    public async Task FR152_Linking_a_Supabase_project_resets_the_password_AIDE_doesnt_hold()
    {
        var stub = new PlatformStub()
            .On("GET", "projects/abcd", """{"ref":"abcd","name":"legacy","region":"us-east-1"}""")
            .On("PATCH", "projects/abcd/database/password", "{}");

        var result = await new SupabaseDatabaseProvider(Http(stub)).SetupAsync(new DatabaseAuth("sbp_x", "acme"), Request(DatabaseSetupMode.Link, link: "abcd"), Ct);

        Assert.True(result.PasswordSet);
        Assert.Contains("pw-123", Assert.Single(stub.Where("PATCH", "projects/abcd/database/password")).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR152_FR153_Supabase_poll_waits_for_health_then_reads_keys_creating_missing_ones_and_the_pooler()
    {
        var keysRead = 0;
        var stub = new PlatformStub()
            .On("GET", "projects/abcd/health", """[{"name":"db","status":"ACTIVE_HEALTHY"},{"name":"pooler","status":"ACTIVE_HEALTHY"},{"name":"auth","healthy":true},{"name":"rest","status":"ACTIVE_HEALTHY"}]""")
            .On("GET", "projects/abcd/api-keys", _ => PlatformStub.Json(keysRead++ == 0
                ? """[{"type":"legacy","name":"anon","api_key":"eyJ.anon"},{"type":"publishable","name":"default","api_key":"sb_publishable_1"}]"""
                : """[{"type":"publishable","name":"default","api_key":"sb_publishable_1"},{"type":"secret","name":"aide","api_key":"sb_secret_1"}]"""))
            .On("POST", "projects/abcd/api-keys", "{}", HttpStatusCode.Created)
            .On("GET", "projects/abcd/config/database/pooler", """[{"database_type":"PRIMARY","db_host":"aws-0-eu-west-2.pooler.supabase.com","db_user":"postgres.abcd","db_name":"postgres","db_port":6543}]""")
            .On("GET", "projects/abcd", """{"ref":"abcd","status":"ACTIVE_HEALTHY","region":"eu-west-2"}""");

        var poll = await new SupabaseDatabaseProvider(Http(stub)).PollAsync(new DatabaseAuth("sbp_x", "acme"), Ref("abcd"), Ct);

        Assert.Equal((ManagedDatabaseStatus.Available, true), (poll.Status, poll.Terminal));
        Assert.Equal(
            ("abcd", "aws-0-eu-west-2.pooler.supabase.com", "postgres.abcd", "sb_publishable_1", "sb_secret_1"),
            (poll.Connection!.SupabaseRef, poll.Connection.PoolerHost, poll.Connection.PoolerUser, poll.Connection.PublishableKey, poll.Connection.SecretKey));
        Assert.Contains("\"secret\"", Assert.Single(stub.Where("POST", "projects/abcd/api-keys")).Body, StringComparison.Ordinal);
        Assert.Contains(stub.Calls, c => c.Path == "projects/abcd/api-keys?reveal=true");
    }

    [Fact]
    public async Task FR152_A_Supabase_project_coming_up_or_with_an_unhealthy_service_keeps_polling_and_INACTIVE_is_paused()
    {
        var coming = new PlatformStub().On("GET", "projects/abcd", """{"status":"COMING_UP"}""");
        var unhealthy = new PlatformStub()
            .On("GET", "projects/abcd/health", """[{"name":"db","status":"ACTIVE_HEALTHY"},{"name":"pooler","status":"COMING_UP"}]""")
            .On("GET", "projects/abcd", """{"status":"ACTIVE_HEALTHY"}""");
        var paused = new PlatformStub().On("GET", "projects/abcd", """{"status":"INACTIVE"}""");
        var auth = new DatabaseAuth("sbp_x", "acme");

        Assert.False((await new SupabaseDatabaseProvider(Http(coming)).PollAsync(auth, Ref("abcd"), Ct)).Terminal);
        Assert.Equal(ManagedDatabaseStatus.Provisioning, (await new SupabaseDatabaseProvider(Http(unhealthy)).PollAsync(auth, Ref("abcd"), Ct)).Status);
        Assert.Equal(ManagedDatabaseStatus.Paused, (await new SupabaseDatabaseProvider(Http(paused)).PollAsync(auth, Ref("abcd"), Ct)).Status);
    }

    [Fact]
    public async Task FR153_Missing_API_Key_Secrets_permission_is_forbidden_with_the_permission_name()
    {
        var stub = new PlatformStub()
            .On("GET", "projects/abcd/health", """[{"name":"db","status":"ACTIVE_HEALTHY"},{"name":"pooler","status":"ACTIVE_HEALTHY"},{"name":"auth","status":"ACTIVE_HEALTHY"},{"name":"rest","status":"ACTIVE_HEALTHY"}]""")
            .On("GET", "projects/abcd/api-keys", """{"message":"Forbidden","missing_permissions":["API Key Secrets: Read"]}""", HttpStatusCode.Forbidden)
            .On("GET", "projects/abcd", """{"status":"ACTIVE_HEALTHY"}""");

        var ex = await Assert.ThrowsAsync<DatabaseProviderException>(() => new SupabaseDatabaseProvider(Http(stub)).PollAsync(new DatabaseAuth("sbp_x", "acme"), Ref("abcd"), Ct));

        Assert.Equal(DeploymentFailure.Forbidden, ex.Failure);
        Assert.Equal(["API Key Secrets: Read"], ex.MissingPermissions);
    }

    [Fact]
    public async Task FR153_Auth_URLs_restore_and_advisors_use_the_Management_API()
    {
        var stub = new PlatformStub()
            .On("PATCH", "projects/abcd/config/auth", "{}")
            .On("POST", "projects/abcd/restore", "{}")
            .On("GET", "projects/abcd/advisors/security", """{"lints":[{"name":"rls_disabled_in_public","title":"RLS Disabled in Public","level":"ERROR","detail":"Table public.orders","remediation":"https://supabase.com/docs"}]}""");
        var provider = new SupabaseDatabaseProvider(Http(stub));
        var auth = new DatabaseAuth("sbp_x", "acme");

        await provider.ApplyAuthUrlsAsync(auth, "abcd", "https://todo.app", "https://todo.app/**,http://localhost:5173/**", Ct);
        await provider.RestoreAsync(auth, "abcd", Ct);
        var findings = await provider.SecurityAdvisorsAsync(auth, "abcd", Ct);

        using var body = JsonDocument.Parse(Assert.Single(stub.Where("PATCH", "projects/abcd/config/auth")).Body);
        Assert.Equal(("https://todo.app", "https://todo.app/**,http://localhost:5173/**"), (body.RootElement.GetProperty("site_url").GetString(), body.RootElement.GetProperty("uri_allow_list").GetString()));
        Assert.Single(stub.Where("POST", "projects/abcd/restore"));
        Assert.Equal(("rls_disabled_in_public", "ERROR"), (Assert.Single(findings).Name, findings[0].Level));
    }

    [Fact]
    public async Task FR152_Supabase_429_is_rate_limited_until_X_RateLimit_Reset()
    {
        var stub = new PlatformStub().On("GET", "projects/abcd", _ =>
        {
            var response = PlatformStub.Json("""{"message":"Too many requests"}""", HttpStatusCode.TooManyRequests);
            response.Headers.Add("X-RateLimit-Reset", "30");
            return response;
        });

        var ex = await Assert.ThrowsAsync<DatabaseProviderException>(() => new SupabaseDatabaseProvider(Http(stub)).PollAsync(new DatabaseAuth("sbp_x", "acme"), Ref("abcd"), Ct));

        Assert.Equal((DeploymentFailure.RateLimited, Now.AddSeconds(30)), (ex.Failure, ex.RetryAt));
    }

    // ------------------------------------------------------------------ Render

    [Fact]
    public async Task FR152_Render_create_posts_version_17_in_the_backends_region_with_a_database_name()
    {
        var stub = new PlatformStub()
            .On("GET", "postgres?", "[]")
            .On("GET", "services/srv-api", """{"id":"srv-api","serviceDetails":{"region":"frankfurt"}}""")
            .On("POST", "postgres", """{"id":"dpg-1","name":"todo-preview","region":"frankfurt","plan":"free"}""", HttpStatusCode.Created);

        var result = await new RenderPostgresProvider(Http(stub), new ManualTime(Now)).SetupAsync(
            new DatabaseAuth("rnd_x", "tea-1"), Request(DatabaseSetupMode.Create, "todo-preview", region: null, host: new DatabaseHostContext("srv-api", null, null)), Ct);

        Assert.Equal(("dpg-1", "frankfurt"), (result.ResourceId, result.Region));
        using var body = JsonDocument.Parse(Assert.Single(stub.Where("POST", "postgres")).Body);
        Assert.Equal(("todo-preview", "tea-1", "17", "frankfurt", "todo_preview", "free"), (
            body.RootElement.GetProperty("name").GetString(),
            body.RootElement.GetProperty("ownerId").GetString(),
            body.RootElement.GetProperty("version").GetString(),
            body.RootElement.GetProperty("region").GetString(),
            body.RootElement.GetProperty("databaseName").GetString(),
            body.RootElement.GetProperty("plan").GetString()));
    }

    [Fact]
    public async Task FR152_Render_create_adopts_an_instance_of_the_same_name()
    {
        var stub = new PlatformStub().On("GET", "postgres?", """[{"postgres":{"id":"dpg-old","name":"todo"},"cursor":"c"}]""");

        var result = await new RenderPostgresProvider(Http(stub), new ManualTime(Now)).SetupAsync(new DatabaseAuth("rnd_x", "tea-1"), Request(DatabaseSetupMode.Create), Ct);

        Assert.Equal("dpg-old", result.ResourceId);
        Assert.Empty(stub.Where("POST", "postgres"));
    }

    [Fact]
    public async Task FR152_Render_available_gives_the_internal_URL_for_a_Render_backend_and_expiring_near_expiresAt()
    {
        var stub = new PlatformStub()
            .On("GET", "postgres/dpg-1/connection-info", """{"internalConnectionString":"postgresql://u:p@dpg-1-a/todo","externalConnectionString":"postgresql://u:p@dpg-1-a.frankfurt-postgres.render.com/todo"}""")
            .On("GET", "postgres/dpg-1", $$"""{"id":"dpg-1","name":"todo","status":"available","plan":"free","region":"frankfurt","expiresAt":"{{Now.AddDays(3):O}}"}""");
        var provider = new RenderPostgresProvider(Http(stub), new ManualTime(Now));
        var auth = new DatabaseAuth("rnd_x", "tea-1");

        var onRender = await provider.PollAsync(auth, Ref("dpg-1", new DatabaseHostContext("srv-api", null, null)), Ct);
        var elsewhere = await provider.PollAsync(auth, Ref("dpg-1"), Ct);

        Assert.Equal((ManagedDatabaseStatus.Expiring, "postgresql://u:p@dpg-1-a/todo"), (onRender.Status, onRender.Connection!.Url));
        Assert.Equal("postgresql://u:p@dpg-1-a.frankfurt-postgres.render.com/todo", elsewhere.Connection!.Url);
        Assert.Equal(Now.AddDays(3), onRender.ExpiresAt);
    }

    // ------------------------------------------------------------------ Railway

    [Fact]
    public async Task FR152_Railway_create_adds_a_Postgres_service_with_a_volume_in_the_apps_project_and_deploys_it()
    {
        var stub = new PlatformStub().On("POST", "/graphql/v2", call => PlatformStub.Json(
            call.Body.Contains("serviceCreate", StringComparison.Ordinal) ? """{"data":{"serviceCreate":{"id":"svc-pg","name":"Postgres"}}}"""
            : call.Body.Contains("volumes", StringComparison.Ordinal) ? """{"data":{"project":{"volumes":{"edges":[]}}}}"""
            : call.Body.Contains("services", StringComparison.Ordinal) ? """{"data":{"project":{"id":"p1","services":{"edges":[{"node":{"id":"svc-api","name":"todo-api"}}]}}}}"""
            : """{"data":{}}"""));
        var host = new DatabaseHostContext(null, "p1:svc-api:e-prod", "e-prev");

        var result = await new RailwayPostgresProvider(Http(stub)).SetupAsync(new DatabaseAuth("rw_x", "ws-1"), Request(DatabaseSetupMode.Create, host: host), Ct);

        Assert.Equal(("p1:svc-pg:e-prod", true), (result.ResourceId, result.PasswordSet));
        var bodies = stub.Where("POST", "/graphql/v2").Select(c => c.Body).ToList();
        var create = Assert.Single(bodies, b => b.Contains("serviceCreate", StringComparison.Ordinal));
        Assert.Contains(RailwayPostgresProvider.Image, create, StringComparison.Ordinal);
        Assert.Contains("pw-123", create, StringComparison.Ordinal);
        Assert.Contains(bodies, b => b.Contains("volumeCreate", StringComparison.Ordinal) && b.Contains(RailwayPostgresProvider.MountPath, StringComparison.Ordinal));
        Assert.Contains(bodies, b => b.Contains("serviceInstanceDeployV2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR152_Railway_Postgres_needs_the_apps_Railway_target_first()
    {
        var ex = await Assert.ThrowsAsync<DatabaseProviderException>(() =>
            new RailwayPostgresProvider(Http(new PlatformStub())).SetupAsync(new DatabaseAuth("rw_x", "ws-1"), Request(DatabaseSetupMode.Create), Ct));

        Assert.Equal(DeploymentFailure.Rejected, ex.Failure);
    }

    // ------------------------------------------------------------------ Fake

    [Fact]
    public async Task FR152_The_Fake_is_available_on_its_second_poll_with_the_hosts_connection_shape()
    {
        var fake = new FakeDatabaseProvider(ManagedDatabaseProvider.Supabase, new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal));
        var auth = new DatabaseAuth("fake", null);
        var setup = await fake.SetupAsync(auth, Request(DatabaseSetupMode.Create), Ct);
        var database = Ref(setup.ResourceId!);

        Assert.Equal(ManagedDatabaseStatus.Provisioning, (await fake.PollAsync(auth, database, Ct)).Status);
        var second = await fake.PollAsync(auth, database, Ct);
        Assert.Equal(ManagedDatabaseStatus.Available, second.Status);
        Assert.StartsWith("sb_publishable_", second.Connection!.PublishableKey, StringComparison.Ordinal);
    }
}
