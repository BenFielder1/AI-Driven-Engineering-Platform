using System.Net;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.GitHub;
using static Aide.UnitTests.Deployments.PlatformFixtures;

namespace Aide.UnitTests.Deployments;

/// <summary>FR-145, FR-146, §4.20, §4.22: GitHub Pages build-time values as repository Actions variables, against a stubbed GitHub (no network).</summary>
public class GitHubPagesEnvSyncTests
{
    private const string Variables = "/repos/acme/todo/actions/variables";
    private const DeploymentEnvironment Production = DeploymentEnvironment.Production;
    private const DeploymentEnvironment Preview = DeploymentEnvironment.Preview;

    private static readonly DeploymentAuth Auth = new("ghs_token", null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeploymentTargetRef PagesTarget => Target(DeploymentProvider.GithubPages, "acme/todo");

    private static (GitHubPagesDeploymentProvider Provider, PlatformStub Stub) Create()
    {
        var stub = new PlatformStub();
        return (new GitHubPagesDeploymentProvider(new GitHubApi(new AnyHostStubFactory(stub), new ManualTime(Now))), stub);
    }

    private static DeploymentEnvWrite Public(string key, string value, DeploymentEnvironment environment, string? existing = null) =>
        new(key, value, environment, false, existing, true);

    private static Task<IReadOnlyList<DeploymentEnvWritten>> Sync(GitHubPagesDeploymentProvider provider, IReadOnlyList<DeploymentEnvWrite> writes, params string[] deletes) =>
        provider.SyncEnvAsync(Auth, PagesTarget, new DeploymentEnvSyncRequest(writes, deletes), Ct);

    [Fact]
    public async Task FR146_VITE_API_BASE_URL_reaches_the_workflow_as_a_new_repository_variable_with_the_production_value()
    {
        var (provider, stub) = Create();
        stub.On("POST", Variables, "{}", HttpStatusCode.Created);

        var written = await Sync(provider, [
            Public("VITE_API_BASE_URL", "https://api.up.railway.app", Production),
            Public("VITE_API_BASE_URL", "https://preview.up.railway.app", Preview)]);

        Assert.Single(stub.Where("PATCH", Variables));
        var post = Assert.Single(stub.Where("POST", Variables)).Body;
        Assert.Contains("\"name\":\"VITE_API_BASE_URL\"", post, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"https://api.up.railway.app\"", post, StringComparison.Ordinal);
        Assert.DoesNotContain(stub.Calls, c => c.Body.Contains("preview.up.railway.app", StringComparison.Ordinal));
        Assert.Equal(
            [GitHubPagesRules.EnvId("VITE_API_BASE_URL", Production), GitHubPagesRules.EnvId("VITE_API_BASE_URL", Preview)],
            written.Select(w => w.Id));
        Assert.All(written, w => Assert.Null(w.Refused));
    }

    [Fact]
    public async Task FR146_A_201_with_an_empty_body_counts_as_created()
    {
        var (provider, stub) = Create();
        stub.On("POST", Variables, _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) });

        var written = await Sync(provider, [Public("VITE_X", "1", Production)]);

