using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Deployments;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Release;
using Aide.Api.Features.Review;
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
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Deployments;

/// <summary>
/// FR-90, FR-91, §4.15 end to end with the Fake LLM, Local Git and the Fake deployment provider (§9): execution opens the
/// PR and records a preview, review waits for it, a failed build is a critical finding, the release deploys production;
/// plus the deployment job against a stub Vercel API (build-log tail, adoption, rate limits, timeout, promote).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeploymentFlowTests(AideApiFactory factory)
{
    private static readonly string[] Preview = ["preview"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> FakeApp(decimal score = 0.93m) =>
        AgentJobs.WithResponders(DeploymentHarness.Fake(factory), new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [score] }));

    /// <summary>A Local project bound to the Fake provider, in execution with a 2-step plan (step 2 writes <paramref name="extraFile"/>), started.</summary>
    private static async Task<ProjectFixture> ExecutingAsync(WebApplicationFactory<Program> app, string? extraFile = null)
    {
        var fixture = await ProjectApi.FixtureAsync(app);
        var id = fixture.Project.Id;
        await DeploymentHarness.BindAsync(app, id, await DeploymentHarness.SeedConnectionAsync(app, fixture.Owner.User!.Id), fixture.Owner.User!.Id);
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

    private static Task<Job> ReviewJobAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.ProjectId == id && j.Type == JobTypes.Review && j.Status == JobStatus.Queued, Ct));

    private static Task<ComplianceReport> ReportAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));

    [Fact]
    public async Task FR90_S4_15_Execution_records_a_Fake_preview_review_waits_for_READY_then_scores_with_it()
    {
        var app = FakeApp();
        using var fixture = await ExecutingAsync(app);
        var id = fixture.Project.Id;

        // Execution, PR (Local comparison) and the advance to review run; the review and the deployment wait.
        await AgentJobs.RunQueuedAsync(app, id);
        var preview = Assert.Single(await DeploymentHarness.DeploymentsAsync(app, id));
        Assert.Equal((DeploymentEnvironment.Preview, DeploymentTrigger.Auto, DeploymentProvider.Fake), (preview.Environment, preview.Trigger, preview.Provider));
        var version = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == id, Ct));
        Assert.Equal(version.BranchName, preview.GitRef);
        Assert.Equal(40, preview.GitSha.Length);
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase);

        // Review waits (not a failed attempt) while the preview builds.
        var review = await ReviewJobAsync(app, id);
        Assert.True(await DeploymentHarness.RunOnceAsync(app, review.Id));
        var waiting = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == review.Id, Ct));
        Assert.Equal((JobStatus.Queued, 0), (waiting.Status, waiting.Attempts));
        Assert.Contains("previewWaitSince", waiting.Payload, StringComparison.Ordinal);
        Assert.Empty(await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.Where(r => r.ProjectId == id).ToListAsync(Ct)));

        // Fake: queued on create (that first run happened above), building on the first poll, ready on the second.
        var job = await DeploymentHarness.JobOfAsync(app, id, preview.Id);
        Assert.Equal(2, await DeploymentHarness.SettleAsync(app, job.Id));
        var ready = await DeploymentHarness.DeploymentAsync(app, preview.Id);
        Assert.Equal((DeploymentStatus.Ready, FakeUrl(preview.GitSha)), (ready.Status, ready.Url));
        Assert.NotNull(ready.ReadyAt);
        Assert.NotEmpty(await LifecycleSeed.EventsAsync(app, id, EventTypes.DeploymentPreviewReady));
        var updates = (await LifecycleSeed.EventsAsync(app, id, EventTypes.DeploymentUpdated)).Select(e => LifecycleSeed.Payload(e.Payload).GetProperty("status").GetString()).ToList();
        Assert.Equal(["building", "ready"], updates);

        // Now review scores, with the preview stored on the report and no preview finding.
        Assert.False(await DeploymentHarness.RunOnceAsync(app, review.Id));
        var report = await ReportAsync(app, id);
        var checks = JsonDocument.Parse(report.ExternalChecks!).RootElement.GetProperty("preview");
        Assert.Equal(("ready", preview.Id.ToString()), (checks.GetProperty("status").GetString(), checks.GetProperty("deploymentId").GetString()));
        Assert.DoesNotContain(report.Violations, v => v.Message == "Vercel preview build failed");
        var dto = await ProjectApi.ReadAsync<ComplianceReportDetailDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/compliance/reports/1", UriKind.Relative), Ct));
        Assert.Equal((DeploymentStatus.Ready, FakeUrl(preview.GitSha)), (dto.ExternalChecks!.Preview!.Status, dto.ExternalChecks.Preview.Url));
        Assert.Equal(ReviewDecision.ReleaseEligible, report.Decision);

        // The project state and the Deployments page show it.
        var state = await ProjectApi.ReadAsync<ProjectStateDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/state", UriKind.Relative), Ct));
        Assert.Equal(preview.Id, state.LatestDeployment!.Id);
        var listed = await ProjectApi.ReadAsync<List<DeploymentDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/deployments?target=preview", UriKind.Relative), Ct));
        Assert.Equal((preview.Id, "0.1.0", (DeploymentUserDto?)null), (Assert.Single(listed).Id, listed[0].ReleaseVersion, listed[0].TriggeredBy));

        // FR-90: a Contributor deploys the branch head again on request; it replaces the older one for polling.
        var manual = await ProjectApi.ReadAsync<DeploymentDto>(await fixture.Contributor.Client.PostAsync(new Uri($"{fixture.Path}/deployments/preview", UriKind.Relative), null, Ct), HttpStatusCode.Accepted);
        Assert.Equal((DeploymentTrigger.Manual, DeploymentStatus.Queued, fixture.Contributor.User!.Id, preview.GitSha), (manual.Trigger, manual.Status, manual.TriggeredBy!.Id, manual.GitSha));
    }

    [Fact]
    public async Task FR90_FR91_A_Fake_build_with_aide_fake_fail_build_is_a_critical_finding_and_a_failed_production_deployment_keeps_the_release()
    {
        // A high agent score: the decision can still be release_eligible when the score clears the threshold (FR-90).
        var app = FakeApp(score: 0.99m);
        using var fixture = await ExecutingAsync(app, extraFile: FakeDeploymentRules.FailBuildMarker);
        var id = fixture.Project.Id;
        await AgentJobs.RunQueuedAsync(app, id);
        var preview = Assert.Single(await DeploymentHarness.DeploymentsAsync(app, id));
        await DeploymentHarness.SettleAsync(app, (await DeploymentHarness.JobOfAsync(app, id, preview.Id)).Id);

        var failed = await DeploymentHarness.DeploymentAsync(app, preview.Id);
        Assert.Equal((DeploymentStatus.Error, ErrorCodes.VercelError), (failed.Status, failed.ErrorCode));
        Assert.Contains("Type error", failed.BuildLogTail, StringComparison.Ordinal);
        Assert.Empty(await LifecycleSeed.EventsAsync(app, id, EventTypes.DeploymentFailed)); // previews don't notify Owners

        await DeploymentHarness.SettleAsync(app, (await ReviewJobAsync(app, id)).Id);
        var report = await ReportAsync(app, id);
        var finding = Assert.Single(report.Violations, v => v.Message == "Vercel preview build failed");
        Assert.Equal((ReviewCategory.Architecture, Severity.Critical, FindingSource.Deterministic), (finding.Category, finding.Severity, finding.Source));
        Assert.Contains("Type error", finding.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Violations, v => v.Message.Contains("(FR-89)", StringComparison.Ordinal)); // FR-89: never with the Fake provider
        var architecture = report.CategoryScores.Single(c => c.Category == ReviewCategory.Architecture);
        Assert.Equal(0.20m, architecture.AgentScore - architecture.Score);
        Assert.Equal(ReviewDecision.ReleaseEligible, report.Decision);

        // Release: merged and tagged; the production deployment of the merge commit fails, and the release stands.
        var gate = Assert.Single(await LifecycleSeed.GatesAsync(app, id), g => g.Status == GateStatus.Pending);
        await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve");
        await AgentJobs.RunQueuedAsync(app, id);
        var production = Assert.Single(await DeploymentHarness.DeploymentsAsync(app, id), d => d.Environment == DeploymentEnvironment.Production);
        await DeploymentHarness.SettleAsync(app, (await DeploymentHarness.JobOfAsync(app, id, production.Id)).Id);

        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal(ProjectStatus.Released, project.Status);
        var release = await ProjectApi.ReadAsync<ReleaseDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/releases/latest", UriKind.Relative), Ct));
        Assert.Equal((ReleaseStatus.Succeeded, DeploymentStatus.Error, release.MergeCommitSha), (release.Status, release.ProductionDeployment!.Status, release.ProductionDeployment.GitSha));
        var failedEvent = Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.DeploymentFailed));
        Assert.Equal("production", LifecycleSeed.Payload(failedEvent.Payload).GetProperty("target").GetString());
        Assert.Contains(await LifecycleSeed.NotificationJobsAsync(app, id), j => j.Payload.Contains(failedEvent.Id.ToString(), StringComparison.Ordinal));

        // An Owner can redeploy production (a new row); a Contributor can't.
        var redeployPath = new Uri($"{fixture.Path}/deployments/{production.Id}/redeploy", UriKind.Relative);
        await Problems.AssertAsync(await fixture.Contributor.Client.PostAsync(redeployPath, null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        var redeploy = await ProjectApi.ReadAsync<DeploymentDto>(await fixture.Owner.Client.PostAsync(redeployPath, null, Ct), HttpStatusCode.Accepted);
        Assert.Equal((DeploymentTrigger.Redeploy, release.Id, production.GitSha), (redeploy.Trigger, redeploy.ReleaseId, redeploy.GitSha));
    }

    [Fact]
    public async Task FR91_Release_merged_the_production_deployment_becomes_READY_and_the_release_shows_its_URL()
    {
        var app = FakeApp();
        using var fixture = await ExecutingAsync(app);
        var id = fixture.Project.Id;
        await AgentJobs.RunQueuedAsync(app, id);
        await DeploymentHarness.SettleAsync(app, (await DeploymentHarness.JobOfAsync(app, id, Assert.Single(await DeploymentHarness.DeploymentsAsync(app, id)).Id)).Id);
        await DeploymentHarness.SettleAsync(app, (await ReviewJobAsync(app, id)).Id);
        var gate = Assert.Single(await LifecycleSeed.GatesAsync(app, id), g => g.Status == GateStatus.Pending);
        await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve");

        await AgentJobs.RunQueuedAsync(app, id);
        var production = Assert.Single(await DeploymentHarness.DeploymentsAsync(app, id), d => d.Environment == DeploymentEnvironment.Production);
        var releaseRow = await ProjectApi.WithDbAsync(app, db => db.Releases.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Equal((releaseRow.Id, releaseRow.MergeCommitSha, DeploymentTrigger.Auto), (production.ReleaseId, production.GitSha, production.Trigger));
        await DeploymentHarness.SettleAsync(app, (await DeploymentHarness.JobOfAsync(app, id, production.Id)).Id);

        var release = await ProjectApi.ReadAsync<ReleaseDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/releases/latest", UriKind.Relative), Ct));
        Assert.Equal((DeploymentStatus.Ready, FakeUrl(releaseRow.MergeCommitSha!), DeploymentEnvironment.Production), (release.ProductionDeployment!.Status, release.ProductionDeployment.Url, release.ProductionDeployment.Target));
        var settings = await DeploymentHarness.FrontendTargetAsync(app, id);
        Assert.Equal(FakeUrl(releaseRow.MergeCommitSha!), settings.ProductionUrl);
        Assert.Empty(await LifecycleSeed.EventsAsync(app, id, EventTypes.DeploymentFailed));

        // Idempotent per release: completing the release again records no second automatic production deployment.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var scheduler = scope.ServiceProvider.GetRequiredService<DeploymentScheduler>();
            Assert.Null(await scheduler.AutoProductionAsync(releaseRow, "main", DateTimeOffset.UtcNow, Ct));
        }

        // FR-91: the Owner promotes the ready production deployment (a new promote row; Git untouched).
        var promoted = await ProjectApi.ReadAsync<DeploymentDto>(
            await fixture.Owner.Client.PostAsync(new Uri($"{fixture.Path}/deployments/{production.Id}/promote", UriKind.Relative), null, Ct), HttpStatusCode.Accepted);
        Assert.Equal((DeploymentTrigger.Promote, DeploymentStatus.Ready, (Guid?)production.Id, fixture.Owner.User!.Id), (promoted.Trigger, promoted.Status, promoted.PromotedFromDeploymentId, promoted.TriggeredBy!.Id));
    }

    [Fact]
    public async Task S4_15_A_newer_preview_of_the_branch_stops_polling_of_the_older_which_stays_recorded()
    {
        var app = DeploymentHarness.Fake(factory);
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client);
        await DeploymentHarness.BindAsync(app, project.Id, await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id), owner.User!.Id);
        var versionId = await DeploymentHarness.VersionIdAsync(app, project.Id);
        var older = await DeploymentHarness.ScheduleAsync(app, new NewDeployment(project.Id, versionId, null, DeploymentEnvironment.Preview, "aide/x", new string('a', 40), DeploymentTrigger.Auto, null));
        var olderJob = await DeploymentHarness.JobOfAsync(app, project.Id, older.Id);
        Assert.True(await DeploymentHarness.RunOnceAsync(app, olderJob.Id));

        var newer = await DeploymentHarness.ScheduleAsync(app, new NewDeployment(project.Id, versionId, null, DeploymentEnvironment.Preview, "aide/x", new string('b', 40), DeploymentTrigger.Auto, null));

        var stopped = await DeploymentHarness.DeploymentAsync(app, older.Id);
        Assert.Equal((DeploymentStatus.Canceled, DeploymentPollRules.SupersededMessage), (stopped.Status, stopped.ErrorMessage));
        Assert.Equal(JobStatus.Cancelled, (await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == olderJob.Id, Ct))).Status);
        Assert.Equal(DeploymentStatus.Queued, (await DeploymentHarness.DeploymentAsync(app, newer.Id)).Status);
        Assert.Equal(2, (await DeploymentHarness.DeploymentsAsync(app, project.Id)).Count);
    }

    [Fact]
    public async Task S4_15_NFR_REL2_A_restart_resumes_polling_from_the_job_table()
    {
        var app = DeploymentHarness.Fake(factory);
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client);
        await DeploymentHarness.BindAsync(app, project.Id, await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id), owner.User!.Id);
        var versionId = await DeploymentHarness.VersionIdAsync(app, project.Id);
        var deployment = await DeploymentHarness.ScheduleAsync(app, new NewDeployment(project.Id, versionId, null, DeploymentEnvironment.Preview, "aide/x", new string('c', 40), DeploymentTrigger.Manual, owner.User!.Id));
        var job = await DeploymentHarness.JobOfAsync(app, project.Id, deployment.Id);
        Assert.True(await DeploymentHarness.RunOnceAsync(app, job.Id));
        Assert.True(await DeploymentHarness.RunOnceAsync(app, job.Id));
        var deferred = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct));
        Assert.Equal((JobStatus.Queued, 0), (deferred.Status, deferred.Attempts));
        Assert.True(deferred.RunAfter > DateTimeOffset.UtcNow);
        Assert.Equal(DeploymentStatus.Building, (await DeploymentHarness.DeploymentAsync(app, deployment.Id)).Status);

        // A new host (fresh provider state) picks up the queued job and finishes the deployment.
        var restarted = DeploymentHarness.Fake(factory);
        await DeploymentHarness.SettleAsync(restarted, job.Id);

        Assert.Equal(DeploymentStatus.Ready, (await DeploymentHarness.DeploymentAsync(restarted, deployment.Id)).Status);
    }

    // ---------------------------------------------------------------- the deployment job against a stub Vercel API

    private async Task<(WebApplicationFactory<Program> App, ApiSession Owner, ProjectDto Project, Guid VersionId)> VercelBoundAsync(FakeGitHubApi vercel)
    {
        var app = DeploymentHarness.Vercel(factory, vercel);
        var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.CreateAsync(owner.Client, git: new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_deployFlowToken" });
        await DeploymentHarness.BindAsync(app, project.Id, await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id), owner.User!.Id);
        return (app, owner, project, await DeploymentHarness.VersionIdAsync(app, project.Id));
    }

    [Fact]
    public async Task FR90_A_Vercel_preview_build_error_records_the_build_log_tail()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v13/deployments", HttpStatusCode.OK, """{"id":"dpl_1","url":"todo-git-aide.vercel.app","readyState":"QUEUED","inspectorUrl":"https://vercel.com/acme/todo/dpl_1"}""")
            .On(HttpMethod.Get, "/v13/deployments/dpl_1", HttpStatusCode.OK, """{"id":"dpl_1","url":"todo-git-aide.vercel.app","readyState":"ERROR","errorMessage":"Command \"npm run build\" exited with 1"}""")
            .On(HttpMethod.Get, "/v3/deployments/dpl_1/events", HttpStatusCode.OK,
                """[{"type":"stderr","created":3,"payload":{"text":"Type error: Type 'number' is not assignable to type 'string'."}},{"type":"stdout","created":1,"payload":{"text":"Running \"npm run build\""}},{"type":"stdout","created":2,"text":"Failed to compile."},{"type":"stdout","created":4,"text":"debug: key=build-secret-0123456789"}]""");
        var (app, owner, project, versionId) = await VercelBoundAsync(vercel);
        using var _ = owner;

        // An env value echoed by the build is masked in the stored tail (it is shown to members and given to review).
        await owner.Client.PutAsJsonAsync($"/api/projects/{project.Id}/settings/deployment/env/BUILD_SECRET", new { value = "build-secret-0123456789", targets = Preview }, Ct);
        var sha = new string('d', 40);
        var deployment = await DeploymentHarness.ScheduleAsync(app, new NewDeployment(project.Id, versionId, null, DeploymentEnvironment.Preview, "aide/todo/v0.1.0", sha, DeploymentTrigger.Auto, null));

        await DeploymentHarness.SettleAsync(app, (await DeploymentHarness.JobOfAsync(app, project.Id, deployment.Id)).Id);

        var failed = await DeploymentHarness.DeploymentAsync(app, deployment.Id);
        Assert.Equal((DeploymentStatus.Error, ErrorCodes.VercelError, "https://todo-git-aide.vercel.app", "dpl_1"), (failed.Status, failed.ErrorCode, failed.Url, failed.ProviderDeploymentId));
        Assert.StartsWith("Running \"npm run build\"\nFailed to compile.\nType error: Type 'number' is not assignable to type 'string'.\n", failed.BuildLogTail, StringComparison.Ordinal);
        Assert.DoesNotContain("build-secret-0123456789", failed.BuildLogTail, StringComparison.Ordinal);
        var create = JsonDocument.Parse(Assert.Single(vercel.Requests, r => r.Method == "POST" && r.Path == "/v13/deployments").Body).RootElement;
        Assert.Equal(("todo", "prj_todo", "github", "acme", "todo", "aide/todo/v0.1.0", sha), (
            create.GetProperty("name").GetString(),
            create.GetProperty("project").GetString(),
            create.GetProperty("gitSource").GetProperty("type").GetString(),
            create.GetProperty("gitSource").GetProperty("org").GetString(),
            create.GetProperty("gitSource").GetProperty("repo").GetString(),
            create.GetProperty("gitSource").GetProperty("ref").GetString(),
            create.GetProperty("gitSource").GetProperty("sha").GetString()));
        Assert.False(create.TryGetProperty("target", out var _1)); // a preview names no target
    }

    [Fact]
    public async Task FR91_The_production_deployment_adopts_the_one_Vercel_made_for_the_merge_commit()
    {
        var sha = new string('e', 40);
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v6/deployments", HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                deployments = new object[]
                {
                    new { uid = "dpl_other", state = "READY", meta = new { githubCommitSha = new string('f', 40) } },
                    new { uid = "dpl_auto", url = "todo-abc.vercel.app", state = "BUILDING", meta = new { githubCommitSha = sha } },
                },
            }))
            .On(HttpMethod.Get, "/v13/deployments/dpl_auto", HttpStatusCode.OK, """{"id":"dpl_auto","url":"todo-abc.vercel.app","readyState":"READY","alias":["todo.vercel.app"]}""");
        var (app, owner, project, versionId) = await VercelBoundAsync(vercel);
        using var _ = owner;
        var deployment = await DeploymentHarness.ScheduleAsync(app, new NewDeployment(project.Id, versionId, null, DeploymentEnvironment.Production, "main", sha, DeploymentTrigger.Auto, null));

        await DeploymentHarness.SettleAsync(app, (await DeploymentHarness.JobOfAsync(app, project.Id, deployment.Id)).Id);

        var adopted = await DeploymentHarness.DeploymentAsync(app, deployment.Id);
        Assert.Equal(("dpl_auto", DeploymentStatus.Ready), (adopted.ProviderDeploymentId, adopted.Status));
        Assert.DoesNotContain(vercel.Requests, r => r.Method == "POST");
        Assert.Contains(vercel.Requests, r => r.Path == "/v6/deployments" && r.Query.Contains("target=production", StringComparison.Ordinal) && r.Query.Contains($"sha={sha}", StringComparison.Ordinal));
        Assert.Equal("https://todo.vercel.app", (await DeploymentHarness.FrontendTargetAsync(app, project.Id)).ProductionUrl);
    }

    [Fact]
    public async Task FR91_Without_Vercels_own_deployment_AIDE_creates_production_after_the_adoption_window()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v6/deployments", HttpStatusCode.OK, """{"deployments":[]}""")
            .On(HttpMethod.Post, "/v13/deployments", HttpStatusCode.OK, """{"id":"dpl_prod","url":"todo-prod.vercel.app","readyState":"BUILDING"}""")
            .On(HttpMethod.Get, "/v13/deployments/dpl_prod", HttpStatusCode.OK, """{"id":"dpl_prod","url":"todo-prod.vercel.app","readyState":"READY"}""");
        var (app, owner, project, versionId) = await VercelBoundAsync(vercel);
        using var _ = owner;
        var deployment = await DeploymentHarness.ScheduleAsync(
            app, new NewDeployment(project.Id, versionId, null, DeploymentEnvironment.Production, "main", new string('1', 40), DeploymentTrigger.Auto, null), DateTimeOffset.UtcNow.AddMinutes(-1));

        await DeploymentHarness.SettleAsync(app, (await DeploymentHarness.JobOfAsync(app, project.Id, deployment.Id)).Id);

        Assert.Equal(DeploymentStatus.Ready, (await DeploymentHarness.DeploymentAsync(app, deployment.Id)).Status);
        var create = JsonDocument.Parse(Assert.Single(vercel.Requests, r => r.Method == "POST").Body).RootElement;
        Assert.Equal("production", create.GetProperty("target").GetString());
    }

    [Fact]
    public async Task S4_15_NFR_REL2_A_Vercel_rate_limit_defers_without_counting_an_attempt()
    {
        var reset = DateTimeOffset.UtcNow.AddSeconds(90);
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v13/deployments", HttpStatusCode.TooManyRequests, """{"error":{"code":"rate_limited","message":"Rate limit exceeded"}}""",
                new Dictionary<string, string> { ["x-ratelimit-reset"] = reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var (app, owner, project, versionId) = await VercelBoundAsync(vercel);
        using var _ = owner;
        var deployment = await DeploymentHarness.ScheduleAsync(app, new NewDeployment(project.Id, versionId, null, DeploymentEnvironment.Preview, "aide/x", new string('2', 40), DeploymentTrigger.Auto, null));
        var job = await DeploymentHarness.JobOfAsync(app, project.Id, deployment.Id);

        Assert.True(await DeploymentHarness.RunOnceAsync(app, job.Id));

        var deferred = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct));
        Assert.Equal((JobStatus.Queued, 0), (deferred.Status, deferred.Attempts));
        Assert.True(deferred.RunAfter >= reset.AddSeconds(-1), $"{deferred.RunAfter:O} < {reset:O}");
        Assert.Equal(DeploymentStatus.Queued, (await DeploymentHarness.DeploymentAsync(app, deployment.Id)).Status);
    }

    [Fact]
    public async Task S4_15_A_deployment_still_building_after_30_minutes_is_DEPLOYMENT_TIMEOUT()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Get, "/v13/deployments/dpl_slow", HttpStatusCode.OK, """{"id":"dpl_slow","readyState":"BUILDING"}""");
        var (app, owner, project, versionId) = await VercelBoundAsync(vercel);
        using var _ = owner;
        var deployment = await DeploymentHarness.ScheduleAsync(
            app, new NewDeployment(project.Id, versionId, null, DeploymentEnvironment.Production, "main", new string('3', 40), DeploymentTrigger.Redeploy, owner.User!.Id), DateTimeOffset.UtcNow.AddMinutes(-31));
        await ProjectApi.WithDbAsync(app, db => db.Deployments.Where(d => d.Id == deployment.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ProviderDeploymentId, "dpl_slow").SetProperty(d => d.Status, DeploymentStatus.Building), Ct));

        Assert.False(await DeploymentHarness.RunOnceAsync(app, (await DeploymentHarness.JobOfAsync(app, project.Id, deployment.Id)).Id));

        var timedOut = await DeploymentHarness.DeploymentAsync(app, deployment.Id);
        Assert.Equal((DeploymentStatus.Error, ErrorCodes.DeploymentTimeout), (timedOut.Status, timedOut.ErrorCode));
        Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.DeploymentFailed));
    }

    [Fact]
    public async Task FR87_A_token_Vercel_rejects_during_a_deployment_marks_the_connection_invalid_and_notifies_Owners()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v13/deployments", HttpStatusCode.Forbidden, """{"error":{"code":"forbidden","message":"Not authorized","invalidToken":true}}""");
        var (app, owner, project, versionId) = await VercelBoundAsync(vercel);
        using var _ = owner;
        var deployment = await DeploymentHarness.ScheduleAsync(app, new NewDeployment(project.Id, versionId, null, DeploymentEnvironment.Preview, "aide/x", new string('4', 40), DeploymentTrigger.Auto, null));

        Assert.False(await DeploymentHarness.RunOnceAsync(app, (await DeploymentHarness.JobOfAsync(app, project.Id, deployment.Id)).Id));

        Assert.Equal((DeploymentStatus.Error, ErrorCodes.CredentialInvalid), ((await DeploymentHarness.DeploymentAsync(app, deployment.Id)).Status, (await DeploymentHarness.DeploymentAsync(app, deployment.Id)).ErrorCode));
        Assert.Equal(PlatformConnectionStatus.Invalid, (await ProjectApi.WithDbAsync(app, db => db.PlatformConnections.AsNoTracking().SingleAsync(c => c.UserId == owner.User!.Id, Ct))).Status);
        Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.ConnectionInvalid));
        Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.DeploymentFailed));
    }

    [Fact]
    public async Task FR91_Promote_calls_Vercel_with_the_earlier_deployment_and_records_a_promote_row()
    {
        var vercel = new FakeGitHubApi()
            .On(HttpMethod.Post, "/v10/projects/prj_todo/promote/dpl_old", HttpStatusCode.Created, "{}");
        var (app, owner, project, versionId) = await VercelBoundAsync(vercel);
        using var _ = owner;
        var old = await DeploymentHarness.ScheduleAsync(app, new NewDeployment(project.Id, versionId, null, DeploymentEnvironment.Production, "main", new string('5', 40), DeploymentTrigger.Auto, null));
        await ProjectApi.WithDbAsync(app, db => db.Deployments.Where(d => d.Id == old.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ProviderDeploymentId, "dpl_old").SetProperty(d => d.Status, DeploymentStatus.Ready).SetProperty(d => d.Url, "https://todo-old.vercel.app"), Ct));

        var promoted = await ProjectApi.ReadAsync<DeploymentDto>(
            await owner.Client.PostAsync(new Uri($"/api/projects/{project.Id}/deployments/{old.Id}/promote", UriKind.Relative), null, Ct), HttpStatusCode.Accepted);

        Assert.Equal((DeploymentTrigger.Promote, DeploymentStatus.Ready, "https://todo-old.vercel.app", (Guid?)old.Id), (promoted.Trigger, promoted.Status, promoted.Url, promoted.PromotedFromDeploymentId));
        Assert.Single(vercel.Requests, r => r.Method == "POST" && r.Path == "/v10/projects/prj_todo/promote/dpl_old");
    }

    private static string FakeUrl(string sha) => FakeDeploymentRules.Url(sha);
}
