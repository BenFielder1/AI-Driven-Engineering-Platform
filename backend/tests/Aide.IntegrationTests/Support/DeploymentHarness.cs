using Aide.Api.Features.Deployments;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Infrastructure.Deployments;
using Aide.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Support;

/// <summary>
/// FR-87–FR-91 test helpers: the app with the Fake deployment provider (<c>Deployments:Provider=Fake</c>) or with Vercel
/// answered by a stub handler (never api.vercel.com), seeded connections and bindings, and driving deployment jobs.
/// </summary>
public static class DeploymentHarness
{
    public const string VercelProjectId = "prj_todo";
    public const string VercelProjectName = "todo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The app with every deployment on the Fake provider.</summary>
    public static WebApplicationFactory<Program> Fake(WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(b => b.UseSetting("Deployments:Provider", "Fake"));

    /// <summary>The app whose <c>vercel</c> HttpClient is answered by <paramref name="vercel"/> (an in-memory stub).</summary>
    public static WebApplicationFactory<Program> Vercel(WebApplicationFactory<Program> factory, FakeGitHubApi vercel) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHttpClient(VercelDeploymentProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => vercel)));

    /// <summary>A valid connection of <paramref name="userId"/> whose token is <paramref name="token"/> (encrypted as in production).</summary>
    public static async Task<Guid> SeedConnectionAsync(WebApplicationFactory<Program> app, Guid userId, string token = "vcp_seededToken0123456789", string? teamId = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var protector = app.Services.GetRequiredService<ISecretProtectors>().ForPurpose(SecretPurposes.PlatformToken);
        var connection = new PlatformConnection
        {
            UserId = userId,
            Provider = PlatformProvider.Vercel,
            Label = "Personal",
            SecretEncrypted = protector.Protect(token),
            Last4 = token[^4..],
            AccountId = "usr_seed",
            AccountName = "seed-user",
            Metadata = new PlatformConnectionMetadata { TeamId = teamId, TeamSlug = teamId is null ? null : "acme-team" },
            Status = PlatformConnectionStatus.Valid,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.PlatformConnections.Add(connection);
            await db.SaveChangesAsync(Ct);
        });
        return connection.Id;
    }

    /// <summary>A frontend deployment target (v2 settings) bound to <paramref name="connectionId"/> and linked to the Vercel project.</summary>
    public static Task BindAsync(
        WebApplicationFactory<Program> app, Guid projectId, Guid connectionId, Guid ownerId, string? vercelProjectId = VercelProjectId, DeploymentProvider provider = DeploymentProvider.Vercel) =>
        ProjectApi.WithDbAsync(app, async db =>
        {
            db.DeploymentTargets.Add(Target(projectId, connectionId, ownerId, vercelProjectId, provider));
            await db.SaveChangesAsync(Ct);
        });

    /// <summary>A frontend target as v2's Vercel flow leaves it (status from <see cref="DeploymentTargetRules"/>).</summary>
    public static DeploymentTarget Target(Guid projectId, Guid? connectionId, Guid? ownerId, string? vercelProjectId, DeploymentProvider provider = DeploymentProvider.Vercel, string rootDirectory = ".")
    {
        var target = new DeploymentTarget
        {
            ProjectId = projectId,
            Component = DeploymentComponent.Frontend,
            Provider = provider,
            ConnectionId = connectionId,
            BoundByUserId = ownerId,
            ResourceId = vercelProjectId,
            Name = vercelProjectId is null ? null : VercelProjectName,
            RootDirectory = rootDirectory,
            BuildConfig = new DeploymentBuildConfig { Framework = DeploymentTarget.DefaultFramework },
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        DeploymentTargetRules.Refresh(target);
        return target;
    }

    /// <summary>The project's frontend target (v2's deployment settings).</summary>
    public static Task<DeploymentTarget> FrontendTargetAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.DeploymentTargets.AsNoTracking()
            .SingleAsync(t => t.ProjectId == projectId && t.Component == DeploymentComponent.Frontend, Ct));

    /// <summary>Records a deployment through the scheduler (row, job, event), as the PR job or an endpoint would.</summary>
    public static async Task<Deployment> ScheduleAsync(WebApplicationFactory<Program> app, NewDeployment spec, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        await using var scope = app.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<DeploymentScheduler>();
        var deployment = await scheduler.AddAsync(spec, "test", now ?? DateTimeOffset.UtcNow, Ct);
        await scope.ServiceProvider.GetRequiredService<Aide.Infrastructure.Persistence.AideDbContext>().SaveChangesAsync(Ct);
        return deployment;
    }

    /// <summary>The <c>deployment</c> job of <paramref name="deploymentId"/>.</summary>
    public static Task<Job> JobOfAsync(WebApplicationFactory<Program> app, Guid projectId, Guid deploymentId) =>
        ProjectApi.WithDbAsync(app, async db =>
            (await db.Jobs.AsNoTracking().Where(j => j.ProjectId == projectId && j.Type == JobTypes.Deployment).ToListAsync(Ct))
                .Single(j => DeploymentScheduler.TryPayload(j.Payload)?.DeploymentId == deploymentId));

    /// <summary>Runs a queued job once (ignoring its run-after); <c>true</c> when it deferred itself again.</summary>
    public static async Task<bool> RunOnceAsync(WebApplicationFactory<Program> app, Guid jobId)
    {
        var job = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct));
        Assert.Equal(JobStatus.Queued, job.Status);
        return await AgentJobs.RunCoreAsync(app, job);
    }

    /// <summary>Runs a job until it stops deferring itself (at most <paramref name="maxRuns"/> runs); returns the runs.</summary>
    public static async Task<int> SettleAsync(WebApplicationFactory<Program> app, Guid jobId, int maxRuns = 10)
    {
        for (var run = 1; run <= maxRuns; run++)
        {
            if (!await RunOnceAsync(app, jobId))
            {
                return run;
            }
        }

        Assert.Fail($"Job {jobId} still deferred after {maxRuns} runs.");
        return maxRuns;
    }

    public static Task<Deployment> DeploymentAsync(WebApplicationFactory<Program> app, Guid deploymentId) =>
        ProjectApi.WithDbAsync(app, db => db.Deployments.AsNoTracking().SingleAsync(d => d.Id == deploymentId, Ct));

    public static Task<List<Deployment>> DeploymentsAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.Deployments.AsNoTracking().Where(d => d.ProjectId == projectId).OrderBy(d => d.CreatedAt).ToListAsync(Ct));

    /// <summary>The release version id of a project's version in development.</summary>
    public static Task<Guid> VersionIdAsync(WebApplicationFactory<Program> app, Guid projectId) =>
        ProjectApi.WithDbAsync(app, db => db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.CurrentReleaseVersionId!.Value).SingleAsync(Ct));
}
