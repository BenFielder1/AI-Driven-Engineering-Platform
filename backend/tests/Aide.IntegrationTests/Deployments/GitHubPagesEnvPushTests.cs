using System.Net;
using Aide.Api.Features.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Deployments;

/// <summary>FR-145, FR-146: what pushing the inventory to GitHub Pages records on each variable, with a stubbed GitHub.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitHubPagesEnvPushTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class GitHubStub : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Calls { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(new HttpResponseMessage(Status));
        }
    }

    private static DeploymentContext Context(GitHubStub stub) => new(
        new DeploymentTarget { ProjectId = Guid.CreateVersion7(), Component = DeploymentComponent.Frontend, Provider = DeploymentProvider.GithubPages, ResourceId = "acme/todo" },
        null,
        new DeploymentAuth("ghs_token", null),
        new GitHubPagesDeploymentProvider(new GitHubApi(stub, TimeProvider.System)),
        new DeploymentRepoRef("github", "acme", "todo", 42, null, "acme/todo", "main"));

    private static DeploymentEnvVar Var(DeploymentAccess access, string key, string value, bool isPublic, bool sensitive, IReadOnlyDictionary<string, string>? ids = null) => new()
    {
        Key = key,
        ValueEncrypted = access.Values.Protect(value),
        Public = isPublic,
        Sensitive = sensitive,
        Targets = ["production"],
        AppliesTo = ["frontend"],
        VercelEnvIds = ids,
    };

    [Fact]
    public async Task FR146_A_public_variable_is_synced_with_its_id_and_a_sensitive_one_only_gets_a_syncError()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<DeploymentAccess>();
        var sync = scope.ServiceProvider.GetRequiredService<DeploymentEnvSync>();
        var stub = new GitHubStub();
        var api = Var(access, "VITE_API_BASE_URL", "https://api.up.railway.app", isPublic: true, sensitive: false);
        var secret = Var(access, "API_TOKEN", "s3cret", isPublic: false, sensitive: true);

        await sync.PushAsync(Context(stub), [api, secret], Ct);

        Assert.Equal(["PATCH /repos/acme/todo/actions/variables/VITE_API_BASE_URL"], stub.Calls);
        Assert.NotNull(api.SyncedAt);
        Assert.Null(api.SyncError);
        Assert.Equal("pages:production:VITE_API_BASE_URL", api.VercelEnvIds!["production"]);
        Assert.Null(secret.SyncedAt);
        Assert.NotNull(secret.SyncError);
        Assert.Null(secret.VercelEnvIds);
    }

    [Fact]
    public async Task FR145_A_failed_delete_keeps_the_id_and_the_error_so_the_next_push_retries_it()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<DeploymentAccess>();
        var sync = scope.ServiceProvider.GetRequiredService<DeploymentEnvSync>();
        var stub = new GitHubStub { Status = HttpStatusCode.BadGateway };
        var changed = Var(access, "VITE_KEY", "s3cret", isPublic: true, sensitive: true, new Dictionary<string, string> { ["production"] = "pages:production:VITE_KEY" });

        await sync.PushAsync(Context(stub), [changed], Ct);

        Assert.Equal("pages:production:VITE_KEY", changed.VercelEnvIds!["production"]);
        Assert.NotNull(changed.SyncError);

        stub.Status = HttpStatusCode.NoContent;
        await sync.PushAsync(Context(stub), [changed], Ct);

        Assert.Equal(2, stub.Calls.Count(c => c == "DELETE /repos/acme/todo/actions/variables/VITE_KEY"));
        Assert.Null(changed.VercelEnvIds);
    }

    [Fact]
    public async Task FR145_S8_A_saved_public_database_url_never_leaves_AIDE_and_one_sent_earlier_is_removed()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<DeploymentAccess>();
        var sync = scope.ServiceProvider.GetRequiredService<DeploymentEnvSync>();
        var stub = new GitHubStub();
        var fresh = Var(access, "VITE_NEW", "postgres://u:p@h/d", isPublic: true, sensitive: false);
        var old = Var(access, "VITE_OLD", "postgres://u:p@h/d", isPublic: true, sensitive: false, new Dictionary<string, string> { ["production"] = "pages:production:VITE_OLD" });

        await sync.PushAsync(Context(stub), [fresh, old], Ct);

        Assert.Equal(["DELETE /repos/acme/todo/actions/variables/VITE_OLD"], stub.Calls);
        Assert.All([fresh, old], v =>
        {
            Assert.Null(v.VercelEnvIds);
            Assert.Null(v.SyncedAt);
            Assert.Contains("can't be public", v.SyncError, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task FR145_A_variable_that_became_sensitive_loses_its_id_and_the_repository_variable()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<DeploymentAccess>();
        var sync = scope.ServiceProvider.GetRequiredService<DeploymentEnvSync>();
        var stub = new GitHubStub();
        var changed = Var(access, "VITE_KEY", "s3cret", isPublic: true, sensitive: true, new Dictionary<string, string> { ["production"] = "pages:production:VITE_KEY" });

        await sync.PushAsync(Context(stub), [changed], Ct);

        Assert.Equal(["DELETE /repos/acme/todo/actions/variables/VITE_KEY"], stub.Calls);
        Assert.Null(changed.VercelEnvIds);
        Assert.NotNull(changed.SyncError);
    }
}
