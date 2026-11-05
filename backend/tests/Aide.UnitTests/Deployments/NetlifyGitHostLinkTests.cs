using System.Net;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Infrastructure.Bitbucket;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.GitLab;
using Aide.Infrastructure.Platforms;
using Aide.UnitTests.GitHosts;
using static Aide.UnitTests.Deployments.PlatformFixtures;

namespace Aide.UnitTests.Deployments;

/// <summary>A record of what the Netlify provider asks of the repository, with keys already on it.</summary>
internal sealed class RecordingRepoLinks : IGitHostRepoLinks
{
    public List<RepoDeployKey> Keys { get; } = [];

    public List<string> Webhooks { get; } = [];

    public Exception? WebhookFailure { get; set; }

    public Task<IReadOnlyList<RepoDeployKey>> ListDeployKeysAsync(Guid projectId, DeploymentRepoRef repo, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RepoDeployKey>>(Keys);

    public Task AddReadOnlyDeployKeyAsync(Guid projectId, DeploymentRepoRef repo, string title, string publicKey, CancellationToken cancellationToken)
    {
        Keys.Add(new RepoDeployKey(publicKey, true));
        return Task.CompletedTask;
    }

    public Task EnsurePushWebhookAsync(Guid projectId, DeploymentRepoRef repo, string url, string description, CancellationToken cancellationToken)
    {
        if (WebhookFailure is not null)
        {
            throw WebhookFailure;
        }

        if (!Webhooks.Contains(url))
        {
            Webhooks.Add(url);
        }

        return Task.CompletedTask;
    }
}

/// <summary>FR-144, D-V4-16, §4.20, §8: Netlify sites for GitLab and Bitbucket repositories, against stubbed Netlify (no network).</summary>
public class NetlifyGitHostSiteTests
{
    private static readonly DeploymentAuth Auth = new("nfp_token", null, "acc_1", "acme-team");

    private static readonly DeploymentRepoRef GitLab = new("gitlab", "acme", "todo", null, "99", "acme/todo", "main") { WebUrl = "https://gitlab.com/acme/todo" };

    private static readonly DeploymentRepoRef Bitbucket = new("bitbucket", "acme", "todo", null, "{uuid-1}", "acme/todo", "main");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (NetlifyDeploymentProvider Provider, PlatformStub Stub, RecordingRepoLinks Links) Create()
    {
        var stub = new PlatformStub();
        var links = new RecordingRepoLinks();
        var time = new ManualTime(Now);
        return (new NetlifyDeploymentProvider(new PlatformHttp(new PlatformStubFactory(stub), time), new NetlifyDeployRateLimiter(time), links), stub, links);
    }

    private static DeploymentSetupRequest Request(DeploymentRepoRef repo) =>
        new(Target(DeploymentProvider.Netlify, null, repo: repo), true, "todo-web", null, null, null);

    private static PlatformStub Empty(PlatformStub stub) => stub
        .On("GET", "acme-team/sites", "[]")
        .On("GET", "deploy_keys", "[]")
        .On("POST", "deploy_keys", """{"id":"dk1","public_key":"ssh-rsa AAAA netlify"}""")
        .On("POST", "acme-team/sites", """{"id":"s1","name":"todo-web"}""")
        .On("PATCH", "sites/s1", """{"id":"s1","name":"todo-web","ssl_url":"https://todo-web.netlify.app"}""");

