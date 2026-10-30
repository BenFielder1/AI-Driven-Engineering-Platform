using System.Net;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Platforms;
using static Aide.UnitTests.Deployments.PlatformFixtures;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-144, FR-145, FR-147, FR-149, FR-150, §4.20: the Cloudflare provider against a stubbed API (no network).</summary>
public class CloudflareDeploymentProviderTests
{
    private const string A = "accounts/acc_1";

    private static readonly DeploymentAuth Auth = new("cf_token", null, "acc_1");

    private static (CloudflareDeploymentProvider Provider, PlatformStub Stub) Create()
    {
        var stub = new PlatformStub();
        var http = new PlatformHttp(new PlatformStubFactory(stub), new ManualTime(Now));
        return (new CloudflareDeploymentProvider(http), stub);
    }

    private static string R(string result) => $$"""{"success":true,"result":{{result}}}""";

    [Fact]
    public async Task FR144_Create_bootstraps_the_Worker_connects_the_repo_and_creates_both_triggers()
    {
        var (provider, stub) = Create();
        var scripts = 0;
        stub.On("GET", $"{A}/workers/scripts", _ => PlatformStub.Json(scripts++ == 0 ? R("[]") : R("""[{"id":"todo-web","tag":"tag1"}]""")))
            .On("PUT", $"{A}/workers/scripts/todo-web", R("""{"id":"todo-web"}"""))
            .On("PUT", $"{A}/builds/repos/connections", R("""{"repo_connection_uuid":"rc1"}"""))
            .On("GET", "user/tokens/verify", R("""{"id":"tok1","status":"active"}"""))
            .On("GET", $"{A}/builds/tokens", R("[]"))
            .On("POST", $"{A}/builds/tokens", R("""{"build_token_uuid":"bt1"}"""))
            .On("GET", $"{A}/builds/workers/tag1/triggers", R("[]"))
            .On("POST", $"{A}/builds/triggers", c => PlatformStub.Json(R(c.Body.Contains("versions upload", StringComparison.Ordinal) ? """{"trigger_uuid":"prev1"}""" : """{"trigger_uuid":"prod1"}""")))
            .On("GET", $"{A}/workers/subdomain", R("""{"subdomain":"acme"}"""));

        var result = await provider.SetupTargetAsync(
            Auth, new DeploymentSetupRequest(Target(DeploymentProvider.Cloudflare, null), true, "todo-web", null, null, null), TestContext.Current.CancellationToken);

        Assert.Equal("todo-web", result.ResourceId);
        Assert.Equal("prev1|prod1", result.PreviewResourceId);
        Assert.Equal("https://todo-web.acme.workers.dev", result.ProductionUrl);
        Assert.Single(stub.Where("PUT", $"{A}/workers/scripts/todo-web"));
        var triggers = stub.Where("POST", $"{A}/builds/triggers").Select(c => c.Body).ToList();
        Assert.Equal(2, triggers.Count);
        Assert.Contains(triggers, b => b.Contains("\"branch_includes\":[\"main\"]", StringComparison.Ordinal) && b.Contains("npx wrangler deploy", StringComparison.Ordinal));
        Assert.Contains(triggers, b => b.Contains("\"branch_excludes\":[\"aide/*\"]", StringComparison.Ordinal) && b.Contains("\"build_token_uuid\":\"bt1\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR144_A_repo_the_Cloudflare_Git_app_cannot_see_is_PLATFORM_GIT_NOT_CONNECTED()
    {
        var (provider, stub) = Create();
        stub.On("GET", $"{A}/workers/scripts", R("""[{"id":"todo-web","tag":"tag1"}]"""))
            .On("PUT", $"{A}/builds/repos/connections", """{"success":false,"errors":[{"code":12000,"message":"GitHub app not installed"}]}""", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.SetupTargetAsync(
            Auth, new DeploymentSetupRequest(Target(DeploymentProvider.Cloudflare, null), true, "todo-web", null, null, null), TestContext.Current.CancellationToken));

        Assert.Equal(DeploymentFailure.GitNotConnected, ex.Failure);
        Assert.Equal(DeploymentPlatforms.GitConnectUrl(DeploymentProvider.Cloudflare, "github"), ex.InstallUrl);
    }

    [Fact]
    public async Task FR144_Creating_a_Pages_project_is_refused()
    {
        var (provider, _) = Create();

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.SetupTargetAsync(
            Auth, new DeploymentSetupRequest(Target(DeploymentProvider.Cloudflare, null), true, "pages:site", null, null, null), TestContext.Current.CancellationToken));

        Assert.Equal(DeploymentFailure.Rejected, ex.Failure);
    }

    [Fact]
    public async Task FR144_Linking_a_Pages_project_sets_custom_previews_excluding_aide_branches()
    {
        var (provider, stub) = Create();
        stub.On("GET", $"{A}/workers/scripts", R("[]"))
            .On("GET", $"{A}/pages/projects/site", R("""{"name":"site","source":{"config":{"owner":"acme","repo_name":"todo"}}}"""))
            .On("PATCH", $"{A}/pages/projects/site", R("""{"name":"site","subdomain":"site.pages.dev"}"""));

        var result = await provider.SetupTargetAsync(
            Auth, new DeploymentSetupRequest(Target(DeploymentProvider.Cloudflare, null), false, null, null, null, "site"), TestContext.Current.CancellationToken);

        Assert.Equal("pages:site", result.ResourceId);
        Assert.Equal("https://site.pages.dev", result.ProductionUrl);
        var patch = Assert.Single(stub.Where("PATCH", $"{A}/pages/projects/site"));
        Assert.Contains("\"preview_deployment_setting\":\"custom\"", patch.Body, StringComparison.Ordinal);
        Assert.Contains("\"preview_branch_excludes\":[\"aide/*\"]", patch.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR147_A_Workers_preview_is_SHA_pinned_on_the_preview_trigger_and_reads_its_preview_url()
    {
        var (provider, stub) = Create();
        stub.On("POST", $"{A}/builds/triggers/prev1/builds", R("""{"build_uuid":"b1"}"""))
            .On("GET", $"{A}/builds/builds/b1", R("""{"build_uuid":"b1","status":"stopped","build_outcome":"success","preview_url":"https://abc-todo.acme.workers.dev"}"""));
        var target = Target(DeploymentProvider.Cloudflare, "todo-web", "prev1|prod1");

        var created = await provider.DeployAsync(Auth, new DeployRequest(Guid.CreateVersion7(), target, DeploymentEnvironment.Preview, "aide/v1", "abc123"), TestContext.Current.CancellationToken);
        var polled = await provider.GetDeploymentAsync(Auth, target, created.Id, TestContext.Current.CancellationToken);

        var start = Assert.Single(stub.Where("POST", $"{A}/builds/triggers/prev1/builds"));
        Assert.Contains("\"commit_hash\":\"abc123\"", start.Body, StringComparison.Ordinal);
        Assert.Contains("\"branch\":\"aide/v1\"", start.Body, StringComparison.Ordinal);
        Assert.Equal(DeploymentStatus.Ready, polled.Status);
        Assert.Equal("https://abc-todo.acme.workers.dev", polled.Url);
    }

    [Fact]
    public async Task FR149_Production_adopts_the_production_trigger_build_of_the_merge_commit()
    {
        var (provider, stub) = Create();
        stub.On("GET", $"{A}/workers/scripts", R("""[{"id":"todo-web","tag":"tag1"}]"""))
            .On("GET", $"{A}/builds/workers/tag1/builds", R("""[{"build_uuid":"bp","trigger":{"trigger_uuid":"prev1"},"build_trigger_metadata":{"commit_hash":"m1"}},{"build_uuid":"b9","status":"running","trigger":{"trigger_uuid":"prod1"},"build_trigger_metadata":{"commit_hash":"m1"}}]"""));

        var adopted = await provider.FindDeploymentForShaAsync(
            Auth, Target(DeploymentProvider.Cloudflare, "todo-web", "prev1|prod1"), DeploymentEnvironment.Production, "m1", null, null, TestContext.Current.CancellationToken);

        Assert.Equal("b9", adopted!.Id);
        Assert.Equal(DeploymentStatus.Building, adopted.Status);
    }

    [Fact]
    public async Task FR150_Workers_rollback_deploys_the_builds_version_at_100_percent()
    {
        var (provider, stub) = Create();
        stub.On("GET", $"{A}/builds/builds/b1", R("""{"build_uuid":"b1","build_trigger_metadata":{"commit_hash":"abc1234ff"}}"""))
            .On("GET", $"{A}/workers/scripts/todo-web/versions", R("""{"items":[{"id":"v2","annotations":{"workers/message":"zzz"}},{"id":"v1","annotations":{"workers/message":"abc1234 fix"}}]}"""))
            .On("POST", $"{A}/workers/scripts/todo-web/deployments", R("{}"));

        await provider.RollbackAsync(Auth, Target(DeploymentProvider.Cloudflare, "todo-web", "prev1|prod1"), "b1", TestContext.Current.CancellationToken);

        var deploy = Assert.Single(stub.Where("POST", $"{A}/workers/scripts/todo-web/deployments"));
        Assert.Contains("\"strategy\":\"percentage\"", deploy.Body, StringComparison.Ordinal);
        Assert.Contains("\"version_id\":\"v1\"", deploy.Body, StringComparison.Ordinal);
        Assert.Contains("\"percentage\":100", deploy.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR150_Pages_rollback_calls_the_deployment_rollback()
    {
        var (provider, stub) = Create();
        stub.On("POST", $"{A}/pages/projects/site/deployments/d1/rollback", R("{}"));

        await provider.RollbackAsync(Auth, Target(DeploymentProvider.Cloudflare, "pages:site"), "d1", TestContext.Current.CancellationToken);

        Assert.Single(stub.Where("POST", $"{A}/pages/projects/site/deployments/d1/rollback"));
    }

    [Fact]
    public async Task FR145_Workers_env_sync_writes_build_variables_per_trigger_and_production_secrets_as_runtime_secrets()
    {
        var (provider, stub) = Create();
        stub.On("PATCH", $"{A}/builds/triggers/", R("{}")).On("PUT", $"{A}/workers/scripts/todo-web/secrets", R("{}"));
        var writes = new[]
        {
            new DeploymentEnvWrite("DB_KEY", "s3cret", DeploymentEnvironment.Production, true, null),
            new DeploymentEnvWrite("DB_KEY", "s3cret", DeploymentEnvironment.Preview, true, null),
            new DeploymentEnvWrite("API_ORIGIN", "https://p", DeploymentEnvironment.Preview, false, null),
        };

        var written = await provider.SyncEnvAsync(
            Auth, Target(DeploymentProvider.Cloudflare, "todo-web", "prev1|prod1"), new DeploymentEnvSyncRequest(writes, []), TestContext.Current.CancellationToken);

        var prod = Assert.Single(stub.Where("PATCH", $"{A}/builds/triggers/prod1/environment_variables"));
        Assert.Contains("\"is_secret\":true", prod.Body, StringComparison.Ordinal);
        var preview = Assert.Single(stub.Where("PATCH", $"{A}/builds/triggers/prev1/environment_variables"));
        Assert.Contains("API_ORIGIN", preview.Body, StringComparison.Ordinal);
        var secret = Assert.Single(stub.Where("PUT", $"{A}/workers/scripts/todo-web/secrets"));
        Assert.Contains("\"type\":\"secret_text\"", secret.Body, StringComparison.Ordinal);
        Assert.Equal(3, written.Count);
    }

    [Fact]
    public async Task FR147_A_Pages_preview_posts_the_branch_and_re_triggers_a_build_of_another_commit_once()
    {
        var (provider, stub) = Create();
        var posts = 0;
        stub.On("POST", $"{A}/pages/projects/site/deployments", _ => PlatformStub.Json(R(posts++ == 0 ? """{"id":"d1"}""" : """{"id":"d2"}""")))
            .On("GET", $"{A}/pages/projects/site/deployments/d1", R("""{"id":"d1","deployment_trigger":{"metadata":{"commit_hash":"fff0000"}},"latest_stage":{"name":"build","status":"active"}}"""))
            .On("GET", $"{A}/pages/projects/site/deployments/d2", R("""{"id":"d2","url":"https://d2.site.pages.dev","deployment_trigger":{"metadata":{"commit_hash":"abc123"}},"latest_stage":{"name":"deploy","status":"success"}}"""));
        var target = Target(DeploymentProvider.Cloudflare, "pages:site");

        var created = await provider.DeployAsync(Auth, new DeployRequest(Guid.CreateVersion7(), target, DeploymentEnvironment.Preview, "aide/v1", "abc123"), TestContext.Current.CancellationToken);
        var retried = await provider.GetDeploymentAsync(Auth, target, created.Id, TestContext.Current.CancellationToken);
        var ready = await provider.GetDeploymentAsync(Auth, target, retried.Id, TestContext.Current.CancellationToken);

        Assert.Contains("aide/v1", stub.Where("POST", $"{A}/pages/projects/site/deployments").First().Body, StringComparison.Ordinal);
        Assert.Equal("d2|abc123|2|aide/v1", retried.Id);
        Assert.Equal(DeploymentStatus.Ready, ready.Status);
        Assert.Equal("https://d2.site.pages.dev", ready.Url);
    }

    [Fact]
    public async Task FR149_Pages_production_is_adopt_only_and_waits_while_no_build_of_the_merge_commit_exists()
    {
        var (provider, stub) = Create();
        stub.On("GET", $"{A}/pages/projects/site/deployments?env=production", R("[]"));

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.DeployAsync(
            Auth, new DeployRequest(Guid.CreateVersion7(), Target(DeploymentProvider.Cloudflare, "pages:site"), DeploymentEnvironment.Production, "main", "m1"), TestContext.Current.CancellationToken));

        Assert.Equal(DeploymentFailure.Upstream, ex.Failure);
        Assert.Empty(stub.Where("POST", $"{A}/pages/projects/site/deployments"));
    }

    [Fact]
    public async Task FR151_A_ready_Workers_production_build_reports_the_workers_dev_URL_as_its_alias()
    {
        var (provider, stub) = Create();
        stub.On("GET", $"{A}/builds/builds/b9", R("""{"build_uuid":"b9","status":"stopped","build_outcome":"success","trigger":{"trigger_uuid":"prod1"}}"""))
            .On("GET", $"{A}/workers/subdomain", R("""{"subdomain":"acme"}"""));

        var ready = await provider.GetDeploymentAsync(Auth, Target(DeploymentProvider.Cloudflare, "todo-web", "prev1|prod1"), "b9", TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentStatus.Ready, ready.Status);
        Assert.Equal("https://todo-web.acme.workers.dev", ready.Alias);
    }

    [Fact]
    public async Task FR149_A_redeploy_never_adopts_an_earlier_Workers_build_of_the_commit()
    {
        var (provider, stub) = Create();
        stub.On("GET", $"{A}/workers/scripts", R("""[{"id":"todo-web","tag":"tag1"}]"""))
            .On("GET", $"{A}/builds/workers/tag1/builds", R("""[{"build_uuid":"old","status":"stopped","build_outcome":"fail","created_on":"2026-10-30T11:00:00Z","trigger":{"trigger_uuid":"prod1"},"build_trigger_metadata":{"commit_hash":"m1"}}]"""));
        var target = Target(DeploymentProvider.Cloudflare, "todo-web", "prev1|prod1");

        var redeploy = await provider.FindDeploymentForShaAsync(
            Auth, target, DeploymentEnvironment.Production, "m1", Guid.CreateVersion7(), Now, TestContext.Current.CancellationToken);
        var auto = await provider.FindDeploymentForShaAsync(Auth, target, DeploymentEnvironment.Production, "m1", null, null, TestContext.Current.CancellationToken);

        Assert.Null(redeploy);
        Assert.Equal("old", auto!.Id);
    }

    [Fact]
    public async Task FR149_A_Workers_build_made_after_the_redeploy_row_is_adopted()
    {
        var (provider, stub) = Create();
        stub.On("GET", $"{A}/workers/scripts", R("""[{"id":"todo-web","tag":"tag1"}]"""))
            .On("GET", $"{A}/builds/workers/tag1/builds", R("""[{"build_uuid":"new","status":"running","created_on":"2026-10-30T12:00:05Z","trigger":{"trigger_uuid":"prod1"},"build_trigger_metadata":{"commit_hash":"m1"}},{"build_uuid":"old","status":"running","created_on":"2026-10-30T11:00:00Z","trigger":{"trigger_uuid":"prod1"},"build_trigger_metadata":{"commit_hash":"m1"}}]"""));

        var adopted = await provider.FindDeploymentForShaAsync(
            Auth, Target(DeploymentProvider.Cloudflare, "todo-web", "prev1|prod1"), DeploymentEnvironment.Production, "m1", Guid.CreateVersion7(), Now, TestContext.Current.CancellationToken);

        Assert.Equal("new", adopted!.Id);
    }

    private const string PagesProduction = """[{"id":"p1","environment":"production","url":"https://p1hash.site.pages.dev","created_on":"2026-10-30T11:00:00Z","latest_stage":{"name":"deploy","status":"success"},"deployment_trigger":{"metadata":{"commit_hash":"m1234567"}}}]""";

    [Fact]
    public async Task FR151_Pages_production_reports_the_project_pages_dev_URL_as_its_alias()
    {
        var (provider, stub) = Create();
        stub.On("GET", $"{A}/pages/projects/site/deployments?env=production", R(PagesProduction));

        var adopted = await provider.DeployAsync(
            Auth, new DeployRequest(Guid.CreateVersion7(), Target(DeploymentProvider.Cloudflare, "pages:site"), DeploymentEnvironment.Production, "main", "m1234567"), TestContext.Current.CancellationToken);

        Assert.Equal("p1", adopted.Id);
        Assert.Equal("https://p1hash.site.pages.dev", adopted.Url);
        Assert.Equal("https://site.pages.dev", adopted.Alias);
    }

    [Fact]
    public async Task FR149_A_Pages_production_redeploy_retries_the_earlier_build_instead_of_adopting_it()
    {
        var (provider, stub) = Create();
        stub.On("GET", $"{A}/pages/projects/site/deployments?env=production", R(PagesProduction))
            .On("POST", $"{A}/pages/projects/site/deployments/p1/retry", R("""{"id":"p2","environment":"production","latest_stage":{"name":"queued","status":"active"}}"""));
        var target = Target(DeploymentProvider.Cloudflare, "pages:site");

        var earlier = await provider.FindDeploymentForShaAsync(
            Auth, target, DeploymentEnvironment.Production, "m1234567", Guid.CreateVersion7(), Now, TestContext.Current.CancellationToken);
        var rebuilt = await provider.DeployAsync(
            Auth, new DeployRequest(Guid.CreateVersion7(), target, DeploymentEnvironment.Production, "main", "m1234567", Rebuild: true), TestContext.Current.CancellationToken);

        Assert.Null(earlier);
        Assert.Equal("p2", rebuilt.Id);
        Assert.Single(stub.Where("POST", $"{A}/pages/projects/site/deployments/p1/retry"));
    }
}
