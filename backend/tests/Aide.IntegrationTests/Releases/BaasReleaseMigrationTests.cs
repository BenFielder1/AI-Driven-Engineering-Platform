using System.Net;
using System.Text.Json;
using Aide.Api.Features.Databases;
using Aide.Api.Features.Deployments;
using Aide.Api.Features.Release;
using Aide.Domain.Common;
using Aide.Domain.Databases;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Releases;

/// <summary>
/// FR-153, §4.21 "When migrations run": a Supabase BaaS release applies its migrations to the production database in the
/// release job, before AIDE merges, and records the run on the release; a failed run blocks the merge. Local Git, the Fake
/// LLM, and the Fake Supabase database (its <c>*.test</c> pooler host uses the runner's in-memory history).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class BaasReleaseMigrationTests(AideApiFactory factory)
{
    private const string Migration = "supabase/migrations/20261031000000_orders.sql";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> App() =>
        AgentJobs.WithResponders(DeploymentHarness.Fake(factory), new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));

    private string WorkingCopy(Guid id) => Path.Combine(factory.WorkspacesRoot, id.ToString("D"));

    /// <summary>A releasable Local project with an available Supabase production database and a migration committed on its branch.</summary>
    private async Task<(ProjectFixture Fixture, string BranchHead, string MainBefore)> ReleasableWithMigrationAsync(WebApplicationFactory<Program> app, string sql)
    {
        var fixture = await ReleaseFlowTests.ReleasableAsync(app);
        var id = fixture.Project.Id;
        await SeedProductionSupabaseAsync(app, id);

        var branch = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == id).Select(v => v.BranchName!).SingleAsync(Ct));
        var directory = WorkingCopy(id);
        await TestGit.RunAsync(directory, Ct, "checkout", "-q", branch);
        var file = Path.Combine(directory, Migration.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, sql, Ct);
        await TestGit.RunAsync(directory, Ct, "add", Migration);
        await TestGit.RunAsync(directory, Ct, "commit", "-q", "-m", "Add the orders migration");
        return (fixture, await TestGit.RunAsync(directory, Ct, "rev-parse", branch), await TestGit.RunAsync(directory, Ct, "rev-parse", "main"));
    }

    /// <summary>An available Supabase production database (the Fake <c>*.test</c> pooler host: the runner's in-memory history).</summary>
    private static async Task SeedProductionSupabaseAsync(WebApplicationFactory<Program> app, Guid id)
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            var secrets = scope.ServiceProvider.GetRequiredService<ManagedDatabaseSecrets>();
            var now = DateTimeOffset.UtcNow;
            var row = new ManagedDatabase
            {
                ProjectId = id,
                Environment = DeploymentEnvironment.Production,
                Provider = ManagedDatabaseProvider.Supabase,
                ResourceId = "abcd",
                Status = ManagedDatabaseStatus.Available,
                CreatedAt = now,
                UpdatedAt = now,
            };
            secrets.SetConnection(row, new ManagedDatabaseConnection
            {
                SupabaseRef = "abcd",
                PoolerHost = $"aws-0-{id:N}.pooler.supabase.test",
                PoolerUser = "postgres.abcd",
                DatabaseName = "postgres",
            });
            secrets.SetPassword(row, "fake-password");
            db.ManagedDatabases.Add(row);
            await db.SaveChangesAsync(Ct);
        }
    }

    [Fact]
    public async Task FR153_S4_21_A_BaaS_release_applies_its_migrations_to_production_before_the_merge_and_records_the_run()
    {
        var app = App();
        var (fixture, branchHead, _) = await ReleasableWithMigrationAsync(app, "create table orders (id bigint primary key);\n");
        using var _ = fixture;
        var id = fixture.Project.Id;

        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        var jobs = await AgentJobs.RunQueuedAsync(app, id);

        Assert.Equal(JobStatus.Succeeded, Assert.Single(jobs, j => j.Type == JobTypes.Release).Status);
        var release = await ProjectApi.WithDbAsync(app, db => db.Releases.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Equal(ReleaseStatus.Succeeded, release.Status);
        var run = await ProjectApi.WithDbAsync(app, db => db.DatabaseMigrationRuns.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Equal(
            (DeploymentEnvironment.Production, MigrationRunStatus.Succeeded, branchHead, (Guid?)release.Id, (Guid?)run.Id),
            (run.Environment, run.Status, run.GitSha, run.ReleaseId, release.MigrationRunId));
        Assert.Equal(["20261031000000"], run.Applied.Select(a => a.Version));
        Assert.True(run.FinishedAt <= release.CompletedAt); // applied before the merge was recorded
        Assert.Equal(branchHead, await TestGit.RunAsync(WorkingCopy(id), Ct, "rev-parse", "main^2"));

        var dto = await ProjectApi.ReadAsync<ReleaseDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/releases/latest", UriKind.Relative), Ct));
        Assert.Equal((run.Id, false), (dto.MigrationRunId, dto.MigrationsAppliedNotMerged));
        var listed = await ProjectApi.ReadAsync<List<MigrationRunDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/migrations", UriKind.Relative), Ct));
        Assert.Equal(release.Id, Assert.Single(listed).ReleaseId);
    }

    [Fact]
    public async Task FR153_S4_21_FR155_A_release_that_failed_unmerged_after_its_migrations_applied_says_so_and_the_run_has_its_events()
    {
        var app = App();
        var (fixture, _, _) = await ReleasableWithMigrationAsync(app, "create table orders (id bigint primary key);\n");
        using var _ = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MigrationRunStarted));
        var completed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MigrationRunCompleted)).Payload);
        Assert.Equal(("production", 1), (completed.GetProperty("environment").GetString(), completed.GetProperty("applied").GetInt32()));
        Assert.Empty(await LifecycleSeed.EventsAsync(app, id, EventTypes.MigrationRunFailed));

        // The state a closed PR leaves: the migrations were applied, the merge never happened.
        await ProjectApi.WithDbAsync(app, db => db.Releases.Where(r => r.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReleaseStatus.Failed).SetProperty(r => r.MergeCommitSha, (string?)null), Ct));

        var dto = await ProjectApi.ReadAsync<ReleaseDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/releases/latest", UriKind.Relative), Ct));
        Assert.True(dto.MigrationsAppliedNotMerged);
    }

    [Fact]
    public async Task FR153_S4_21_GitHub_migrates_the_PR_remote_head_and_pins_the_merge_to_it_not_the_stale_local_branch()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        const string Pulls = "/repos/acme/widgets/pulls";
        const string Pull = Pulls + "/42";
        const string PrUrl = "https://github.com/acme/widgets/pull/42";
        var branch = new string[1];
        string? mergedAs = null;
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, Pulls, HttpStatusCode.OK, "[]")
            .On(HttpMethod.Post, Pulls, HttpStatusCode.Created, $$"""{"number":42,"html_url":"{{PrUrl}}"}""")
            .On(HttpMethod.Get, Pull, _ => (HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                number = 42,
                state = mergedAs is null ? "open" : "closed",
                merged = mergedAs is not null,
                merge_commit_sha = mergedAs,
                mergeable = true,
                html_url = PrUrl,
                head = new { sha = ReleaseFlowTests.BareSha(remote, $"refs/heads/{branch[0]}") },
            })))
            .On(HttpMethod.Put, Pull + "/merge", _ => (HttpStatusCode.OK, JsonSerializer.Serialize(new { sha = mergedAs = ReleaseFlowTests.MergeInBare(remote, branch[0]), merged = true })));
        await using var app = remote.App(App(), api, out _);
        using var fixture = await ProjectApi.FixtureAsync(app, git: new { provider = "github_pat", repoUrl = BareRemote.RepoUrl, defaultBranch = "main", token = "ghp_baasPinnedHeadToken0123456789" });
        var id = fixture.Project.Id;
        branch[0] = Aide.Domain.Releases.ReleaseVersionRules.BranchName(fixture.Project.Slug, id, Aide.Domain.Releases.SemVer.Parse(fixture.Project.DevelopmentVersion!));
        await SeedProductionSupabaseAsync(app, id);
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        // Someone pushes a migration to the PR branch on GitHub after AIDE's push: the working copy's local branch is stale.
        var clone = Path.Combine(remote.Root, "push-" + Guid.NewGuid().ToString("N"));
        ReleaseFlowTests.Run(remote.Root, "clone", "-q", "-b", branch[0], remote.BarePath, clone);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(clone, Migration))!);
        await File.WriteAllTextAsync(Path.Combine(clone, Migration), "create table orders (id bigint primary key);\n", Ct);
        ReleaseFlowTests.Run(clone, "add", Migration);
        ReleaseFlowTests.Run(clone, "commit", "-q", "-m", "Add the orders migration on GitHub");
        ReleaseFlowTests.Run(clone, "push", "-q", "origin", branch[0]);
        var remoteHead = ReleaseFlowTests.BareSha(remote, $"refs/heads/{branch[0]}");

        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        var jobs = await AgentJobs.RunQueuedAsync(app, id);

        var job = Assert.Single(jobs, j => j.Type == JobTypes.Release);
        Assert.True(job.Status == JobStatus.Succeeded, job.LastError);
        var run = await ProjectApi.WithDbAsync(app, db => db.DatabaseMigrationRuns.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Equal((MigrationRunStatus.Succeeded, remoteHead), (run.Status, run.GitSha));
        Assert.Equal(["20261031000000"], run.Applied.Select(a => a.Version));
        Assert.NotEqual(remoteHead, await TestGit.RunAsync(WorkingCopy(id), Ct, "rev-parse", $"refs/heads/{branch[0]}"));
        var merge = api.Requests.Single(r => r.Method == "PUT" && r.Path == Pull + "/merge");
        using var body = JsonDocument.Parse(merge.Body);
        Assert.Equal(remoteHead, body.RootElement.GetProperty("sha").GetString());
    }

    [Fact]
    public async Task FR43_FR153_A_BaaS_release_that_conflicts_fails_with_MERGE_CONFLICT_and_its_files_without_migrating_or_merging()
    {
        var app = App();
        var (fixture, _, _) = await ReleasableWithMigrationAsync(app, "create table orders (id bigint primary key);\n");
        using var _ = fixture;
        var id = fixture.Project.Id;

        // main gets a different file at the same path: the branch now conflicts with it.
        var directory = WorkingCopy(id);
        await TestGit.RunAsync(directory, Ct, "checkout", "-q", "main");
        var file = Path.Combine(directory, Migration.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "create table orders (sku text primary key);\n", Ct);
        await TestGit.RunAsync(directory, Ct, "add", Migration);
        await TestGit.RunAsync(directory, Ct, "commit", "-q", "-m", "A different orders migration on main");
        var mainBefore = await TestGit.RunAsync(directory, Ct, "rev-parse", "main");

        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var release = await ProjectApi.ReadAsync<ReleaseDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/releases/latest", UriKind.Relative), Ct));
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.MergeConflict), (release.Status, release.ErrorCode));
        Assert.Equal([Migration], release.ConflictingFiles);
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.DatabaseMigrationRuns.AnyAsync(r => r.ProjectId == id, Ct)));
        Assert.Equal(mainBefore, await TestGit.RunAsync(directory, Ct, "rev-parse", "main"));
        Assert.Equal(ProjectStatus.Releasing, (await LifecycleSeed.ProjectAsync(app, id)).Status);
    }

    [Theory]
    [InlineData(ManagedDatabaseStatus.Paused)]
    [InlineData(ManagedDatabaseStatus.Error)]
    [InlineData(ManagedDatabaseStatus.Provisioning)]
    public async Task FR153_FR155_A_production_database_that_cant_take_migrations_blocks_the_merge_with_a_failed_run(ManagedDatabaseStatus status)
    {
        var app = App();
        var (fixture, _, mainBefore) = await ReleasableWithMigrationAsync(app, "create table orders (id bigint primary key);\n");
        using var _ = fixture;
        var id = fixture.Project.Id;
        await ProjectApi.WithDbAsync(app, db => db.ManagedDatabases.Where(d => d.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status), Ct));

        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var release = await ProjectApi.WithDbAsync(app, db => db.Releases.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.MigrationFailed, (string?)null), (release.Status, release.ErrorCode, release.MergeCommitSha));
        Assert.Equal(mainBefore, await TestGit.RunAsync(WorkingCopy(id), Ct, "rev-parse", "main"));
        var run = await ProjectApi.WithDbAsync(app, db => db.DatabaseMigrationRuns.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Equal((MigrationRunStatus.Failed, ErrorCodes.MigrationFailed, (Guid?)run.Id), (run.Status, run.ErrorCode, release.MigrationRunId));
        Assert.Empty(run.Applied);
        var failed = Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MigrationRunFailed));
        Assert.Contains(await LifecycleSeed.NotificationJobsAsync(app, id), j => j.Payload.Contains(failed.Id.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR153_FR155_A_paused_preview_database_fails_the_preview_rollout_with_a_failed_run()
    {
        var app = App();
        var (fixture, branchHead, _) = await ReleasableWithMigrationAsync(app, "create table orders (id bigint primary key);\n");
        using var _ = fixture;
        var id = fixture.Project.Id;
        await ProjectApi.WithDbAsync(app, db => db.ManagedDatabases.Where(d => d.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Environment, DeploymentEnvironment.Preview).SetProperty(d => d.Status, ManagedDatabaseStatus.Paused), Ct));
        var job = await ProjectApi.WithDbAsync(app, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var versionId = await db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == id).Select(v => v.Id).SingleAsync(Ct);
            var queued = new Job
            {
                Type = JobTypes.PreviewRollout,
                ProjectId = id,
                Status = JobStatus.Queued,
                Payload = AideJson.Serialize(new RolloutJobPayload(id, DeploymentEnvironment.Preview, versionId, null, "aide/test", branchHead, DeploymentTrigger.Auto, null, now)),
                RunAfter = now,
                CreatedAt = now,
            };
            db.Jobs.Add(queued);
            await db.SaveChangesAsync(Ct);
            return queued;
        });

        await DeploymentHarness.SettleRolloutAsync(app, id, job.Id);

        var run = await ProjectApi.WithDbAsync(app, db => db.DatabaseMigrationRuns.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Equal((DeploymentEnvironment.Preview, MigrationRunStatus.Failed, ErrorCodes.MigrationFailed), (run.Environment, run.Status, run.ErrorCode));
        Assert.Empty(run.Applied);
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MigrationRunFailed));
        var completed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.PreviewRolloutCompleted)).Payload);
        Assert.Equal(("failed", ErrorCodes.MigrationFailed), (completed.GetProperty("state").GetString(), completed.GetProperty("errorCode").GetString()));
    }

    [Fact]
    public async Task FR153_S4_21_A_failed_production_migration_blocks_the_merge_with_MIGRATION_FAILED()
    {
        var app = App();
        var (fixture, _, mainBefore) = await ReleasableWithMigrationAsync(app, "-- aide-fake:fail\ncreate table orders ();\n");
        using var _ = fixture;
        var id = fixture.Project.Id;

        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var release = await ProjectApi.WithDbAsync(app, db => db.Releases.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.MigrationFailed, (string?)null), (release.Status, release.ErrorCode, release.MergeCommitSha));
        Assert.Contains("nothing was merged", release.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(mainBefore, await TestGit.RunAsync(WorkingCopy(id), Ct, "rev-parse", "main"));
        var run = await ProjectApi.WithDbAsync(app, db => db.DatabaseMigrationRuns.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Equal((MigrationRunStatus.Failed, ErrorCodes.MigrationFailed, (Guid?)run.Id), (run.Status, run.ErrorCode, release.MigrationRunId));
        Assert.Empty(run.Applied);
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MigrationRunFailed));
        Assert.Equal(ProjectStatus.Releasing, (await LifecycleSeed.ProjectAsync(app, id)).Status);
    }
}
