using System.Text.Json;
using Aide.Api.Features.Git;
using Aide.Api.Features.Projects;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.Infrastructure.Agents;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Releases;

/// <summary>
/// FR-153, §4.21 "When migrations run" under the <c>manual</c> strategy: production is migrated as the release enters
/// <c>awaiting_merge</c> once merge readiness passes, the merge poll keeps it level with the PR head, and a person merging a
/// head that wasn't migrated is on record. Local Git, the Fake LLM, and the Fake Supabase database.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class BaasManualReleaseMigrationTests(AideApiFactory factory)
{
    private const string Orders = "supabase/migrations/20261031000000_orders.sql";
    private const string Items = "supabase/migrations/20261031000100_items.sql";
    private const string OrdersSql = "create table orders (id bigint primary key);\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> App() =>
        AgentJobs.WithResponders(DeploymentHarness.Fake(factory), new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));

    private string WorkingCopy(Guid id) => Path.Combine(factory.WorkspacesRoot, id.ToString("D"));

    /// <summary>A manual-strategy BaaS project with its release gate pending and the orders migration on the version branch.</summary>
    private async Task<(ProjectFixture Fixture, string Branch, string Head)> ManualReleasableAsync(WebApplicationFactory<Program> app)
    {
        var fixture = await ReleaseFlowTests.ReleasableAsync(app);
        var id = fixture.Project.Id;
        await BaasReleaseMigrationTests.SeedProductionSupabaseAsync(app, id);
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        var branch = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == id).Select(v => v.BranchName!).SingleAsync(Ct));
        await TestGit.RunAsync(WorkingCopy(id), Ct, "checkout", "-q", branch);
        return (fixture, branch, await CommitAsync(id, Orders, OrdersSql));
    }

    /// <summary>Commits a file on the checked-out branch and returns the new head.</summary>
    private async Task<string> CommitAsync(Guid id, string path, string content)
    {
        var directory = WorkingCopy(id);
        var file = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, content, Ct);
        await TestGit.RunAsync(directory, Ct, "add", path);
        await TestGit.RunAsync(directory, Ct, "commit", "-q", "-m", $"Add {path}");
        return await TestGit.RunAsync(directory, Ct, "rev-parse", "HEAD");
    }

    /// <summary>A person merges the branch into main in the Local repository, as a merge on the host.</summary>
    private async Task MergeByHandAsync(Guid id, string branch)
    {
        var directory = WorkingCopy(id);
        await TestGit.RunAsync(directory, Ct, "checkout", "-q", "main");
        await TestGit.RunAsync(directory, Ct, "merge", "-q", "--no-ff", "-m", "Merge by hand", branch);
        await TestGit.RunAsync(directory, Ct, "checkout", "-q", branch);
    }

    private static Task<Job> PollJobAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.ProjectId == id && j.Type == JobTypes.MergePoll, Ct));

    private static Task<Aide.Domain.Entities.Release> ReleaseAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.Releases.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));

    private static Task<List<DatabaseMigrationRun>> RunsAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.DatabaseMigrationRuns.AsNoTracking().Where(r => r.ProjectId == id).OrderBy(r => r.StartedAt).ToListAsync(Ct));

    [Fact]
    public async Task FR153_S4_21_A_manual_BaaS_release_migrates_as_it_enters_awaiting_merge_and_never_merges()
    {
        var app = App();
        var (fixture, branch, head) = await ManualReleasableAsync(app);
        using var _ = fixture;
        var id = fixture.Project.Id;
        var mainBefore = await TestGit.RunAsync(WorkingCopy(id), Ct, "rev-parse", "main");

        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var release = await ReleaseAsync(app, id);
        var run = Assert.Single(await RunsAsync(app, id));
        Assert.Equal((ReleaseStatus.AwaitingMerge, (Guid?)run.Id), (release.Status, release.MigrationRunId));
        Assert.Equal((MigrationRunStatus.Succeeded, head, (Guid?)release.Id), (run.Status, run.GitSha, run.ReleaseId));
        Assert.Equal(["20261031000000"], run.Applied.Select(a => a.Version));
        Assert.Equal(mainBefore, await TestGit.RunAsync(WorkingCopy(id), Ct, "rev-parse", "main"));

        // The head stays: more polls don't run it again. A person merges it: nothing to warn about.
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));
        Assert.Single(await RunsAsync(app, id));
        await MergeByHandAsync(id, branch);
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        Assert.Equal(ReleaseStatus.Succeeded, (await ReleaseAsync(app, id)).Status);
        Assert.Empty(await LifecycleSeed.EventsAsync(app, id, EventTypes.MergedHeadMismatch));
    }

    [Fact]
    public async Task FR43_FR153_S4_21_A_conflict_at_entry_waits_unmigrated_then_the_poll_migrates_once_it_is_resolved()
    {
        var app = App();
        var (fixture, branch, _) = await ManualReleasableAsync(app);
        using var _ = fixture;
        var id = fixture.Project.Id;
        var directory = WorkingCopy(id);
        await TestGit.RunAsync(directory, Ct, "checkout", "-q", "main");
        await CommitAsync(id, Orders, "create table orders (sku text primary key);\n");
        await TestGit.RunAsync(directory, Ct, "checkout", "-q", branch);

        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var release = await ReleaseAsync(app, id);
        Assert.Equal((ReleaseStatus.AwaitingMerge, (Guid?)null), (release.Status, release.MigrationRunId));
        Assert.Contains(MergeReadinessReasonCode.MergeConflict, MergeReasonsJson.Read(release.MergeReasons)!.Select(r => r.Code));
        Assert.Empty(await RunsAsync(app, id));

        // Still conflicting: the poll doesn't migrate.
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));
        Assert.Empty(await RunsAsync(app, id));

        // Resolved on the branch: the poll migrates the new head, and the release keeps waiting for the person's merge.
        await TestGit.RunAsync(directory, Ct, "merge", "-q", "-X", "ours", "-m", "Resolve", "main");
        var resolved = await TestGit.RunAsync(directory, Ct, "rev-parse", "HEAD");
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var run = Assert.Single(await RunsAsync(app, id));
        Assert.Equal((MigrationRunStatus.Succeeded, resolved), (run.Status, run.GitSha));
        release = await ReleaseAsync(app, id);
        Assert.Equal((ReleaseStatus.AwaitingMerge, (Guid?)run.Id), (release.Status, release.MigrationRunId));
    }

    [Fact]
    public async Task FR153_S4_21_A_head_that_moves_while_awaiting_is_migrated_again_and_the_release_points_at_the_newest_run()
    {
        var app = App();
        var (fixture, _, head) = await ManualReleasableAsync(app);
        using var _ = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var moved = await CommitAsync(id, Items, "create table items (id bigint primary key);\n");
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var runs = await RunsAsync(app, id);
        Assert.Equal([head, moved], runs.Select(r => r.GitSha));
        Assert.Equal(["20261031000100"], runs[1].Applied.Select(a => a.Version));
        var release = await ReleaseAsync(app, id);
        Assert.Equal((ReleaseStatus.AwaitingMerge, (Guid?)runs[1].Id), (release.Status, release.MigrationRunId));
    }

    [Fact]
    public async Task FR153_S4_21_FR155_A_failed_run_while_awaiting_notifies_once_keeps_the_release_waiting_and_a_new_head_runs_again()
    {
        var app = App();
        var (fixture, _, _) = await ManualReleasableAsync(app);
        using var _ = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        await CommitAsync(id, Items, "-- aide-fake:fail\ncreate table items ();\n");
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var runs = await RunsAsync(app, id);
        Assert.Equal((2, MigrationRunStatus.Failed, ErrorCodes.MigrationFailed), (runs.Count, runs[1].Status, runs[1].ErrorCode));
        var release = await ReleaseAsync(app, id);
        Assert.Equal((ReleaseStatus.AwaitingMerge, (Guid?)runs[1].Id, (string?)null), (release.Status, release.MigrationRunId, release.ErrorCode));
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Release, ProjectStatus.AwaitingMerge), (project.CurrentPhase, project.Status));
        Assert.Equal(JobStatus.Queued, (await PollJobAsync(app, id)).Status);
        var failed = Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MigrationRunFailed));
        Assert.Contains(await LifecycleSeed.NotificationJobsAsync(app, id), j => j.Payload.Contains(failed.Id.ToString(), StringComparison.Ordinal));

        // The same head isn't retried (or re-notified) on every poll.
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));
        Assert.Equal(2, (await RunsAsync(app, id)).Count);
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MigrationRunFailed));

        // A fix on the branch runs again.
        var fixedHead = await CommitAsync(id, Items, "create table items (id bigint primary key);\n");
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));
        var last = (await RunsAsync(app, id))[^1];
        Assert.Equal((fixedHead, MigrationRunStatus.Succeeded), (last.GitSha, last.Status));
        Assert.Equal(ReleaseStatus.AwaitingMerge, (await ReleaseAsync(app, id)).Status);
    }

    [Fact]
    public async Task FR153_S4_21_FR155_A_database_paused_while_awaiting_runs_again_once_it_is_available()
    {
        var app = App();
        var (fixture, _, _) = await ManualReleasableAsync(app);
        using var _ = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        await ProjectApi.WithDbAsync(app, db => db.ManagedDatabases.Where(d => d.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ManagedDatabaseStatus.Paused), Ct));
        var moved = await CommitAsync(id, Items, "create table items (id bigint primary key);\n");
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));
        Assert.Equal((MigrationRunStatus.Failed, moved), ((await RunsAsync(app, id))[^1].Status, (await RunsAsync(app, id))[^1].GitSha));

        await ProjectApi.WithDbAsync(app, db => db.ManagedDatabases.Where(d => d.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ManagedDatabaseStatus.Available).SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow.AddSeconds(1)), Ct));
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var last = (await RunsAsync(app, id))[^1];
        Assert.Equal((MigrationRunStatus.Succeeded, moved), (last.Status, last.GitSha));
        Assert.Equal((Guid?)last.Id, (await ReleaseAsync(app, id)).MigrationRunId);
    }

    [Fact]
    public async Task FR153_S4_21_A_person_merging_a_moved_head_before_the_poll_migrates_it_is_recorded_and_the_release_still_completes()
    {
        var app = App();
        var (fixture, branch, head) = await ManualReleasableAsync(app);
        using var _ = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var moved = await CommitAsync(id, Items, "create table items (id bigint primary key);\n");
        await MergeByHandAsync(id, branch);
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var release = await ReleaseAsync(app, id);
        Assert.Equal(ReleaseStatus.Succeeded, release.Status);
        var mismatch = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MergedHeadMismatch)).Payload);
        Assert.Equal(
            (release.Id, head, moved, release.MergeCommitSha),
            (mismatch.GetProperty("releaseId").GetGuid(), mismatch.GetProperty("expectedHeadSha").GetString(), mismatch.GetProperty("mergedHeadSha").GetString(), mismatch.GetProperty("mergeCommitSha").GetString()));
    }

    [Fact]
    public async Task FR153_S4_21_A_person_merging_unmigrated_with_no_target_set_up_is_migrated_by_a_migrations_only_rollout()
    {
        var app = App();
        var (fixture, branch, _) = await ManualReleasableAsync(app);
        using var _ = fixture;
        var id = fixture.Project.Id;
        var directory = WorkingCopy(id);
        await TestGit.RunAsync(directory, Ct, "checkout", "-q", "main");
        await CommitAsync(id, Orders, "create table orders (sku text primary key);\n");
        await TestGit.RunAsync(directory, Ct, "checkout", "-q", branch);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await TestGit.RunAsync(directory, Ct, "merge", "-q", "-X", "ours", "-m", "Resolve", "main");
        await MergeByHandAsync(id, branch);
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MergedHeadMismatch));
        Assert.Empty(await RunsAsync(app, id));

        var rollout = await DeploymentHarness.RolloutJobAsync(app, id, JobTypes.ProductionRollout);
        await DeploymentHarness.SettleRolloutAsync(app, id, rollout.Id);

        var release = await ReleaseAsync(app, id);
        var run = Assert.Single(await RunsAsync(app, id));
        Assert.Equal((MigrationRunStatus.Succeeded, release.MergeCommitSha, (Guid?)run.Id), (run.Status, run.GitSha, release.MigrationRunId));
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MigrationRunStarted));
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MigrationRunCompleted));
        Assert.Empty(await DeploymentHarness.DeploymentsAsync(app, id));
        var finished = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == rollout.Id, Ct));
        Assert.Equal(JobStatus.Succeeded, finished.Status);
    }

    [Fact]
    public async Task FR153_S4_21_A_failed_poll_run_is_replaced_by_the_rollouts_succeeded_run_after_a_person_merges()
    {
        var app = App();
        var (fixture, branch, _) = await ManualReleasableAsync(app);
        using var _ = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        await ProjectApi.WithDbAsync(app, db => db.ManagedDatabases.Where(d => d.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ManagedDatabaseStatus.Paused), Ct));
        await CommitAsync(id, Items, "create table items (id bigint primary key);\n");
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));
        var failed = (await RunsAsync(app, id))[^1];
        Assert.Equal((MigrationRunStatus.Failed, (Guid?)failed.Id), (failed.Status, (await ReleaseAsync(app, id)).MigrationRunId));

        await ProjectApi.WithDbAsync(app, db => db.ManagedDatabases.Where(d => d.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ManagedDatabaseStatus.Available), Ct));
        await MergeByHandAsync(id, branch);
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));
        var rollout = await DeploymentHarness.RolloutJobAsync(app, id, JobTypes.ProductionRollout);
        await DeploymentHarness.SettleRolloutAsync(app, id, rollout.Id);

        var last = (await RunsAsync(app, id))[^1];
        Assert.Equal(MigrationRunStatus.Succeeded, last.Status);
        Assert.NotEqual(failed.Id, last.Id);
        Assert.Equal((Guid?)last.Id, (await ReleaseAsync(app, id)).MigrationRunId);
    }

    [Fact]
    public async Task FR153_S4_21_A_person_merging_with_nothing_migrated_is_recorded_without_an_expected_head()
    {
        var app = App();
        var (fixture, branch, _) = await ManualReleasableAsync(app);
        using var _ = fixture;
        var id = fixture.Project.Id;
        var directory = WorkingCopy(id);
        await TestGit.RunAsync(directory, Ct, "checkout", "-q", "main");
        await CommitAsync(id, Orders, "create table orders (sku text primary key);\n");
        await TestGit.RunAsync(directory, Ct, "checkout", "-q", branch);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        Assert.Empty(await RunsAsync(app, id));

        await TestGit.RunAsync(directory, Ct, "merge", "-q", "-X", "ours", "-m", "Resolve", "main");
        var resolved = await TestGit.RunAsync(directory, Ct, "rev-parse", "HEAD");
        await MergeByHandAsync(id, branch);
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        Assert.Equal(ReleaseStatus.Succeeded, (await ReleaseAsync(app, id)).Status);
        var mismatch = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MergedHeadMismatch)).Payload);
        Assert.Equal(JsonValueKind.Null, mismatch.GetProperty("expectedHeadSha").ValueKind);
        Assert.Equal(resolved, mismatch.GetProperty("mergedHeadSha").GetString());
    }
}
