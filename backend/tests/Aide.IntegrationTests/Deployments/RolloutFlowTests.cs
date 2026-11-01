using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Deployments;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Release;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Review;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Deployments;

/// <summary>
/// FR-147, FR-148, FR-149, FR-154 end to end on the Fake deployment provider (§9): the preview rollout in dependency order
/// (backend, then the frontend built against it), a backend pre-deploy migration failure stopping the frontend, preview
/// database divergence and reset, and the production rollout shown on the release.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RolloutFlowTests(AideApiFactory factory)
{
    private static readonly object PatGit = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_rolloutFlowToken0001" };

    /// <summary>FR-147 example: React on Netlify, ASP.NET on Render, Render Postgres.</summary>
    private static readonly object NetlifyRenderStack = new
    {
        frontend = new { pack = "fe-react-vite", path = "frontend" },
        backend = new { pack = "be-aspnet", path = "backend" },
        data = new { pack = "data-efcore-postgres" },
        deploy = new { frontendHost = "deploy-netlify", backendHost = "deploy-render", databaseHost = "render-postgres" },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri U(string path) => new(path, UriKind.Relative);

    private WebApplicationFactory<Program> FakeApp() =>
        AgentJobs.WithResponders(DeploymentHarness.Fake(factory), new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.99m] }));

    private string WorkingCopy(Guid id) => Path.Combine(factory.WorkspacesRoot, id.ToString("D"));

    /// <summary>
    /// A Local project with a Vercel frontend and a Render backend target (Fake), in execution with a 2-step plan (step 2
    /// also writes <paramref name="extraFile"/>), started.
    /// </summary>
    private static async Task<ProjectFixture> ExecutingAsync(WebApplicationFactory<Program> app, string? extraFile = null)
    {
        var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        var owner = fixture.Owner.User!.Id;
        await DeploymentHarness.BindAsync(app, id, await DeploymentHarness.SeedConnectionAsync(app, owner), owner);
        var render = await DeploymentHarness.SeedConnectionAsync(app, owner, token: "rnd_seededKey0123", provider: PlatformProvider.Render);
        await ProjectApi.WithDbAsync(app, async db =>
        {
            var backend = DeploymentHarness.Target(id, render, owner, "fake_render_todo-api", DeploymentProvider.Render, "backend");
            backend.Component = DeploymentComponent.Backend;
            backend.PreviewResourceId = "fake_render_todo-api-preview";
            backend.PreviewUrl = "https://todo-api-backend-preview.render.test";
            db.DeploymentTargets.Add(backend);
            await db.SaveChangesAsync(Ct);
        });
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Execution, 4, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: "# Todo API\n\n## Summary\nManage todos.");
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, content: "# Spec\n\n## Technical Architecture\nNode.js service.\n\n## API Contracts\nNo HTTP API.\n");
        var plan = await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ImplementationPlan, content: AgentJobs.PlanMarkdown(2));
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.ExecutionSteps.AddRange(Enumerable.Range(1, 2).Select(n => new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = plan.Id,
                StepNumber = n,
                Title = $"Step {n}",
                Task = $"Build part {n}.",
                DeclaredFiles = n == 2 && extraFile is not null
                    ? [new DeclaredFile($"src/part{n}.js", FileAction.Create), new DeclaredFile(extraFile, FileAction.Create)]
                    : [new DeclaredFile($"src/part{n}.js", FileAction.Create)],
                Dependencies = n == 1 ? [] : [n - 1],
            }));
            await db.SaveChangesAsync(Ct);
        });
        await ProjectApi.ReadAsync<ExecutionStateDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);
        return fixture;
    }

    [Fact]
    public async Task FR147_FR149_Netlify_and_Render_preview_then_production_rollouts_deploy_the_backend_first_and_the_release_is_live_on_both()
    {
        var app = DeploymentHarness.Fake(factory);
        using var owner = await ApiSession.RegisterAsync(app);
        var id = (await ProjectApi.CreateAsync(owner.Client, git: PatGit)).Id;
        await DeploymentHarness.SetStackAsync(owner.Client, id, NetlifyRenderStack);
        var netlify = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "nfp_seededToken0123", provider: PlatformProvider.Netlify);
        var render = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "rnd_seededKey0123", provider: PlatformProvider.Render);
        await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "backend", new { provider = "render", connectionId = render, create = new { name = "todo-api" } });
        await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "frontend", new { provider = "netlify", connectionId = netlify, create = new { name = "todo" } });
        var versionId = await DeploymentHarness.VersionIdAsync(app, id);
        var sha = new string('a', 40);

        Guid rolloutId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var rollouts = scope.ServiceProvider.GetRequiredService<RolloutScheduler>();
            rolloutId = (await rollouts.EnqueuePreviewAsync(id, versionId, "aide/todo/v0.1.0", sha, DeploymentTrigger.Auto, null, DateTimeOffset.UtcNow, Ct))!.Id;
            await scope.ServiceProvider.GetRequiredService<AideDbContext>().SaveChangesAsync(Ct);
        }

        await DeploymentHarness.SettleRolloutAsync(app, id, rolloutId);

        // The backend's preview was ready before the frontend's started (built against the backend's preview URL).
        var rows = await DeploymentHarness.DeploymentsAsync(app, id);
        var backend = Assert.Single(rows, d => d.Component == DeploymentComponent.Backend);
        var frontend = Assert.Single(rows, d => d.Component == DeploymentComponent.Frontend);
        Assert.All(rows, d => Assert.Equal((DeploymentEnvironment.Preview, DeploymentStatus.Ready, (Guid?)rolloutId), (d.Environment, d.Status, d.RolloutId)));
        Assert.True(backend.ReadyAt <= frontend.CreatedAt);
        var backendTarget = await ProjectApi.WithDbAsync(app, db => db.DeploymentTargets.AsNoTracking().SingleAsync(t => t.ProjectId == id && t.Component == DeploymentComponent.Backend, Ct));
        Assert.NotNull(backendTarget.PreviewUrl);
        var derived = await ProjectApi.WithDbAsync(app, db => db.DeploymentEnvVars.AsNoTracking()
            .Where(v => v.ProjectId == id && v.Source == DeploymentEnvSource.Derived && (v.Key == "API_ORIGIN" || v.Key == "VITE_API_BASE_URL"))
            .ToListAsync(Ct));
        Assert.Contains(derived, v => v.Targets.Contains("preview"));

        var environment = await ProjectApi.ReadAsync<PreviewEnvironmentDto>(await owner.Client.GetAsync(U($"/api/projects/{id}/deployments/preview"), Ct));
        Assert.Equal(RolloutState.Succeeded, environment.Current!.State);
        Assert.Equal(
            [(RolloutStepKind.Backend, (DeploymentProvider?)DeploymentProvider.Render, (DeploymentStatus?)DeploymentStatus.Ready), (RolloutStepKind.Frontend, DeploymentProvider.Netlify, DeploymentStatus.Ready)],
            environment.Current.Steps.Select(s => (s.Step, s.Provider, s.Status)));
        Assert.All(environment.Current.Steps, s => Assert.NotNull(s.Url));
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.PreviewRolloutCompleted));

        // FR-149: the release's production rollout deploys both; the release shows where it is live; queuing it again does nothing.
        var merge = new string('b', 40);
        var release = new Domain.Entities.Release
        {
            ProjectId = id,
            ReleaseVersionId = versionId,
            Checkpoint = 6,
            Tag = "v0.1.0",
            MergeCommitSha = merge,
            Status = ReleaseStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.Releases.Add(release);
            await db.SaveChangesAsync(Ct);
        });
        Guid productionId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var rollouts = scope.ServiceProvider.GetRequiredService<RolloutScheduler>();
            productionId = (await rollouts.EnqueueProductionAsync(release, "main", DateTimeOffset.UtcNow, Ct))!.Id;
            await scope.ServiceProvider.GetRequiredService<AideDbContext>().SaveChangesAsync(Ct);
            Assert.Null(await rollouts.EnqueueProductionAsync(release, "main", DateTimeOffset.UtcNow, Ct));
        }

        await DeploymentHarness.SettleRolloutAsync(app, id, productionId);

        var released = await ProjectApi.ReadAsync<ReleaseDto>(await owner.Client.GetAsync(U($"/api/projects/{id}/releases/latest"), Ct));
        Assert.Equal(RolloutState.Succeeded, released.Rollout!.State);
        Assert.Equal(
            [(DeploymentComponent.Backend, "Render"), (DeploymentComponent.Frontend, "Netlify")],
            released.LiveOn!.Select(l => (l.Component, l.ProviderName)));
        Assert.All(released.LiveOn!, l => Assert.Contains(merge[..8], l.Url, StringComparison.Ordinal));
        var production = (await DeploymentHarness.DeploymentsAsync(app, id)).Where(d => d.Environment == DeploymentEnvironment.Production).ToList();
        Assert.Equal(2, production.Count);
        Assert.All(production, d => Assert.Equal((release.Id, DeploymentTrigger.Auto, (Guid?)productionId), (d.ReleaseId, d.Trigger, d.RolloutId)));
    }

    [Fact]
    public async Task FR147_FR154_A_backend_pre_deploy_migration_failure_never_starts_the_frontend_and_is_a_critical_finding()
    {
        var app = FakeApp();
        using var fixture = await ExecutingAsync(app, extraFile: FakeDeploymentRules.FailMigrationMarker);
        var id = fixture.Project.Id;
        await AgentJobs.RunQueuedAsync(app, id);
        var rollout = await DeploymentHarness.RolloutJobAsync(app, id);

        await DeploymentHarness.SettleRolloutAsync(app, id, rollout.Id);

        var backend = Assert.Single(await DeploymentHarness.DeploymentsAsync(app, id));
        Assert.Equal((DeploymentComponent.Backend, DeploymentStatus.Error, ErrorCodes.MigrationFailed), (backend.Component, backend.Status, backend.ErrorCode));
        Assert.Contains("Pre-deploy", backend.BuildLogTail, StringComparison.Ordinal);
        var completed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.PreviewRolloutCompleted)).Payload);
        Assert.Equal(("failed", ErrorCodes.MigrationFailed), (completed.GetProperty("state").GetString(), completed.GetProperty("errorCode").GetString()));

        var environment = await ProjectApi.ReadAsync<PreviewEnvironmentDto>(await fixture.Viewer.Client.GetAsync(U($"{fixture.Path}/deployments/preview"), Ct));
        Assert.Equal(
            [(RolloutStepKind.Backend, RolloutStepState.Failed, true), (RolloutStepKind.Frontend, RolloutStepState.Pending, false)],
            environment.Current!.Steps.Select(s => (s.Step, s.State, s.CanRedeploy)));

        await DeploymentHarness.SettleAsync(app, (await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
            .SingleAsync(j => j.ProjectId == id && j.Type == JobTypes.Review && j.Status == JobStatus.Queued, Ct))).Id);
        var report = await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        var finding = Assert.Single(report.Violations, v => v.Message == RolloutFindings.PreDeployFailedMessage);
        Assert.Equal((ReviewCategory.Architecture, Severity.Critical, FindingSource.Deterministic), (finding.Category, finding.Severity, finding.Source));
        Assert.Contains("Pre-deploy", finding.Evidence, StringComparison.Ordinal);
    }
    [Fact]
    public async Task FR148_A_rollback_before_migration_0003_blocks_the_next_preview_with_PREVIEW_DATABASE_DIVERGED_until_an_Owner_resets()
    {
        var app = FakeApp();
        using var fixture = await ExecutingAsync(app, extraFile: "backend/Migrations/20261031000000_0003_Orders.cs");
        var id = fixture.Project.Id;
        await AgentJobs.RunQueuedAsync(app, id);
        await DeploymentHarness.SettleRolloutAsync(app, id, (await DeploymentHarness.RolloutJobAsync(app, id)).Id);
        Assert.Contains(await DeploymentHarness.DeploymentsAsync(app, id), d => d.Component == DeploymentComponent.Backend && d.Status == DeploymentStatus.Ready);

        // A rollback to the checkpoint before migration 0003 (the branch loses the migration the preview database applied).
        var branch = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == id).Select(v => v.BranchName!).SingleAsync(Ct));
        await TestGit.RunAsync(WorkingCopy(id), Ct, "update-ref", $"refs/heads/{branch}", $"refs/heads/{branch}~1");

        var environment = await ProjectApi.ReadAsync<PreviewEnvironmentDto>(await fixture.Viewer.Client.GetAsync(U($"{fixture.Path}/deployments/preview"), Ct));
        Assert.True(environment.DatabaseDiverged);
        Assert.Contains("0003_Orders", environment.DivergedMessage, StringComparison.Ordinal);
        var preview = U($"{fixture.Path}/deployments/preview");
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(preview, null, Ct), HttpStatusCode.Conflict, ErrorCodes.PreviewDatabaseDiverged);

        // Only an Owner resets; the reset clears the divergence and the preview rolls out again.
        var reset = U($"{fixture.Path}/settings/deployment/databases/preview/reset");
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(reset, null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        var queued = await ProjectApi.ReadAsync<Aide.Api.Features.Databases.PreviewDatabaseResetDto>(await fixture.Owner.Client.PostAsync(reset, null, Ct), HttpStatusCode.Accepted);
        Assert.Equal(PreviewResetMethod.Manual, queued.Method); // Render backend without a database card: recorded, emptied on the platform
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(reset, null, Ct), HttpStatusCode.Conflict, ErrorCodes.InvalidState);

        // While the reset is queued, a preview rollout is refused too (both sides queue under the project lock).
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(preview, null, Ct), HttpStatusCode.Conflict, ErrorCodes.InvalidState);
        await DeploymentHarness.SettleAsync(app, queued.JobId);
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.DatabaseReset));

        Assert.False((await ProjectApi.ReadAsync<PreviewEnvironmentDto>(await fixture.Viewer.Client.GetAsync(preview, Ct))).DatabaseDiverged);
        var rollout = await ProjectApi.ReadAsync<RolloutDto>(await fixture.Contributor.Client.PostAsync(preview, null, Ct), HttpStatusCode.Accepted);
        Assert.Equal(RolloutState.Running, rollout.State);

        // A reset while a rollout runs is INVALID_STATE.
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsync(reset, null, Ct), HttpStatusCode.Conflict, ErrorCodes.InvalidState);
    }
}