        Assert.Null(Assert.Single(written).Refused);
    }

    [Fact]
    public async Task FR145_An_existing_variable_is_updated_in_place()
    {
        var (provider, stub) = Create();
        stub.On("PATCH", $"{Variables}/VITE_API_BASE_URL", "{}", HttpStatusCode.NoContent);

        await Sync(provider, [Public("VITE_API_BASE_URL", "https://new", Production, "pages:production:VITE_API_BASE_URL")]);

        Assert.Contains("\"value\":\"https://new\"", Assert.Single(stub.Where("PATCH", Variables)).Body, StringComparison.Ordinal);
        Assert.Empty(stub.Where("POST", Variables));
    }

    [Fact]
    public async Task FR145_A_create_that_lost_a_race_updates_instead()
    {
        var (provider, stub) = Create();
        var patches = 0;
        stub.On("PATCH", $"{Variables}/VITE_X", _ => ++patches == 1 ? PlatformStub.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound) : PlatformStub.Json("{}"))
            .On("POST", Variables, """{"message":"Already exists"}""", HttpStatusCode.Conflict);

        await Sync(provider, [Public("VITE_X", "1", Production)]);

        Assert.Equal(2, patches);
    }

    [Fact]
    public async Task FR145_Sensitive_values_never_reach_GitHub_and_are_refused_with_a_reason()
    {
        var (provider, stub) = Create();

        var written = await Sync(provider, [new DeploymentEnvWrite("API_TOKEN", "s3cret", Production, true, null, true)]);

        Assert.Empty(stub.Calls);
        Assert.Contains("sensitive", Assert.Single(written).Refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR145_A_non_public_variable_is_refused_and_a_secret_looking_public_one_too()
    {
        var (provider, stub) = Create();

        var written = await Sync(provider, [
            new DeploymentEnvWrite("BUILD_FLAG", "1", Production, false, null),
            Public("VITE_SERVICE_ROLE", "x", Production),
            Public("VITE_SAFE_NAME", "sb_secret_abc", Production)]);

        Assert.Empty(stub.Calls);
        Assert.All(written, w => Assert.NotNull(w.Refused));
    }

    [Fact]
    public async Task FR145_A_synced_variable_that_became_sensitive_is_removed_from_GitHub()
    {
        var (provider, stub) = Create();
        stub.On("DELETE", $"{Variables}/VITE_KEY", "{}", HttpStatusCode.NoContent);

        var written = await Sync(provider, [new DeploymentEnvWrite("VITE_KEY", "s3cret", Production, true, "pages:production:VITE_KEY", true)]);

        Assert.Single(stub.Where("DELETE", Variables));
        Assert.Empty(stub.Where("PATCH", Variables));
        Assert.NotNull(Assert.Single(written).Refused);
    }

    [Fact]
    public async Task FR145_A_deleted_key_is_removed_and_already_gone_is_fine_and_a_preview_id_calls_nothing()
    {
        var (provider, stub) = Create();
        stub.On("DELETE", $"{Variables}/OLD", "{}", HttpStatusCode.NoContent);

        await Sync(provider, [], "pages:production:OLD", "pages:production:GONE", "pages:preview:OLD", "env_from_another_provider");

        Assert.Equal([$"{Variables}/OLD", $"{Variables}/GONE"], stub.Where("DELETE", Variables).Select(c => c.Path));
    }

    [Fact]
    public async Task FR145_A_401_is_credential_rejected()
    {
        var (provider, stub) = Create();
        stub.On("PATCH", Variables, """{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => Sync(provider, [Public("VITE_X", "1", Production)]));

        Assert.Equal(DeploymentFailure.CredentialRejected, ex.Failure);
    }

    [Fact]
    public async Task FR145_A_403_names_the_Variables_permission()
    {
        var (provider, stub) = Create();
        stub.On("PATCH", Variables, """{"message":"Resource not accessible by integration"}""", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => Sync(provider, [Public("VITE_X", "1", Production)]));

        Assert.Equal(DeploymentFailure.PermissionMissing, ex.Failure);
        Assert.Contains("Variables", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR145_A_429_waits_for_its_reset()
    {
        var (provider, stub) = Create();
        stub.On("PATCH", Variables, _ =>
        {
            var response = PlatformStub.Json("""{"message":"slow down"}""", HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "60");
            return response;
        });

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => Sync(provider, [Public("VITE_X", "1", Production)]));

        Assert.Equal(DeploymentFailure.RateLimited, ex.Failure);
        Assert.Equal(Now.AddSeconds(60), ex.RetryAt);
    }

    [Fact]
    public async Task FR145_A_GITHUB_prefixed_name_is_refused_without_calling_GitHub()
    {
        var (provider, stub) = Create();

        var written = await Sync(provider, [Public("GITHUB_REF_NAME", "x", Production)]);

        Assert.Empty(stub.Calls);
        Assert.Contains("GITHUB_", Assert.Single(written).Refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR145_One_variable_GitHub_refuses_doesnt_stop_the_others()
    {
        var (provider, stub) = Create();
        stub.On("PATCH", $"{Variables}/VITE_BAD", """{"message":"Invalid name"}""", HttpStatusCode.UnprocessableEntity)
            .On("PATCH", $"{Variables}/VITE_DOWN", """{"message":"boom"}""", HttpStatusCode.BadGateway)
            .On("PATCH", $"{Variables}/VITE_GOOD", "{}", HttpStatusCode.NoContent);

        var written = await Sync(provider, [Public("VITE_BAD", "1", Production), Public("VITE_DOWN", "1", Production), Public("VITE_GOOD", "1", Production)]);

        Assert.NotNull(written.Single(w => w.Key == "VITE_BAD").Refused);
        Assert.NotNull(written.Single(w => w.Key == "VITE_DOWN").Refused);
        Assert.Null(written.Single(w => w.Key == "VITE_GOOD").Refused);
        Assert.Single(stub.Where("PATCH", $"{Variables}/VITE_GOOD"));
    }

    [Fact]
    public async Task FR145_A_failed_delete_of_a_now_sensitive_variable_keeps_its_id_and_the_next_sync_deletes_it()
    {
        var (provider, stub) = Create();
        var deletes = 0;
        stub.On("DELETE", $"{Variables}/VITE_KEY", _ => ++deletes == 1 ? PlatformStub.Json("""{"message":"boom"}""", HttpStatusCode.BadGateway) : PlatformStub.Json("{}", HttpStatusCode.NoContent));
        const string id = "pages:production:VITE_KEY";
        DeploymentEnvWrite write = new("VITE_KEY", "s3cret", Production, true, id, true);

        var first = Assert.Single(await Sync(provider, [write]));

        Assert.Equal(id, first.Id);
        Assert.Contains("still in the repository", first.Refused, StringComparison.Ordinal);

        var second = Assert.Single(await Sync(provider, [write with { ExistingId = first.Id }]));

        Assert.Equal(2, deletes);
        Assert.Empty(second.Id);
        Assert.NotNull(second.Refused);
    }

    [Fact]
    public async Task FR145_A_failed_update_keeps_the_id_and_a_failed_create_has_none()
    {
        var (provider, stub) = Create();
        stub.On("PATCH", Variables, """{"message":"boom"}""", HttpStatusCode.BadGateway);

        var written = await Sync(provider, [Public("VITE_OLD", "1", Production, "pages:production:VITE_OLD"), Public("VITE_NEW", "1", Production)]);

        Assert.Equal("pages:production:VITE_OLD", written.Single(w => w.Key == "VITE_OLD").Id);
        Assert.Empty(written.Single(w => w.Key == "VITE_NEW").Id);
        Assert.All(written, w => Assert.NotNull(w.Refused));
    }

    private const string Sha = "abc123";
    private const string Runs = "/repos/acme/todo/actions/runs";
    private static readonly DateTimeOffset SyncedAt = Now.AddMinutes(1);

    private static string RunJson(string status, DateTimeOffset startedAt, int attempt = 1) =>
        $$"""{"workflow_runs":[{"id":99,"path":".github/workflows/pages.yml","status":"{{status}}","conclusion":null,"html_url":"https://github.com/run","run_attempt":{{attempt}},"run_started_at":"{{startedAt:O}}"}]}""";

    [Fact]
    public async Task FR146_A_finished_run_that_predates_the_sync_is_re_run()
    {
        var (provider, stub) = Create();
        stub.On("GET", Runs, RunJson("completed", Now)).On("POST", $"{Runs}/99/rerun", "{}", HttpStatusCode.Created);

        Assert.Equal(BuildRefresh.Restarted, await provider.RefreshBuildAsync(Auth, PagesTarget, Sha, SyncedAt, Ct));
        Assert.Single(stub.Where("POST", $"{Runs}/99/rerun"));
    }

    [Fact]
    public async Task FR146_A_running_build_that_predates_the_sync_is_cancelled_first_and_a_409_means_it_is_finishing()
    {
        var (provider, stub) = Create();
        stub.On("GET", Runs, RunJson("in_progress", Now)).On("POST", $"{Runs}/99/cancel", """{"message":"Cannot cancel"}""", HttpStatusCode.Conflict);

        Assert.Equal(BuildRefresh.Stopping, await provider.RefreshBuildAsync(Auth, PagesTarget, Sha, SyncedAt, Ct));
        Assert.Empty(stub.Where("POST", $"{Runs}/99/rerun"));
    }

    [Fact]
    public async Task FR146_No_run_yet_or_a_run_that_started_after_the_sync_is_left_alone()
    {
        var (provider, stub) = Create();
        stub.On("GET", Runs, """{"workflow_runs":[]}""");
        Assert.Equal(BuildRefresh.UpToDate, await provider.RefreshBuildAsync(Auth, PagesTarget, Sha, SyncedAt, Ct));

        var (later, laterStub) = Create();
        laterStub.On("GET", Runs, RunJson("in_progress", SyncedAt.AddSeconds(1)));
        Assert.Equal(BuildRefresh.UpToDate, await later.RefreshBuildAsync(Auth, PagesTarget, Sha, SyncedAt, Ct));
        Assert.Empty(laterStub.Where("POST", Runs));
    }

    [Fact]
    public async Task FR146_A_refused_re_run_names_the_Actions_permission()
    {
        var (provider, stub) = Create();
        stub.On("GET", Runs, RunJson("completed", Now)).On("POST", $"{Runs}/99/rerun", """{"message":"Resource not accessible by integration"}""", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<DeploymentProviderException>(() => provider.RefreshBuildAsync(Auth, PagesTarget, Sha, SyncedAt, Ct));

        Assert.Equal(DeploymentFailure.PermissionMissing, ex.Failure);
        Assert.Contains("Actions", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("completed", -60, 1, true)]
    [InlineData("in_progress", -60, 1, true)]
    [InlineData("waiting", -60, 1, true)]
    [InlineData("queued", -60, 1, false)]
    [InlineData("requested", -60, 1, false)]
    [InlineData("pending", -60, 1, false)]
    [InlineData("completed", 60, 1, false)]
    [InlineData("completed", -60, 2, false)]
    public void FR146_A_build_predates_its_variables_only_once_and_only_if_it_started_before_the_sync(string status, int startedOffsetSeconds, int attempt, bool expected) =>
        Assert.Equal(expected, GitHubPagesRules.BuildPredatesVariables(status, Now.AddSeconds(startedOffsetSeconds), attempt, Now));

    [Fact]
    public void FR146_An_unknown_start_time_isnt_a_stale_build() =>
        Assert.False(GitHubPagesRules.BuildPredatesVariables("completed", null, 1, Now));

    [Fact]
    public void FR145_Variables_permission_is_read_from_what_GitHub_reports()
    {
        Assert.Equal(GitHubPagesAccess.Granted, GitHubRules.VariablesPermission(new Dictionary<string, string> { ["actions_variables"] = "write" }));
        Assert.Equal(GitHubPagesAccess.Missing, GitHubRules.VariablesPermission(new Dictionary<string, string> { ["actions_variables"] = "read" }));
        Assert.Equal(GitHubPagesAccess.Missing, GitHubRules.VariablesPermission(new Dictionary<string, string> { ["pages"] = "write" }));
        Assert.Equal(GitHubPagesAccess.Unknown, GitHubRules.VariablesPermission(null));
    }

    [Fact]
    public void FR145_Only_a_production_id_names_a_repository_variable()
    {
        Assert.Equal("A_B", GitHubPagesRules.ProductionKeyOf(GitHubPagesRules.EnvId("A_B", Production)));
        Assert.Null(GitHubPagesRules.ProductionKeyOf(GitHubPagesRules.EnvId("A_B", Preview)));
        Assert.Null(GitHubPagesRules.ProductionKeyOf("pages:production:"));
        Assert.Null(GitHubPagesRules.ProductionKeyOf(null));
    }
}