    [Fact]
    public async Task FR144_A_GitLab_site_gets_a_webhook_a_read_only_key_and_deploy_key_id()
    {
        var (provider, stub, links) = Create();
        Empty(stub);

        var result = await provider.SetupTargetAsync(Auth, Request(GitLab), Ct);

        Assert.Equal("s1", result.ResourceId);
        Assert.Equal(["https://api.netlify.com/hooks/gitlab"], links.Webhooks);
        Assert.True(Assert.Single(links.Keys).ReadOnly);
        var body = Assert.Single(stub.Where("POST", "acme-team/sites")).Body;
        Assert.Contains("\"provider\":\"gitlab\"", body, StringComparison.Ordinal);
        Assert.Contains("\"deploy_key_id\":\"dk1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"id\":99", body, StringComparison.Ordinal);
        Assert.Contains("\"repo_url\":\"https://gitlab.com/acme/todo\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("installation_id", body, StringComparison.Ordinal);
        Assert.Contains("\"allowed_branches\":[\"main\"]", body, StringComparison.Ordinal);
        Assert.Contains("\"skip_prs\":true", Assert.Single(stub.Where("PATCH", "sites/s1")).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR144_A_Bitbucket_site_points_only_at_Netlifys_bitbucket_hook_and_sends_no_uuid_as_id()
    {
        var (provider, stub, links) = Create();
        Empty(stub);

        await provider.SetupTargetAsync(Auth, Request(Bitbucket), Ct);

        Assert.Equal(["https://api.netlify.com/hooks/bitbucket"], links.Webhooks);
        var body = Assert.Single(stub.Where("POST", "acme-team/sites")).Body;
        Assert.Contains("\"provider\":\"bitbucket\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("uuid-1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR144_A_retry_after_the_key_was_added_reuses_it_and_makes_no_duplicate()
    {
        var (provider, stub, links) = Create();
        links.Keys.Add(new RepoDeployKey("ssh-rsa AAAA another-comment", true));
        stub.On("GET", "acme-team/sites", "[]")
            .On("GET", "deploy_keys", """[{"id":"old","public_key":"ssh-rsa ZZZZ x"},{"id":"dk1","public_key":"ssh-rsa AAAA netlify"}]""")
            .On("POST", "acme-team/sites", """{"id":"s1","name":"todo-web"}""")
            .On("PATCH", "sites/s1", """{"id":"s1","name":"todo-web"}""");

        await provider.SetupTargetAsync(Auth, Request(GitLab), Ct);

        Assert.Empty(stub.Where("POST", "deploy_keys"));
        Assert.Single(links.Keys);
        Assert.Contains("\"deploy_key_id\":\"dk1\"", Assert.Single(stub.Where("POST", "acme-team/sites")).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR144_A_retry_after_the_site_was_created_only_makes_sure_of_the_webhook()
    {
        var (provider, stub, links) = Create();
        stub.On("GET", "acme-team/sites", """[{"id":"s1","name":"todo-web","build_settings":{"repo_path":"acme/todo"}}]""")
            .On("PATCH", "sites/s1", """{"id":"s1","name":"todo-web"}""");

        var result = await provider.SetupTargetAsync(Auth, Request(GitLab), Ct);

        Assert.Equal("s1", result.ResourceId);
        Assert.Single(links.Webhooks);
        Assert.Empty(links.Keys);
        Assert.Empty(stub.Where("POST", "acme-team/sites"));
        Assert.Empty(stub.Where("POST", "deploy_keys"));
    }

    [Fact]
    public async Task FR144_A_connection_that_cant_add_the_webhook_stops_before_anything_is_made_on_Netlify()
    {
        var (provider, stub, links) = Create();
        Empty(stub);
        links.WebhookFailure = new DeploymentProviderException(DeploymentFailure.PermissionMissing, 403, "no scope", platform: DeploymentProvider.Netlify);

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.SetupTargetAsync(Auth, Request(Bitbucket), Ct));

        Assert.Equal(DeploymentFailure.PermissionMissing, ex.Failure);
        Assert.Empty(stub.Where("POST", "deploy_keys"));
        Assert.Empty(stub.Where("POST", "acme-team/sites"));
    }

    [Fact]
    public async Task FR144_A_Netlify_401_on_the_deploy_key_is_the_Netlify_token_rejected()
    {
        var (provider, stub, _) = Create();
        stub.On("GET", "acme-team/sites", "[]").On("GET", "deploy_keys", """{"message":"Access Denied"}""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.SetupTargetAsync(Auth, Request(GitLab), Ct));

        Assert.Equal(DeploymentFailure.CredentialRejected, ex.Failure);
    }

    [Fact]
    public async Task FR144_A_repository_on_no_Git_host_is_not_supported()
    {
        var (provider, _, _) = Create();

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.SetupTargetAsync(Auth, Request(DeploymentRepoRef.Local), Ct));

        Assert.Equal(DeploymentFailure.NotSupported, ex.Failure);
    }

    [Fact]
    public void FR144_S8_Webhooks_go_only_to_Netlifys_hook_for_the_host()
    {
        Assert.Equal("https://api.netlify.com/hooks/gitlab", NetlifyRules.WebhookUrl("gitlab"));
        Assert.Equal("https://api.netlify.com/hooks/bitbucket", NetlifyRules.WebhookUrl("bitbucket"));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetlifyRules.WebhookUrl("github"));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetlifyRules.WebhookUrl("https://evil.example"));
    }

    [Fact]
    public void FR144_S8_A_key_the_repository_lets_push_is_never_reused()
    {
        NetlifyDeployKey[] netlify = [new("dk1", "ssh-rsa AAAA a")];

        Assert.Equal("dk1", NetlifyRules.ReusableDeployKey(netlify, [new RepoDeployKey("ssh-rsa AAAA b", true)]));
        Assert.Null(NetlifyRules.ReusableDeployKey(netlify, [new RepoDeployKey("ssh-rsa AAAA a", false)]));
        Assert.Null(NetlifyRules.ReusableDeployKey(netlify, [new RepoDeployKey("ssh-rsa BBBB a", true)]));
        Assert.False(NetlifyRules.SamePublicKey(null, "ssh-rsa AAAA"));
        Assert.False(NetlifyRules.SamePublicKey("garbage", "garbage"));
    }
}

/// <summary>A scripted <see cref="IGitConnectionTokens"/>: hands out one token, records 401s, refreshes when allowed.</summary>
internal sealed class ScriptedGitTokens(GitAccessToken token, Result<GitAccessToken>? onUnauthorized = null) : IGitConnectionTokens
{
    public int Unauthorized { get; private set; }

    public Task<Result<GitAccessToken>> GetAccessTokenAsync(Guid connectionId, CancellationToken cancellationToken) => Task.FromResult(Result.Success(token));

    public Task<Result<GitAccessToken>> OnUnauthorizedAsync(GitAccessToken rejected, Guid? projectId, CancellationToken cancellationToken)
    {
        Unauthorized++;
        return Task.FromResult(onUnauthorized ?? Result.Failure<GitAccessToken>(new DomainError(ErrorCodes.GitNotConnected, "Connect again.")));
    }

    public Task MarkInvalidAsync(Guid connectionId, string reason, Guid? projectId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>FR-144, §4.19 "Netlify link support", §8: deploy keys and webhooks against stubbed GitLab and Bitbucket (no network).</summary>
public class GitHostRepoLinksTests
{
    private const string HookUrl = "https://api.netlify.com/hooks/gitlab";
    private const string BitbucketHookUrl = "https://api.netlify.com/hooks/bitbucket";
    private const string GitLabKeys = "/api/v4/projects/99/deploy_keys";
    private const string GitLabHooks = "/api/v4/projects/99/hooks";
    private const string BitbucketKeys = "/2.0/repositories/acme/todo/deploy-keys";
    private const string BitbucketHooks = "/2.0/repositories/acme/todo/hooks";

    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly Guid InstanceId = Guid.CreateVersion7();
    private static readonly Guid ProjectId = Guid.CreateVersion7();

    private static readonly DeploymentRepoRef GitLab = new("gitlab", "acme", "todo", null, "99", "acme/todo", "main") { GitConnectionId = ConnectionId, InstanceId = InstanceId };

    private static readonly DeploymentRepoRef Bitbucket = new("bitbucket", "acme", "todo", null, "{uuid-1}", "acme/todo", "main") { GitConnectionId = ConnectionId, InstanceId = InstanceId };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GitAccessToken Token(GitHostKind kind, string secret = "git-token", Guid? instance = null, bool refreshed = false) => new(
        ConnectionId,
        Guid.CreateVersion7(),
        kind,
        instance ?? InstanceId,
        new Uri(kind == GitHostKind.Gitlab ? "https://gitlab.example.com" : BitbucketRules.ApiBaseUrl),
        GitConnectionAuthKind.Oauth,
        "ben",
        secret,
        1,
        refreshed);

    private static GitHostRepoLinks Links(PlatformStub stub, IGitConnectionTokens tokens)
    {
        var factory = new AnyHostStubFactory(stub);
        var time = new ManualTime(Now);
        return new GitHostRepoLinks(
            tokens,
            new GitLabClients(factory, new StaticMonitor<GitLabOptions>(new GitLabOptions()), time),
            new BitbucketClients(factory, new StaticMonitor<BitbucketOptions>(new BitbucketOptions()), new BitbucketRateLimiter(time), time),
            time);
    }

    private static GitHostRepoLinks GitLabLinks(PlatformStub stub) => Links(stub, new ScriptedGitTokens(Token(GitHostKind.Gitlab)));

    private static GitHostRepoLinks BitbucketLinks(PlatformStub stub) => Links(stub, new ScriptedGitTokens(Token(GitHostKind.Bitbucket)));

    private static HttpResponseMessage RateLimited()
    {
        var response = PlatformStub.Json("""{"message":"Too many"}""", HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "120");
        return response;
    }

    [Fact]
    public async Task FR144_S8_GitLab_key_is_added_with_can_push_false_on_the_instance()
    {
        var stub = new PlatformStub().On("POST", GitLabKeys, "{}", HttpStatusCode.Created);

        await GitLabLinks(stub).AddReadOnlyDeployKeyAsync(ProjectId, GitLab, NetlifyRules.RepoLinkTitle, "ssh-rsa AAAA", Ct);

        var body = Assert.Single(stub.Where("POST", GitLabKeys)).Body;
        Assert.Contains("\"can_push\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"ssh-rsa AAAA\"", body, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"Netlify (added by Tevrin)\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR144_GitLab_keys_report_whether_they_can_push()
    {
        var stub = new PlatformStub().On("GET", GitLabKeys, """[{"key":"ssh-rsa A","can_push":false},{"key":"ssh-rsa B","can_push":true}]""");

        var keys = await GitLabLinks(stub).ListDeployKeysAsync(ProjectId, GitLab, Ct);

        Assert.Equal([new RepoDeployKey("ssh-rsa A", true), new RepoDeployKey("ssh-rsa B", false)], keys);
    }

    [Fact]
    public async Task FR144_GitLab_key_taken_by_an_earlier_attempt_counts_as_added()
    {
        var stub = new PlatformStub()
            .On("POST", GitLabKeys, """{"message":{"deploy_key.fingerprint_sha256":["has already been taken"]}}""", HttpStatusCode.BadRequest)
            .On("GET", GitLabKeys, """[{"key":"ssh-rsa AAAA c","can_push":false}]""");

        await GitLabLinks(stub).AddReadOnlyDeployKeyAsync(ProjectId, GitLab, NetlifyRules.RepoLinkTitle, "ssh-rsa AAAA", Ct);

        Assert.Single(stub.Where("POST", GitLabKeys));
    }

    [Fact]
    public async Task FR144_GitLab_key_refused_for_another_reason_stays_refused()
    {
        var stub = new PlatformStub()
            .On("POST", GitLabKeys, """{"message":"key is invalid"}""", HttpStatusCode.BadRequest)
            .On("GET", GitLabKeys, "[]");

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => GitLabLinks(stub).AddReadOnlyDeployKeyAsync(ProjectId, GitLab, "t", "ssh-rsa AAAA", Ct));

        Assert.Equal(DeploymentFailure.Rejected, ex.Failure);
    }

    [Fact]
    public async Task FR144_GitLab_webhook_is_added_once_with_push_events()
    {
        var hooks = "[]";
        var stub = new PlatformStub()
            .On("GET", GitLabHooks, _ => PlatformStub.Json(hooks))
            .On("POST", GitLabHooks, _ =>
            {
                hooks = $$"""[{"id":5,"url":"{{HookUrl}}","push_events":true}]""";
                return PlatformStub.Json("{}", HttpStatusCode.Created);
            });
        var links = GitLabLinks(stub);

        await links.EnsurePushWebhookAsync(ProjectId, GitLab, HookUrl, "d", Ct);
        await links.EnsurePushWebhookAsync(ProjectId, GitLab, HookUrl, "d", Ct);

        var post = Assert.Single(stub.Where("POST", GitLabHooks));
        Assert.Contains($"\"url\":\"{HookUrl}\"", post.Body, StringComparison.Ordinal);
        Assert.Contains("\"push_events\":true", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR144_GitLab_existing_webhook_without_push_events_is_turned_on_not_duplicated()
    {
        var stub = new PlatformStub()
            .On("GET", GitLabHooks, $$"""[{"id":5,"url":"{{HookUrl}}","push_events":false},{"id":6,"url":"https://other.example","push_events":true}]""")
            .On("PUT", $"{GitLabHooks}/5", "{}");

        await GitLabLinks(stub).EnsurePushWebhookAsync(ProjectId, GitLab, HookUrl, "d", Ct);

        Assert.Empty(stub.Where("POST", GitLabHooks));
        Assert.Contains("\"push_events\":true", Assert.Single(stub.Where("PUT", GitLabHooks)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR144_Bitbucket_key_has_a_label_and_webhook_is_a_push_hook()
    {
        var stub = new PlatformStub()
            .On("POST", BitbucketKeys, "{}", HttpStatusCode.Created)
            .On("GET", BitbucketHooks, """{"values":[]}""")
            .On("POST", BitbucketHooks, "{}", HttpStatusCode.Created);
        var links = BitbucketLinks(stub);

        await links.AddReadOnlyDeployKeyAsync(ProjectId, Bitbucket, NetlifyRules.RepoLinkTitle, "ssh-rsa AAAA", Ct);
        await links.EnsurePushWebhookAsync(ProjectId, Bitbucket, BitbucketHookUrl, NetlifyRules.RepoLinkTitle, Ct);

        var key = Assert.Single(stub.Where("POST", BitbucketKeys)).Body;
        Assert.Contains("\"label\":\"Netlify (added by Tevrin)\"", key, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"ssh-rsa AAAA\"", key, StringComparison.Ordinal);
        var hook = Assert.Single(stub.Where("POST", BitbucketHooks)).Body;
        Assert.Contains("\"events\":[\"repo:push\"]", hook, StringComparison.Ordinal);
        Assert.Contains("\"active\":true", hook, StringComparison.Ordinal);
        Assert.Contains($"\"url\":\"{BitbucketHookUrl}\"", hook, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR144_Bitbucket_keys_are_read_only_and_paged()
    {
        var stub = new PlatformStub()
            .On("GET", $"{BitbucketKeys}?pagelen=100&page=1", """{"values":[{"key":"ssh-rsa A"}],"next":"https://api.bitbucket.org/next"}""")
            .On("GET", $"{BitbucketKeys}?pagelen=100&page=2", """{"values":[{"key":"ssh-rsa B"}]}""");

        var keys = await BitbucketLinks(stub).ListDeployKeysAsync(ProjectId, Bitbucket, Ct);

        Assert.Equal([new RepoDeployKey("ssh-rsa A", true), new RepoDeployKey("ssh-rsa B", true)], keys);
    }

    [Fact]
    public async Task FR144_Bitbucket_existing_active_push_webhook_makes_no_call()
    {
        var stub = new PlatformStub().On("GET", BitbucketHooks, $$"""{"values":[{"uuid":"{h}","url":"{{BitbucketHookUrl}}","active":true,"events":["repo:push"]}]}""");

        await BitbucketLinks(stub).EnsurePushWebhookAsync(ProjectId, Bitbucket, BitbucketHookUrl, "d", Ct);

        Assert.Single(stub.Calls);
    }

    [Fact]
    public async Task FR144_Bitbucket_inactive_webhook_is_turned_on_not_duplicated()
    {
        var stub = new PlatformStub()
            .On("GET", BitbucketHooks, $$"""{"values":[{"uuid":"{h}","url":"{{BitbucketHookUrl}}","active":false,"events":["repo:push"]}]}""")
            .On("PUT", $"{BitbucketHooks}/{{h}}", "{}");

        await BitbucketLinks(stub).EnsurePushWebhookAsync(ProjectId, Bitbucket, BitbucketHookUrl, "d", Ct);

        Assert.Empty(stub.Where("POST", BitbucketHooks));
        Assert.Single(stub.Where("PUT", BitbucketHooks));
    }

    [Fact]
    public async Task FR144_A_GitLab_401_refreshes_once_and_retries_with_the_new_token()
    {
        var calls = 0;
        var stub = new PlatformStub().On("GET", GitLabKeys, _ => ++calls == 1 ? PlatformStub.Json("""{"message":"401"}""", HttpStatusCode.Unauthorized) : PlatformStub.Json("[]"));
        var tokens = new ScriptedGitTokens(Token(GitHostKind.Gitlab), Result.Success(Token(GitHostKind.Gitlab, "refreshed", refreshed: true)));

        Assert.Empty(await Links(stub, tokens).ListDeployKeysAsync(ProjectId, GitLab, Ct));

        Assert.Equal(1, tokens.Unauthorized);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FR144_A_Git_401_that_cant_refresh_is_git_not_connected_never_the_Netlify_token()
    {
        var stub = new PlatformStub().On("GET", BitbucketKeys, """{"error":{"message":"bad"}}""", HttpStatusCode.Unauthorized);
        var tokens = new ScriptedGitTokens(Token(GitHostKind.Bitbucket));

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => Links(stub, tokens).ListDeployKeysAsync(ProjectId, Bitbucket, Ct));

        Assert.Equal(DeploymentFailure.GitNotConnected, ex.Failure);
        Assert.Equal(DeploymentProvider.Netlify, ex.Platform);
        Assert.Equal(1, tokens.Unauthorized);
    }

    [Fact]
    public async Task FR144_A_GitLab_403_is_a_missing_permission_naming_Maintainer()
    {
        var stub = new PlatformStub().On("POST", GitLabKeys, """{"message":"403 Forbidden"}""", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => GitLabLinks(stub).AddReadOnlyDeployKeyAsync(ProjectId, GitLab, "t", "ssh-rsa A", Ct));

        Assert.Equal(DeploymentFailure.PermissionMissing, ex.Failure);
        Assert.Contains("Maintainer", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.PlatformPermissionMissing, VercelErrorRules.ErrorCodeFor(ex.Failure));
    }

    [Fact]
    public async Task FR144_A_Bitbucket_403_for_scopes_names_the_webhook_scopes()
    {
        var stub = new PlatformStub().On(
            "GET",
            BitbucketHooks,
            """{"error":{"message":"Your credentials lack one or more required privilege scopes.","detail":{"required":["read:webhook:bitbucket"],"granted":["read:repository:bitbucket"]}}}""",
            HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => BitbucketLinks(stub).EnsurePushWebhookAsync(ProjectId, Bitbucket, BitbucketHookUrl, "d", Ct));

        Assert.Equal(DeploymentFailure.PermissionMissing, ex.Failure);
        Assert.Contains("write:webhook:bitbucket", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR144_A_429_on_either_host_waits_with_its_reset()
    {
        var gitlab = new PlatformStub().On("GET", GitLabHooks, _ => RateLimited());
        var bitbucket = new PlatformStub().On("GET", BitbucketHooks, _ => RateLimited());

        var first = await Assert.ThrowsAsync<DeploymentProviderException>(() => GitLabLinks(gitlab).EnsurePushWebhookAsync(ProjectId, GitLab, HookUrl, "d", Ct));
        var second = await Assert.ThrowsAsync<DeploymentProviderException>(() => BitbucketLinks(bitbucket).EnsurePushWebhookAsync(ProjectId, Bitbucket, BitbucketHookUrl, "d", Ct));

        Assert.Equal(DeploymentFailure.RateLimited, first.Failure);
        Assert.Equal(Now.AddSeconds(120), first.RetryAt);
        Assert.Equal(DeploymentFailure.RateLimited, second.Failure);
        Assert.Equal(Now.AddSeconds(120), second.RetryAt);
    }

    [Fact]
    public async Task FR144_A_5xx_is_upstream_and_retries()
    {
        var stub = new PlatformStub().On("GET", GitLabKeys, """{"message":"oops"}""", HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => GitLabLinks(stub).ListDeployKeysAsync(ProjectId, GitLab, Ct));

        Assert.Equal(DeploymentFailure.Upstream, ex.Failure);
    }

    [Fact]
    public async Task FR144_S8_An_unbound_project_sends_nothing()
    {
        var stub = new PlatformStub();

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => GitLabLinks(stub).ListDeployKeysAsync(ProjectId, GitLab with { GitConnectionId = null }, Ct));

        Assert.Equal(DeploymentFailure.GitNotConnected, ex.Failure);
        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task FR144_S8_A_token_of_another_instance_or_host_sends_nothing()
    {
        var stub = new PlatformStub();

        var otherInstance = await Assert.ThrowsAsync<DeploymentProviderException>(
            () => Links(stub, new ScriptedGitTokens(Token(GitHostKind.Gitlab, instance: Guid.CreateVersion7()))).ListDeployKeysAsync(ProjectId, GitLab, Ct));
        var otherHost = await Assert.ThrowsAsync<DeploymentProviderException>(
            () => Links(stub, new ScriptedGitTokens(Token(GitHostKind.Bitbucket))).ListDeployKeysAsync(ProjectId, GitLab, Ct));

        Assert.Equal(DeploymentFailure.GitNotConnected, otherInstance.Failure);
        Assert.Equal(DeploymentFailure.GitNotConnected, otherHost.Failure);
        Assert.Empty(stub.Calls);
    }
}
