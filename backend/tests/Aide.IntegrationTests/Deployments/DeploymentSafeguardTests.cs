using System.Net;
using System.Text.Json;
using Aide.Api.Features.Deployments;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Deployments;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aide.IntegrationTests.Deployments;

/// <summary>
/// FR-88 (connections are personal, §2), §4.15 create idempotency (no duplicate builds, orphans cancelled) and the §9 Fake
/// provider guard, against a stub Vercel API.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeploymentSafeguardTests(AideApiFactory factory)
{
    private static readonly object PatGit = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_deploySafeguardToken" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FR144_A_co_Owner_cant_set_up_a_target_with_the_binding_Owners_connection_but_can_with_their_own()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v9/projects/prj_other", HttpStatusCode.OK, """{"id":"prj_other","name":"other","link":{"type":"github","org":"acme","repo":"todo"}}""");
        var app = DeploymentHarness.Vercel(factory, vercel);
        using var fixture = await ProjectApi.FixtureAsync(app, git: PatGit);
        var id = fixture.Project.Id;
        await DeploymentHarness.SetStackAsync(fixture.Owner.Client, id, DeploymentHarness.VercelStack);
        var ana = fixture.Owner.User!.Id;
        var anas = await DeploymentHarness.SeedConnectionAsync(app, ana);
        await DeploymentHarness.BindAsync(app, id, anas, ana);
        var ben = fixture.Outsider;
        await ProjectApi.AddMemberInDbAsync(app, id, ben.User!.Id, ProjectRole.Owner);

        // With Ana's connection: refused, nothing queued, and Vercel is never called with her token.
        await Problems.AssertAsync(
            await DeploymentHarness.PutTargetAsync(ben.Client, id, "frontend", new { provider = "vercel", connectionId = anas, link = new { resourceId = "prj_other" } }),
            HttpStatusCode.Forbidden,
            ErrorCodes.CredentialNotOwner);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.Jobs.AnyAsync(j => j.ProjectId == id && j.Type == JobTypes.DeploymentSetup, Ct)));
        Assert.Empty(vercel.Requests);

        // With his own connection: allowed, and the binding becomes his.
        var bens = await DeploymentHarness.SeedConnectionAsync(app, ben.User!.Id, token: "vcp_bensOwnToken0123");
        await DeploymentHarness.SetUpTargetAsync(app, ben.Client, id, "frontend", new { provider = "vercel", connectionId = bens, link = new { resourceId = "prj_other" } });
        var target = Assert.Single((await DeploymentHarness.SettingsAsync(ben.Client, id)).Targets);
        Assert.Equal((bens, ben.User!.Id, "prj_other"), (target.Binding!.ConnectionId, target.Binding.BoundBy.Id, target.ResourceId));
        Assert.All(vercel.Requests, r => Assert.Equal("Bearer vcp_bensOwnToken0123", r.Authorization));

        // Ana (no longer the binder) can't reuse Ben's connection; either Owner may clear the target.
        await Problems.AssertAsync(
            await DeploymentHarness.PutTargetAsync(fixture.Owner.Client, id, "frontend", new { provider = "vercel", connectionId = bens, link = new { resourceId = "prj_todo" } }),
            HttpStatusCode.Forbidden,
            ErrorCodes.CredentialNotOwner);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Owner.Client.DeleteAsync(new Uri(DeploymentHarness.TargetPath(id, "frontend"), UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task FR88_Env_writes_promote_and_production_redeploy_are_Owner_only()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory, git: PatGit);
        var production = await DeploymentHarness.ScheduleAsync(
            factory, new Api.Features.Deployments.NewDeployment(fixture.Project.Id, fixture.Project.ReleaseVersionId!.Value, null, DeploymentEnvironment.Production, "main", new string('9', 40), DeploymentTrigger.Auto, null));

        await Problems.AssertAsync(
            await fixture.Contributor.Client.PutAsync(new Uri($"{fixture.Path}/settings/deployment/env/KEY", UriKind.Relative), Json("""{"value":"v","targets":["preview"]}"""), Ct),
            HttpStatusCode.Forbidden,
            ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(new Uri($"{fixture.Path}/deployments/{production.Id}/promote", UriKind.Relative), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(new Uri($"{fixture.Path}/deployments/{production.Id}/redeploy", UriKind.Relative), null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    private async Task<(WebApplicationFactory<Program> App, ApiSession Owner, Guid ProjectId, Guid VersionId)> BoundAsync(FakeGitHubApi vercel)
    {
        var app = DeploymentHarness.Vercel(factory, vercel);
        var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: PatGit);
        await DeploymentHarness.BindAsync(app, project.Id, await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id), owner.User!.Id);
        return (app, owner, project.Id, await DeploymentHarness.VersionIdAsync(app, project.Id));
    }

    [Fact]
    public async Task S4_15_A_create_that_timed_out_is_found_by_its_AIDE_id_on_retry_without_a_second_build()
    {
        Guid? aideId = null;
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v13/deployments", request =>
            {
                aideId = Guid.Parse(JsonDocument.Parse(request.Body).RootElement.GetProperty("meta").GetProperty("aideDeploymentId").GetString()!);
                return (HttpStatusCode.GatewayTimeout, """{"error":{"code":"timeout","message":"Gateway timeout"}}"""); // created anyway
            })
            .On(HttpMethod.Get, "/v6/deployments", _ => aideId is { } created
                ? (HttpStatusCode.OK, JsonSerializer.Serialize(new { deployments = new[] { new { uid = "dpl_made", state = "BUILDING", url = "todo-made.vercel.app", meta = new { aideDeploymentId = created.ToString() } } } }))
                : (HttpStatusCode.OK, """{"deployments":[]}"""))
            .On(HttpMethod.Get, "/v13/deployments/dpl_made", HttpStatusCode.OK, """{"id":"dpl_made","readyState":"READY","url":"todo-made.vercel.app"}""");
        var (app, owner, id, versionId) = await BoundAsync(vercel);
        using var _ = owner;
        var deployment = await DeploymentHarness.ScheduleAsync(app, new NewDeployment(id, versionId, null, DeploymentEnvironment.Preview, "aide/x", new string('6', 40), DeploymentTrigger.Auto, null));

        await DeploymentHarness.SettleAsync(app, (await DeploymentHarness.JobOfAsync(app, id, deployment.Id)).Id);

        Assert.Equal(deployment.Id, aideId);
        var done = await DeploymentHarness.DeploymentAsync(app, deployment.Id);
        Assert.Equal(("dpl_made", DeploymentStatus.Ready), (done.ProviderDeploymentId, done.Status));
        Assert.Equal(1, vercel.Count(HttpMethod.Post, "/v13/deployments"));
    }

    [Fact]
    public async Task S4_15_A_create_whose_row_was_cancelled_meanwhile_cancels_the_new_Vercel_deployment()
    {
        WebApplicationFactory<Program>? appRef = null;
        Guid deploymentId = default;
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v13/deployments", _ =>
            {
                // A newer preview stops this one while Vercel is creating it.
                ProjectApi.WithDbAsync(appRef!, db => db.Deployments.Where(d => d.Id == deploymentId)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeploymentStatus.Canceled), CancellationToken.None)).GetAwaiter().GetResult();
                return (HttpStatusCode.OK, """{"id":"dpl_orphan","readyState":"QUEUED"}""");
            })
            .On(HttpMethod.Patch, "/v12/deployments/dpl_orphan/cancel", HttpStatusCode.OK, """{"id":"dpl_orphan","readyState":"CANCELED"}""");
        var (app, owner, id, versionId) = await BoundAsync(vercel);
        appRef = app;
        using var _ = owner;
        var deployment = await DeploymentHarness.ScheduleAsync(app, new NewDeployment(id, versionId, null, DeploymentEnvironment.Preview, "aide/x", new string('7', 40), DeploymentTrigger.Auto, null));
        deploymentId = deployment.Id;
        var job = await DeploymentHarness.JobOfAsync(app, id, deployment.Id);

        Assert.False(await DeploymentHarness.RunOnceAsync(app, job.Id));

        Assert.Equal(1, vercel.Count(HttpMethod.Patch, "/v12/deployments/dpl_orphan/cancel"));
        var row = await DeploymentHarness.DeploymentAsync(app, deployment.Id);
        Assert.Equal((DeploymentStatus.Canceled, string.Empty), (row.Status, row.ProviderDeploymentId));

        // The retry sees the stopped row and creates nothing more.
        await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued), Ct));
        Assert.False(await DeploymentHarness.RunOnceAsync(app, job.Id));
        Assert.Equal(1, vercel.Count(HttpMethod.Post, "/v13/deployments"));
    }

    [Theory]
    [InlineData("Production", false, true, false)]
    [InlineData("Production", true, false, true)]
    [InlineData("Development", false, false, false)]
    public async Task S9_The_Fake_provider_guard_refuses_or_warns_outside_Development(string environment, bool allowFake, bool refuses, bool warns)
    {
        var logs = new LogSink();
        var guard = new FakeDeploymentProviderGuard(
            Options.Create(new DeploymentOptions { Provider = "Fake", AllowFake = allowFake }),
            new Env(environment),
            new CapturingLoggerProvider(logs).CreateLogger(nameof(FakeDeploymentProviderGuard)) is var logger ? new Logger(logger) : NullLogger<FakeDeploymentProviderGuard>.Instance);

        var started = await Record.ExceptionAsync(() => guard.StartAsync(Ct));

        Assert.Equal(refuses, started is InvalidOperationException);
        Assert.Equal(warns, logs.Entries.Any(e => e.Message.Contains("not for real use", StringComparison.Ordinal)));
    }

    private static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Aide.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Logger(Microsoft.Extensions.Logging.ILogger inner) : Microsoft.Extensions.Logging.ILogger<FakeDeploymentProviderGuard>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
