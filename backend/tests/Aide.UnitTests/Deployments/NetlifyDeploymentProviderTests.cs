using System.Net;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Platforms;
using static Aide.UnitTests.Deployments.PlatformFixtures;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-144, FR-145, FR-149, FR-150, §4.20: the Netlify provider against a stubbed Netlify API (no network).</summary>
public class NetlifyDeploymentProviderTests
{
    private static readonly DeploymentAuth Auth = new("nfp_token", null, "acc_1", "acme-team");

    private static (NetlifyDeploymentProvider Provider, PlatformStub Stub, ManualTime Time) Create()
    {
        var stub = new PlatformStub();
        var time = new ManualTime(Now);
        var http = new PlatformHttp(new PlatformStubFactory(stub), time);
        return (new NetlifyDeploymentProvider(http, new NetlifyDeployRateLimiter(time)), stub, time);
    }

    [Fact]
    public async Task FR144_Create_uses_the_GitHub_installation_of_another_site_of_the_owner_and_sets_branch_control()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "acme-team/sites", """[{"id":"s0","name":"other","build_settings":{"provider":"github","repo_path":"acme/blog","installation_id":"777"}}]""")
            .On("POST", "acme-team/sites", """{"id":"s1","name":"todo-web"}""")
            .On("PATCH", "sites/s1", """{"id":"s1","name":"todo-web","ssl_url":"https://todo-web.netlify.app"}""");

        var result = await provider.SetupTargetAsync(
            Auth, new DeploymentSetupRequest(Target(DeploymentProvider.Netlify, null), true, "todo-web", null, null, null), TestContext.Current.CancellationToken);

        Assert.Equal("s1", result.ResourceId);
        Assert.Equal("https://todo-web.netlify.app", result.ProductionUrl);
        var create = Assert.Single(stub.Where("POST", "acme-team/sites"));
        Assert.Contains("\"installation_id\":777", create.Body, StringComparison.Ordinal);
        Assert.Contains("\"repo_path\":\"acme/todo\"", create.Body, StringComparison.Ordinal);
        Assert.Contains("\"allowed_branches\":[\"main\"]", create.Body, StringComparison.Ordinal);
        var patch = Assert.Single(stub.Where("PATCH", "sites/s1"));
        Assert.Contains("\"skip_prs\":true", patch.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR144_Create_without_a_Netlify_GitHub_installation_is_PLATFORM_GIT_NOT_CONNECTED_with_the_install_link()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "acme-team/sites", "[]");

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.SetupTargetAsync(
            Auth, new DeploymentSetupRequest(Target(DeploymentProvider.Netlify, null), true, "todo-web", null, null, null), TestContext.Current.CancellationToken));

        Assert.Equal(DeploymentFailure.GitNotConnected, ex.Failure);
        Assert.Equal(NetlifyRules.GitHubInstallUrl, ex.InstallUrl);
        Assert.Empty(stub.Where("POST", "acme-team/sites"));
    }

    [Fact]
    public async Task FR144_A_plan_refusal_on_create_is_PLATFORM_PLAN_LIMIT()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "acme-team/sites", """[{"id":"s0","name":"x","build_settings":{"provider":"github","repo_path":"acme/x","installation_id":"7"}}]""")
            .On("POST", "acme-team/sites", """{"message":"Private repositories owned by an organization require the Pro plan"}""", HttpStatusCode.UnprocessableEntity);

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.SetupTargetAsync(
            Auth, new DeploymentSetupRequest(Target(DeploymentProvider.Netlify, null), true, "todo-web", null, null, null), TestContext.Current.CancellationToken));

        Assert.Equal(DeploymentFailure.PlanLimit, ex.Failure);
    }

    [Fact]
    public async Task FR144_GitLab_create_is_not_automated_yet()
    {
        var (provider, _, _) = Create();
        var gitlab = new DeploymentRepoRef("gitlab", "acme", "todo", null, "99", "acme/todo", "main");

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.SetupTargetAsync(
            Auth, new DeploymentSetupRequest(Target(DeploymentProvider.Netlify, null, repo: gitlab), true, "todo-web", null, null, null), TestContext.Current.CancellationToken));

        Assert.Equal(DeploymentFailure.NotSupported, ex.Failure);
    }

    [Fact]
    public async Task FR144_Link_refuses_a_site_of_another_repository()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "sites/s9", """{"id":"s9","build_settings":{"repo_path":"acme/other"}}""");

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.SetupTargetAsync(
            Auth, new DeploymentSetupRequest(Target(DeploymentProvider.Netlify, null), false, null, null, null, "s9"), TestContext.Current.CancellationToken));

        Assert.Equal(DeploymentFailure.Rejected, ex.Failure);
    }

    [Fact]
    public async Task FR147_A_preview_builds_the_version_branch_with_an_aide_title()
    {
        var (provider, stub, _) = Create();
        stub.On("POST", "sites/s1/builds", """{"id":"b1","deploy_id":"d1"}""");
        var id = Guid.CreateVersion7();

        var created = await provider.DeployAsync(
            Auth, new DeployRequest(id, Target(DeploymentProvider.Netlify, "s1"), DeploymentEnvironment.Preview, "aide/v0.2.0", "abc123"), TestContext.Current.CancellationToken);

        Assert.Equal("d1|abc123|1|aide/v0.2.0", created.Id);
        var call = Assert.Single(stub.Where("POST", "sites/s1/builds"));
        Assert.Contains("branch=aide/v0.2.0", call.Path, StringComparison.Ordinal);
        Assert.Contains($"title=aide:{id:D}", call.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR147_A_branch_build_of_another_commit_is_re_triggered_once_then_fails()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "deploys/d1", """{"id":"d1","state":"building","commit_ref":"fff000"}""")
            .On("GET", "deploys/d2", """{"id":"d2","state":"building","commit_ref":"fff000"}""")
            .On("POST", "sites/s1/builds", """{"deploy_id":"d2"}""");
        var target = Target(DeploymentProvider.Netlify, "s1");

        var retried = await provider.GetDeploymentAsync(Auth, target, "d1|abc123|1|aide/v1", TestContext.Current.CancellationToken);
        var failed = await provider.GetDeploymentAsync(Auth, target, retried.Id, TestContext.Current.CancellationToken);

        Assert.Equal("d2|abc123|2|aide/v1", retried.Id);
        Assert.Equal(DeploymentStatus.Queued, retried.Status);
        Assert.Equal(DeploymentStatus.Error, failed.Status);
        Assert.Single(stub.Where("POST", "sites/s1/builds"));
    }

    [Fact]
    public async Task FR149_Production_adopts_the_deploy_of_the_merge_commit()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "sites/s1/deploys?production=true", """[{"id":"d0","commit_ref":"000"},{"id":"d7","commit_ref":"merge123","state":"ready","ssl_url":"https://todo.netlify.app","deploy_ssl_url":"https://d7--todo.netlify.app"}]""");

        var adopted = await provider.FindDeploymentForShaAsync(
            Auth, Target(DeploymentProvider.Netlify, "s1"), DeploymentEnvironment.Production, "merge123", null, null, TestContext.Current.CancellationToken);

        Assert.Equal("d7", adopted!.Id);
        Assert.Equal(DeploymentStatus.Ready, adopted.Status);
        Assert.Equal("https://todo.netlify.app", adopted.Alias);
    }

    [Fact]
    public async Task FR150_Rollback_restores_the_deploy()
    {
        var (provider, stub, _) = Create();
        stub.On("POST", "sites/s1/deploys/d7/restore", """{"id":"d7","state":"ready"}""");

        await provider.RollbackAsync(Auth, Target(DeploymentProvider.Netlify, "s1"), "d7", TestContext.Current.CancellationToken);

        Assert.Single(stub.Where("POST", "sites/s1/deploys/d7/restore"));
    }

    [Fact]
    public async Task FR145_Env_sync_writes_production_and_both_preview_contexts_and_creates_secrets_with_is_secret()
    {
        var (provider, stub, _) = Create();
        stub.On("PATCH", "accounts/acc_1/env/API_KEY", "{}", HttpStatusCode.NotFound)
            .On("POST", "accounts/acc_1/env", "[]")
            .On("PATCH", "accounts/acc_1/env/API_ORIGIN", "{}");
        var writes = new[]
        {
            new DeploymentEnvWrite("API_KEY", "s3cret", DeploymentEnvironment.Preview, true, null),
            new DeploymentEnvWrite("API_ORIGIN", "https://api", DeploymentEnvironment.Production, false, null),
            new DeploymentEnvWrite("API_ORIGIN", "https://api-preview", DeploymentEnvironment.Preview, false, null),
        };

        var written = await provider.SyncEnvAsync(
            Auth, Target(DeploymentProvider.Netlify, "s1"), new DeploymentEnvSyncRequest(writes, []), TestContext.Current.CancellationToken);

        var create = Assert.Single(stub.Where("POST", "accounts/acc_1/env?site_id=s1"));
        Assert.Contains("\"is_secret\":true", create.Body, StringComparison.Ordinal);
        Assert.Contains("deploy-preview", create.Body, StringComparison.Ordinal);
        Assert.Contains("branch-deploy", create.Body, StringComparison.Ordinal);
        var contexts = stub.Where("PATCH", "accounts/acc_1/env/API_ORIGIN").Select(c => c.Body).ToList();
        Assert.Equal(3, contexts.Count);
        Assert.Contains(contexts, b => b.Contains("\"context\":\"production\"", StringComparison.Ordinal));
        Assert.Contains(contexts, b => b.Contains("\"context\":\"branch-deploy\"", StringComparison.Ordinal));
        Assert.Equal(3, written.Count);
        Assert.Contains(written, w => w.Id == "netlify:preview:API_KEY");
    }

    [Fact]
    public async Task FR149_Deploy_creation_is_held_back_before_calling_after_three_a_minute()
    {
        var (provider, stub, time) = Create();
        stub.On("POST", "sites/s1/builds", """{"deploy_id":"d"}""");
        var target = Target(DeploymentProvider.Netlify, "s1");
        for (var i = 0; i < 3; i++)
        {
            await provider.DeployAsync(Auth, new DeployRequest(Guid.CreateVersion7(), target, DeploymentEnvironment.Production, "main", "a"), TestContext.Current.CancellationToken);
        }

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.DeployAsync(
            Auth, new DeployRequest(Guid.CreateVersion7(), target, DeploymentEnvironment.Production, "main", "a"), TestContext.Current.CancellationToken));

        Assert.Equal(DeploymentFailure.RateLimited, ex.Failure);
        Assert.Equal(Now.AddMinutes(1), ex.RetryAt);
        Assert.Equal(3, stub.Where("POST", "sites/s1/builds").Count());
        time.Now = Now.AddMinutes(1).AddSeconds(1);
        await provider.DeployAsync(Auth, new DeployRequest(Guid.CreateVersion7(), target, DeploymentEnvironment.Production, "main", "a"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FR149_A_Netlify_429_blocks_the_account_until_Retry_After_without_another_call()
    {
        var (provider, stub, _) = Create();
        stub.On("POST", "sites/s1/builds", _ =>
        {
            var response = PlatformStub.Json("{\"message\":\"rate limited\"}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        var request = new DeployRequest(Guid.CreateVersion7(), Target(DeploymentProvider.Netlify, "s1"), DeploymentEnvironment.Production, "main", "a");

        var first = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.DeployAsync(Auth, request, TestContext.Current.CancellationToken));
        var second = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.DeployAsync(Auth, request, TestContext.Current.CancellationToken));

        Assert.Equal(DeploymentFailure.RateLimited, first.Failure);
        Assert.Equal(Now.AddSeconds(30), first.RetryAt);
        Assert.Equal(DeploymentFailure.RateLimited, second.Failure);
        Assert.Single(stub.Where("POST", "sites/s1/builds"));
    }

    [Fact]
    public async Task FR147_A_preview_build_an_ignore_rule_skipped_falls_back_to_the_build_hook_once()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "deploys/d1", """{"id":"d1","state":"skipped","commit_ref":"abc1234ff"}""")
            .On("GET", "sites/s1/build_hooks", "[]")
            .On("POST", "sites/s1/build_hooks", """{"id":"h1","branch":"aide/v1"}""")
            .On("POST", "/build_hooks/h1", "")
            .On("GET", "sites/s1/deploys?per_page=30", """[{"id":"d9","title":"aide:hook:abc1234ff"}]""")
            .On("GET", "deploys/d9", """{"id":"d9","state":"ready","commit_ref":"abc1234ff","deploy_ssl_url":"https://d9--todo.netlify.app"}""");
        var target = Target(DeploymentProvider.Netlify, "s1");

        var hooked = await provider.GetDeploymentAsync(Auth, target, "d1|abc1234ff|1|aide/v1", TestContext.Current.CancellationToken);
        var ready = await provider.GetDeploymentAsync(Auth, target, hooked.Id, TestContext.Current.CancellationToken);

        Assert.Equal($"{NetlifyRules.HookPending}|abc1234ff|{NetlifyRules.HookAttempt}|aide/v1", hooked.Id);
        Assert.Equal(DeploymentStatus.Queued, hooked.Status);
        Assert.Contains("\"branch\":\"aide/v1\"", Assert.Single(stub.Where("POST", "sites/s1/build_hooks")).Body, StringComparison.Ordinal);
        Assert.Contains("trigger_title=aide:hook:abc1234ff", Assert.Single(stub.Where("POST", "/build_hooks/h1")).Path, StringComparison.Ordinal);
        Assert.Equal("d9|abc1234ff|3|aide/v1", ready.Id);
        Assert.Equal(DeploymentStatus.Ready, ready.Status);
    }

    [Fact]
    public async Task FR147_A_skipped_build_after_the_build_hook_is_an_error_not_a_stall()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "deploys/d9", """{"id":"d9","state":"skipped","commit_ref":"abc1234ff"}""");

        var failed = await provider.GetDeploymentAsync(Auth, Target(DeploymentProvider.Netlify, "s1"), "d9|abc1234ff|3|aide/v1", TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentStatus.Error, failed.Status);
        Assert.Contains("skipped", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(stub.Where("POST", "/build_hooks"));
    }

    [Fact]
    public async Task FR150_Rollback_unlocks_a_restore_that_locked_auto_publishing()
    {
        var (provider, stub, _) = Create();
        stub.On("POST", "sites/s1/deploys/d1/restore", """{"id":"d1","state":"ready","locked":true}""")
            .On("POST", "deploys/d1/unlock", """{"id":"d1","locked":false}""");

        await provider.RollbackAsync(Auth, Target(DeploymentProvider.Netlify, "s1"), "d1", TestContext.Current.CancellationToken);

        Assert.Single(stub.Where("POST", "deploys/d1/unlock"));
    }

    [Fact]
    public async Task FR150_Rollback_leaves_an_unlocked_restore_alone()
    {
        var (provider, stub, _) = Create();
        stub.On("POST", "sites/s1/deploys/d1/restore", """{"id":"d1","state":"ready","locked":false}""");

        await provider.RollbackAsync(Auth, Target(DeploymentProvider.Netlify, "s1"), "d1", TestContext.Current.CancellationToken);

        Assert.Empty(stub.Where("POST", "deploys/d1/unlock"));
    }

    [Fact]
    public async Task FR149_Production_never_adopts_a_deploy_with_an_empty_or_short_commit_ref()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "sites/s1/deploys?production=true", """[{"id":"d0","commit_ref":""},{"id":"d1","commit_ref":"abc"},{"id":"d2"}]""");

        var adopted = await provider.FindDeploymentForShaAsync(
            Auth, Target(DeploymentProvider.Netlify, "s1"), DeploymentEnvironment.Production, "abc1234ffee", null, null, TestContext.Current.CancellationToken);

        Assert.Null(adopted);
    }

    [Fact]
    public async Task FR149_A_long_Netlify_Retry_After_holds_creations_back_without_using_the_daily_budget()
    {
        var (provider, stub, time) = Create();
        var calls = 0;
        stub.On("POST", "sites/s1/builds", _ =>
        {
            if (calls++ > 0)
            {
                return PlatformStub.Json("""{"deploy_id":"d"}""");
            }

            var response = PlatformStub.Json("{\"message\":\"rate limited\"}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });
        var target = Target(DeploymentProvider.Netlify, "s1");
        DeployRequest Request() => new(Guid.CreateVersion7(), target, DeploymentEnvironment.Production, "main", "a");

        await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.DeployAsync(Auth, Request(), TestContext.Current.CancellationToken));
        time.Now = Now.AddSeconds(90);
        var held = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.DeployAsync(Auth, Request(), TestContext.Current.CancellationToken));

        Assert.Equal(DeploymentFailure.RateLimited, held.Failure);
        Assert.Equal(Now.AddSeconds(120), held.RetryAt);
        Assert.Single(stub.Where("POST", "sites/s1/builds"));
        time.Now = Now.AddSeconds(121);
        for (var i = 0; i < NetlifyRules.DeploysPerMinute; i++)
        {
            await provider.DeployAsync(Auth, Request(), TestContext.Current.CancellationToken);
        }

        Assert.Equal(1 + NetlifyRules.DeploysPerMinute, stub.Where("POST", "sites/s1/builds").Count());
    }
}
