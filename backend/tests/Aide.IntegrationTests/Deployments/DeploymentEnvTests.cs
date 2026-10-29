using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Deployments;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Deployments;

/// <summary>
/// FR-88, §4.15 env sync against a stub Vercel API: one Vercel var per (key, target), updates by stored id, deletes in
/// Vercel, failures kept as <c>syncError</c>, and the value never returned, logged or stored in clear.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeploymentEnvTests(AideApiFactory factory)
{
    private const string Secret = "service-role-0123456789-very-secret";

    private static readonly object PatGit = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_deployEnvToken" };

    private static readonly string[] PreviewOnly = ["preview"];
    private static readonly string[] ProductionOnly = ["production"];
    private static readonly string[] Both = ["preview", "production"];
    private static readonly string[] BothReversed = ["production", "preview"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Env(Guid projectId, string key) => $"/api/projects/{projectId}/settings/deployment/env/{key}";

    /// <summary>Vercel answering env creates with an id per (key, target) like <c>env_KEY_target</c>.</summary>
    private static FakeGitHubApi EnvVercel() => new FakeGitHubApi()
        .On(HttpMethod.Post, $"/v10/projects/{DeploymentHarness.VercelProjectId}/env", request =>
        {
            var created = JsonDocument.Parse(request.Body).RootElement.EnumerateArray()
                .Select(e => new
                {
                    id = $"env_{e.GetProperty("key").GetString()}_{e.GetProperty("target")[0].GetString()}",
                    key = e.GetProperty("key").GetString(),
                    target = new[] { e.GetProperty("target")[0].GetString() },
                    type = e.GetProperty("type").GetString(),
                })
                .ToList();
            return (HttpStatusCode.Created, JsonSerializer.Serialize(new { created, failed = Array.Empty<object>() }));
        });

    private async Task<(WebApplicationFactory<Program> App, ApiSession Owner, Guid ProjectId)> BoundAsync(FakeGitHubApi vercel)
    {
        var app = DeploymentHarness.Vercel(factory, vercel);
        var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        await DeploymentHarness.BindAsync(app, project.Id, await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id), owner.User!.Id);
        return (app, owner, project.Id);
    }

    [Fact]
    public async Task FR88_S4_15_Saving_an_env_var_pushes_one_sensitive_Vercel_var_per_target_and_never_returns_or_logs_the_value()
    {
        var vercel = EnvVercel();
        var (app, owner, id) = await BoundAsync(vercel);
        using var _ = owner;

        var response = await owner.Client.PutAsJsonAsync(Env(id, "SUPABASE_SERVICE_ROLE_KEY"), new { value = Secret, targets = BothReversed, sensitive = true }, Ct);

        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        var dto = JsonSerializer.Deserialize<DeploymentEnvVarDto>(text, ApiSession.Json)!;
        Assert.Equal((true, true, (string?)null), (dto.HasValue, dto.Sensitive, dto.SyncError));
        Assert.Equal([DeploymentEnvironment.Preview, DeploymentEnvironment.Production], dto.Targets);
        Assert.NotNull(dto.SyncedAt);

        var post = Assert.Single(vercel.Requests, r => r.Method == "POST");
        Assert.Contains("upsert=true", post.Query, StringComparison.Ordinal);
        var sent = JsonDocument.Parse(post.Body).RootElement.EnumerateArray().ToList();
        Assert.Equal(["preview", "production"], sent.Select(e => e.GetProperty("target")[0].GetString()).ToArray());
        Assert.All(sent, e => Assert.Equal(("sensitive", Secret), (e.GetProperty("type").GetString(), e.GetProperty("value").GetString())));

        var row = await ProjectApi.WithDbAsync(app, db => db.DeploymentEnvVars.AsNoTracking().SingleAsync(v => v.ProjectId == id, Ct));
        Assert.DoesNotContain(Secret, row.ValueEncrypted, StringComparison.Ordinal);
        Assert.Equal("env_SUPABASE_SERVICE_ROLE_KEY_production", row.VercelEnvIds!["production"]);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(await LifecycleSeed.EventsAsync(app, id, EventTypes.SettingsUpdated), e => e.Payload.Contains(Secret, StringComparison.Ordinal));

        var listed = await owner.Client.GetStringAsync(new Uri($"/api/projects/{id}/settings/deployment/env", UriKind.Relative), Ct);
        Assert.DoesNotContain(Secret, listed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR88_S4_15_Changing_targets_updates_by_stored_id_and_deletes_the_dropped_target_in_Vercel()
    {
        var vercel = EnvVercel()
            .On(HttpMethod.Patch, "/v9/projects/prj_todo/env/env_API_URL_preview", HttpStatusCode.OK, """{"id":"env_API_URL_preview"}""")
            .On(HttpMethod.Delete, "/v9/projects/prj_todo/env/env_API_URL_production", HttpStatusCode.OK, "{}");
        var (app, owner, id) = await BoundAsync(vercel);
        using var _ = owner;
        await ProjectApi.ReadAsync<DeploymentEnvVarDto>(await owner.Client.PutAsJsonAsync(Env(id, "API_URL"), new { value = "https://a", targets = Both }, Ct));

        var narrowed = await ProjectApi.ReadAsync<DeploymentEnvVarDto>(await owner.Client.PutAsJsonAsync(Env(id, "API_URL"), new { value = "https://b", targets = PreviewOnly }, Ct));

        Assert.Equal([DeploymentEnvironment.Preview], narrowed.Targets);
        var patch = Assert.Single(vercel.Requests, r => r.Method == "PATCH");
        var body = JsonDocument.Parse(patch.Body).RootElement;
        Assert.Equal(("https://b", "encrypted", "preview"), (body.GetProperty("value").GetString(), body.GetProperty("type").GetString(), body.GetProperty("target")[0].GetString()));
        Assert.Single(vercel.Requests, r => r.Method == "DELETE" && r.Path.EndsWith("env_API_URL_production", StringComparison.Ordinal));
        var row = await ProjectApi.WithDbAsync(app, db => db.DeploymentEnvVars.AsNoTracking().SingleAsync(v => v.ProjectId == id, Ct));
        Assert.Equal(["preview"], row.VercelEnvIds!.Keys.ToArray());
    }

    [Fact]
    public async Task FR88_Deleting_deletes_in_Vercel_and_a_Vercel_failure_deletes_nothing()
    {
        var failDelete = true;
        var vercel = EnvVercel()
            .On(HttpMethod.Delete, "/v9/projects/prj_todo/env/env_FLAG_preview", _ => failDelete
                ? (HttpStatusCode.InternalServerError, """{"error":{"code":"internal","message":"boom"}}""")
                : (HttpStatusCode.OK, "{}"));
        var (app, owner, id) = await BoundAsync(vercel);
        using var _ = owner;
        await ProjectApi.ReadAsync<DeploymentEnvVarDto>(await owner.Client.PutAsJsonAsync(Env(id, "FLAG"), new { value = "on", targets = PreviewOnly }, Ct));

        await Problems.AssertAsync(await owner.Client.DeleteAsync(new Uri(Env(id, "FLAG"), UriKind.Relative), Ct), HttpStatusCode.BadGateway, ErrorCodes.UpstreamError);
        Assert.True(await ProjectApi.WithDbAsync(app, db => db.DeploymentEnvVars.AnyAsync(v => v.ProjectId == id && v.Key == "FLAG", Ct)));

        failDelete = false;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync(new Uri(Env(id, "FLAG"), UriKind.Relative), Ct)).StatusCode);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.DeploymentEnvVars.AnyAsync(v => v.ProjectId == id, Ct)));
        await Problems.AssertAsync(await owner.Client.DeleteAsync(new Uri(Env(id, "FLAG"), UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR88_A_failed_push_keeps_the_value_with_syncError_and_answers_502_UPSTREAM_ERROR()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v10/projects/prj_todo/env", HttpStatusCode.ServiceUnavailable, """{"error":{"code":"unavailable","message":"try later"}}""");
        var (app, owner, id) = await BoundAsync(vercel);
        using var _ = owner;

        await Problems.AssertAsync(
            await owner.Client.PutAsJsonAsync(Env(id, "API_URL"), new { value = "https://a", targets = ProductionOnly }, Ct), HttpStatusCode.BadGateway, ErrorCodes.UpstreamError);

        var row = await ProjectApi.WithDbAsync(app, db => db.DeploymentEnvVars.AsNoTracking().SingleAsync(v => v.ProjectId == id, Ct));
        Assert.Equal(((DateTimeOffset?)null, true), (row.SyncedAt, row.SyncError is { Length: > 0 }));
        var listed = await ProjectApi.ReadAsync<List<DeploymentEnvVarDto>>(await owner.Client.GetAsync(new Uri($"/api/projects/{id}/settings/deployment/env", UriKind.Relative), Ct));
        Assert.NotNull(Assert.Single(listed).SyncError);
    }

    [Fact]
    public async Task FR88_Without_a_bound_connection_the_value_is_saved_in_AIDE_only()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);

        var saved = await ProjectApi.ReadAsync<DeploymentEnvVarDto>(await owner.Client.PutAsJsonAsync(Env(project.Id, "API_URL"), new { value = "https://a", targets = PreviewOnly }, Ct));

        Assert.Equal(((DateTimeOffset?)null, (string?)null), (saved.SyncedAt, saved.SyncError));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync(new Uri(Env(project.Id, "API_URL"), UriKind.Relative), Ct)).StatusCode);
    }
}
