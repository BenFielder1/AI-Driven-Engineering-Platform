using System.Reflection;
using System.Runtime.ExceptionServices;
using Aide.Api.Features.Deployments;
using Aide.Api.Features.Projects;
using Aide.Domain.Entities;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Deployments;
using Aide.IntegrationTests.Releases;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Deployments;

/// <summary>
/// FR-145, FR-146: GitHub Pages builds on the merge push, so its pending variables go before the merge (or as the release
/// enters <c>awaiting_merge</c>), and a build that started before a later sync is replaced. Fake deployments and Local Git.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitHubPagesBuildRefreshTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The Fake provider, with <see cref="IDeploymentProvider.RefreshBuildAsync"/> answering from a script.</summary>
    public class ScriptedProvider : DispatchProxy
    {
        public IDeploymentProvider Inner { get; set; } = null!;

        public Queue<BuildRefresh> Script { get; set; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IDeploymentProvider.RefreshBuildAsync))
            {
                return Task.FromResult(Script.TryDequeue(out var next) ? next : BuildRefresh.UpToDate);
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                throw;
            }
        }
    }

    private sealed class ScriptedResolver(IDeploymentProviderResolver inner, Queue<BuildRefresh> script) : IDeploymentProviderResolver
    {
        public bool UsesFake => inner.UsesFake;

        public IVercelClient Vercel => inner.Vercel;

        public IDeploymentProvider ForPlatform(DeploymentProvider platform)
        {
            var proxy = DispatchProxy.Create<IDeploymentProvider, ScriptedProvider>();
            var scripted = (ScriptedProvider)(object)proxy;
            scripted.Inner = inner.ForPlatform(platform);
            scripted.Script = script;
            return proxy;
        }
    }

    private static Task<DeploymentEnvVar> SeedPendingAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, async db =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var access = scope.ServiceProvider.GetRequiredService<DeploymentAccess>();
            db.DeploymentTargets.Add(DeploymentHarness.Target(projectId, null, null, "acme/todo", DeploymentProvider.GithubPages));
            var row = new DeploymentEnvVar
            {
                ProjectId = projectId,
                Key = "VITE_API_BASE_URL",
                ValueEncrypted = access.Values.Protect("https://api.example.test"),
                Public = true,
                Targets = ["production"],
                AppliesTo = ["frontend"],
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.DeploymentEnvVars.Add(row);
            await db.SaveChangesAsync(Ct);
            return row;
        });

    private static Task<DeploymentEnvVar> VarAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.DeploymentEnvVars.AsNoTracking().SingleAsync(v => v.Id == id, Ct));

    [Fact]
    public async Task FR145_FR146_A_manual_release_pushes_pending_Pages_variables_as_it_enters_awaiting_merge()
    {
        var app = AgentJobs.WithResponders(DeploymentHarness.Fake(factory), new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));
        using var fixture = await ReleaseFlowTests.ReleasableAsync(app);
        var id = fixture.Project.Id;
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        var pending = await SeedPendingAsync(app, id);

        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var release = await ProjectApi.WithDbAsync(app, db => db.Releases.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Equal(ReleaseStatus.AwaitingMerge, release.Status);
        var pushed = await VarAsync(app, pending.Id);
        Assert.NotNull(pushed.SyncedAt);
        Assert.Null(pushed.SyncError);
    }

    [Fact]
    public async Task FR146_The_deployment_syncs_first_and_waits_while_a_build_that_predates_the_variables_is_replaced()
    {
        var script = new Queue<BuildRefresh>([BuildRefresh.Stopping, BuildRefresh.Restarted]);
        var app = DeploymentHarness.Fake(factory).WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IDeploymentProviderResolver>(sp => new ScriptedResolver(ActivatorUtilities.CreateInstance<DeploymentProviderResolver>(sp), script))));
        var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client);
        var pending = await SeedPendingAsync(app, project.Id);
        var versionId = await DeploymentHarness.VersionIdAsync(app, project.Id);
        var deployment = await DeploymentHarness.ScheduleAsync(
            app,
            new NewDeployment(project.Id, versionId, null, DeploymentEnvironment.Production, "main", new string('a', 40), DeploymentTrigger.Auto, null));
        var job = await DeploymentHarness.JobOfAsync(app, project.Id, deployment.Id);

        Assert.True(await DeploymentHarness.RunOnceAsync(app, job.Id));
        Assert.NotNull((await VarAsync(app, pending.Id)).SyncedAt);
        Assert.Empty((await DeploymentHarness.DeploymentAsync(app, deployment.Id)).ProviderDeploymentId);

        Assert.True(await DeploymentHarness.RunOnceAsync(app, job.Id));
        Assert.Empty((await DeploymentHarness.DeploymentAsync(app, deployment.Id)).ProviderDeploymentId);

        await DeploymentHarness.SettleAsync(app, job.Id);
        Assert.Empty(script);
        var followed = await DeploymentHarness.DeploymentAsync(app, deployment.Id);
        Assert.NotEmpty(followed.ProviderDeploymentId);
        Assert.Equal(DeploymentStatus.Ready, followed.Status);
    }
}
