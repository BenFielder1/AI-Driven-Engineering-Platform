using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Gates;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Release;
using Aide.Api.Features.Rollback;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Agents;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Releases;

/// <summary>
/// FR-42, FR-43 end to end with the Fake LLM: execution → PR → a release-eligible review → the Owner approves the
/// pre_release gate → the release job merges and tags. Local works in the working copy; GitHub merges through
/// <see cref="FakeGitHubApi"/> (which merges in a local bare repository) and pushes the tag there. Nothing reaches github.com.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ReleaseFlowTests(AideApiFactory factory)
{
    private const string Token = "ghp_phase11SecretToken0123456789abcdef";
    private const string Pulls = "/repos/acme/widgets/pulls";
    private const string Pull = Pulls + "/42";
    private const string PrUrl = "https://github.com/acme/widgets/pull/42";
    private const string Tag = "v6-aide";

    private static readonly string Basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{Token}"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> ReleaseEligible(WebApplicationFactory<Program>? app = null) =>
        AgentJobs.WithResponders(app ?? factory, new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));

    // ---------- Local ----------

    [Fact]
    public async Task FR42_Local_approving_pre_release_merges_the_branch_tags_it_and_marks_the_project_released()
    {
        var app = ReleaseEligible();
        using var fixture = await ReleasableAsync(app);
        var id = fixture.Project.Id;
        var branch = ExecutionRules.BranchName(fixture.Project.Slug, id);
        var branchHead = await GitAsync(id, "rev-parse", branch);

        await ApproveReleaseGateAsync(app, fixture);

        // The attempt is visible as soon as the job is queued.
        var queued = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.InProgress, Tag, 6, 1, (DateTimeOffset?)null), (queued.Status, queued.Tag, queued.ProjectVersion, queued.Attempt, queued.CompletedAt));
        Assert.Equal((ReleaseStatus.InProgress, Tag), ((await StateAsync(fixture.Viewer.Client, id)).LatestRelease!.Status, (await StateAsync(fixture.Viewer.Client, id)).LatestRelease!.Tag));

        var jobs = await AgentJobs.RunQueuedAsync(app, id);

        Assert.Equal(JobStatus.Succeeded, Assert.Single(jobs, j => j.Type == JobTypes.Release).Status);
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Release, ProjectStatus.Released, 7), (project.CurrentPhase, project.Status, project.ProjectVersion));
        var completedTransition = (await LifecycleSeed.TransitionsAsync(app, id)).Last();
        Assert.Equal((Phase.Release, Phase.Release, 7, ReleaseJobHandler.CompletedReason), (completedTransition.FromPhase, completedTransition.ToPhase, completedTransition.ProjectVersion, completedTransition.Reason));

        // Git: a merge commit on main whose second parent is the branch head, an annotated tag on it, the branch checked out again.
        var main = await GitAsync(id, "rev-parse", "main");
        Assert.Equal(branchHead, await GitAsync(id, "rev-parse", "main^2"));
        Assert.Equal($"[AIDE] Release {Tag}: {fixture.Project.Name}", await GitAsync(id, "log", "-1", "--format=%s", "main"));
        Assert.Equal("tag", await GitAsync(id, "cat-file", "-t", Tag));
        Assert.Equal(main, await GitAsync(id, "rev-parse", $"{Tag}^{{commit}}"));
        Assert.Equal(branch, await GitAsync(id, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(string.Empty, await GitAsync(id, "status", "--porcelain"));

        var release = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, main, (string?)null, 1, (string?)null), (release.Status, release.MergeCommitSha, release.PullRequestUrl, release.Attempt, release.ErrorCode));
        Assert.NotNull(release.CompletedAt);
        Assert.Equal(ReleaseStatus.Succeeded, (await StateAsync(fixture.Viewer.Client, id)).LatestRelease!.Status);

        var started = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseStarted)).Payload);
        Assert.Equal((Tag, 1), (started.GetProperty("tag").GetString(), started.GetProperty("attempt").GetInt32()));
        var completed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseCompleted)).Payload);
        Assert.Equal((Tag, 1, main, false), (completed.GetProperty("tag").GetString(), completed.GetProperty("attempt").GetInt32(), completed.GetProperty("mergeCommitSha").GetString(), completed.GetProperty("alreadyTagged").GetBoolean()));
    }

    [Fact]
    public async Task FR42_A_redelivered_release_finds_its_tag_and_is_a_no_op_and_a_tag_elsewhere_is_never_moved()
    {
        var app = ReleaseEligible();
        using var fixture = await ReleasableAsync(app);
        var id = fixture.Project.Id;
        await ApproveReleaseGateAsync(app, fixture);
        var job = Assert.Single(await AgentJobs.RunQueuedAsync(app, id), j => j.Type == JobTypes.Release);
        var main = await GitAsync(id, "rev-parse", "main");
        var tagObject = await GitAsync(id, "rev-parse", Tag);

        // The merge and tag happened but recording them didn't (e.g. the worker died): the job is delivered again.
        await UnrecordAsync(app, id);
        await RedeliverAsync(app, job);

        Assert.Equal((Phase.Release, ProjectStatus.Released), ((await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(app, id)).Status));
        Assert.Equal((main, tagObject), (await GitAsync(id, "rev-parse", "main"), await GitAsync(id, "rev-parse", Tag))); // no second merge, same tag
        var noOp = LifecycleSeed.Payload((await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseCompleted)).Last().Payload);
        Assert.True(noOp.GetProperty("alreadyTagged").GetBoolean());
        Assert.Equal((ReleaseStatus.Succeeded, main), ((await LatestAsync(fixture.Viewer.Client, id)).Status, (await LatestAsync(fixture.Viewer.Client, id)).MergeCommitSha));

        // A tag of that name on a commit without the branch is never moved: the release takes the next free tag.
        await UnrecordAsync(app, id);
        await GitAsync(id, "tag", "-f", "-a", Tag, "-m", "Someone else's tag", "main^1");
        var elsewhere = await GitAsync(id, "rev-parse", Tag);
        await RedeliverAsync(app, job);

        var suffixed = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, Tag + ".2", main), (suffixed.Status, suffixed.Tag, suffixed.MergeCommitSha));
        Assert.Equal(ProjectStatus.Released, (await LifecycleSeed.ProjectAsync(app, id)).Status);
        Assert.Equal(elsewhere, await GitAsync(id, "rev-parse", Tag));
        Assert.Equal(main, await GitAsync(id, "rev-parse", $"{Tag}.2^{{commit}}"));
        Assert.Equal(main, await GitAsync(id, "rev-parse", "main"));
    }

    [Fact]
    public async Task FR42_Re_release_after_a_rollback_with_new_commits_tags_the_next_free_v_N_aide_k()
    {
        var app = ReleaseEligible();
        using var fixture = await ReleasableAsync(app);
        var id = fixture.Project.Id;
        var branch = ExecutionRules.BranchName(fixture.Project.Slug, id);
        await ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        var firstRelease = await GitAsync(id, "rev-parse", $"{Tag}^{{commit}}");

        // Roll back to v5 (review), fix something on the branch, pass review again: the release reuses version 6.
        await ProjectApi.ReadAsync<RollbackDto>(await RollbackAsync(fixture.Owner.Client, id, 5));
        await File.WriteAllTextAsync(Path.Combine(WorkingCopy(id), "src", "part1.js"), "// fixed after the rollback\n", Ct);
        await GitAsync(id, "add", "src/part1.js");
        await GitAsync(id, "commit", "-q", "-m", "Fix after rollback");
        var newHead = await GitAsync(id, "rev-parse", branch);
        await ReReleaseAsync(app, fixture);

        var jobs = await AgentJobs.RunQueuedAsync(app, id);

        Assert.All(jobs.Where(j => j.Type == JobTypes.Release), j => Assert.Equal(JobStatus.Succeeded, j.Status));
        var release = await LatestAsync(fixture.Viewer.Client, id);
        var main = await GitAsync(id, "rev-parse", "main");
        Assert.Equal((ReleaseStatus.Succeeded, Tag + ".2", 6, 2, main), (release.Status, release.Tag, release.ProjectVersion, release.Attempt, release.MergeCommitSha));
        Assert.Equal(newHead, await GitAsync(id, "rev-parse", "main^2"));
        Assert.Equal(main, await GitAsync(id, "rev-parse", $"{Tag}.2^{{commit}}"));
        Assert.Equal(firstRelease, await GitAsync(id, "rev-parse", $"{Tag}^{{commit}}")); // never moved
        Assert.Equal($"[AIDE] Release {Tag}.2: {fixture.Project.Name}", await GitAsync(id, "log", "-1", "--format=%s", "main"));
        Assert.Equal((Phase.Release, ProjectStatus.Released, 7), ((await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(app, id)).Status, (await LifecycleSeed.ProjectAsync(app, id)).ProjectVersion));
        var completed = LifecycleSeed.Payload((await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseCompleted)).Last().Payload);
        Assert.Equal((Tag + ".2", false), (completed.GetProperty("tag").GetString(), completed.GetProperty("alreadyTagged").GetBoolean()));

        // Delivered again: the suffixed tag holds the release, so it is a no-op.
        await UnrecordAsync(app, id);
        await RedeliverAsync(app, (await LifecycleSeed.JobsAsync(app, id)).Where(j => j.Type == JobTypes.Release).OrderBy(j => j.CreatedAt).Last());
        Assert.Equal(main, await GitAsync(id, "rev-parse", "main"));
        var again = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, Tag + ".2"), (again.Status, again.Tag));
        Assert.True(LifecycleSeed.Payload((await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseCompleted)).Last().Payload).GetProperty("alreadyTagged").GetBoolean());
        Assert.Equal(string.Empty, await GitAsync(id, "tag", "--list", $"{Tag}.3"));
    }

    [Fact]
    public async Task FR42_Re_release_after_a_rollback_without_new_commits_succeeds_with_the_existing_tag()
    {
        var app = ReleaseEligible();
        using var fixture = await ReleasableAsync(app);
        var id = fixture.Project.Id;
        await ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        var main = await GitAsync(id, "rev-parse", "main");
        var tagObject = await GitAsync(id, "rev-parse", Tag);

        await ProjectApi.ReadAsync<RollbackDto>(await RollbackAsync(fixture.Owner.Client, id, 5));
        await ReReleaseAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        // Nothing new to merge: v6-aide already holds the branch, so the release is recorded with it (no merge, no tag).
        Assert.Equal((main, tagObject), (await GitAsync(id, "rev-parse", "main"), await GitAsync(id, "rev-parse", Tag)));
        Assert.Equal(string.Empty, await GitAsync(id, "tag", "--list", $"{Tag}.*"));
        var history = await ProjectApi.ReadAsync<List<ReleaseDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/releases", UriKind.Relative), Ct));
        Assert.Equal([(2, ReleaseStatus.Succeeded, Tag), (1, ReleaseStatus.Succeeded, Tag)], history.Select(r => (r.Attempt, r.Status, r.Tag)));
        Assert.All(history, r => Assert.Equal(6, r.ProjectVersion));
        Assert.True(LifecycleSeed.Payload((await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseCompleted)).Last().Payload).GetProperty("alreadyTagged").GetBoolean());
        Assert.Equal((Phase.Release, ProjectStatus.Released, 7), ((await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(app, id)).Status, (await LifecycleSeed.ProjectAsync(app, id)).ProjectVersion));
    }

    [Fact]
    public async Task FR43_A_merge_conflict_fails_the_release_with_its_files_and_a_retry_after_resolution_succeeds()
    {
        var app = ReleaseEligible();
        using var fixture = await ReleasableAsync(app);
        var id = fixture.Project.Id;
        var branch = ExecutionRules.BranchName(fixture.Project.Slug, id);

        // The default branch changed the same lines the project branch wrote.
        await GitAsync(id, "checkout", "-q", "main");
        Directory.CreateDirectory(Path.Combine(WorkingCopy(id), "src"));
        await File.WriteAllTextAsync(Path.Combine(WorkingCopy(id), "src", "part1.js"), "// written on main\n", Ct);
        await GitAsync(id, "add", "src/part1.js");
        await GitAsync(id, "commit", "-q", "-m", "Hotfix on main");
        await GitAsync(id, "checkout", "-q", branch);
        var mainBefore = await GitAsync(id, "rev-parse", "main");

        await ApproveReleaseGateAsync(app, fixture);
        var jobs = await AgentJobs.RunQueuedAsync(app, id);

        Assert.Equal(JobStatus.Succeeded, Assert.Single(jobs, j => j.Type == JobTypes.Release).Status); // the job ended; the release failed
        var failed = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.MergeConflict, 1), (failed.Status, failed.ErrorCode, failed.Attempt));
        Assert.Equal(["src/part1.js"], failed.ConflictingFiles);
        Assert.NotNull(failed.CompletedAt);
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Release, ProjectStatus.Releasing, 6), (project.CurrentPhase, project.Status, project.ProjectVersion));
        Assert.Equal(mainBefore, await GitAsync(id, "rev-parse", "main")); // nothing merged
        Assert.False(File.Exists(Path.Combine(WorkingCopy(id), ".git", "MERGE_HEAD")));
        Assert.Equal(branch, await GitAsync(id, "symbolic-ref", "--short", "HEAD"));
        var failedEvent = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseFailed)).Payload);
        Assert.Equal((Tag, 1, ErrorCodes.MergeConflict), (failedEvent.GetProperty("tag").GetString(), failedEvent.GetProperty("attempt").GetInt32(), failedEvent.GetProperty("errorCode").GetString()));
        Assert.Equal(["src/part1.js"], failedEvent.GetProperty("conflictingFiles").EnumerateArray().Select(f => f.GetString()));

        // Only Owners retry.
        await Problems.AssertAsync(await RetryAsync(fixture.Contributor.Client, id), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        // Resolve on the project branch (keep the branch's version), then retry.
        await GitAsync(id, "merge", "-q", "-s", "ours", "-m", "Resolve conflict with main", "main");
        var retried = await ProjectApi.ReadAsync<ReleaseDto>(await RetryAsync(fixture.Owner.Client, id), HttpStatusCode.Accepted);
        Assert.Equal((ReleaseStatus.InProgress, 2, Tag, 6), (retried.Status, retried.Attempt, retried.Tag, retried.ProjectVersion));
        await Problems.AssertAsync(await RetryAsync(fixture.Owner.Client, id), HttpStatusCode.Conflict, ErrorCodes.ReleaseInProgress);
        var retryJob = Assert.Single(await LifecycleSeed.JobsAsync(app, id), j => j.Type == JobTypes.Release && j.Status == JobStatus.Queued);
        Assert.Equal(ReleaseRecords.Retried, LifecycleSeed.Payload(retryJob.Payload).GetProperty("trigger").GetString());

        await AgentJobs.RunQueuedAsync(app, id);

        project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Release, ProjectStatus.Released, 7), (project.CurrentPhase, project.Status, project.ProjectVersion));
        Assert.Equal(await GitAsync(id, "rev-parse", "main"), await GitAsync(id, "rev-parse", $"{Tag}^{{commit}}"));
        var history = await ProjectApi.ReadAsync<List<ReleaseDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/releases", UriKind.Relative), Ct));
        Assert.Equal([(2, ReleaseStatus.Succeeded), (1, ReleaseStatus.Failed)], history.Select(r => (r.Attempt, r.Status)));
        Assert.Equal(retried.Id, history[0].Id);

        // Released: nothing to retry.
        await Problems.AssertAsync(await RetryAsync(fixture.Owner.Client, id), HttpStatusCode.Conflict, ErrorCodes.NotReleasing);
    }

    [Fact]
    public async Task FR43_Retry_is_NOT_RELEASING_outside_the_release_phase_and_releases_are_members_only()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        await Problems.AssertAsync(await RetryAsync(fixture.Owner.Client, id), HttpStatusCode.Conflict, ErrorCodes.NotReleasing);
        await Problems.AssertAsync(await RetryAsync(fixture.Outsider.Client, id), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await RetryAsync(fixture.Viewer.Client, id), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        Assert.Empty(await ProjectApi.ReadAsync<List<ReleaseDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/releases", UriKind.Relative), Ct)));
        await Problems.AssertAsync(
            await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/releases/latest", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(
            await fixture.Outsider.Client.GetAsync(new Uri($"{fixture.Path}/releases", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Null((await StateAsync(fixture.Viewer.Client, id)).LatestRelease);
    }

    [Fact]
    public async Task FR43_A_retry_while_the_release_job_is_queued_is_RELEASE_IN_PROGRESS()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory, reviewCycle: 1);
        await LifecycleSeed.AddReportAsync(factory, id, 5, Domain.Review.ReviewDecision.ReleaseEligible);
        var gate = await LifecycleSeed.AddGateAsync(factory, id, GateType.PreRelease);
        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve"));

        await Problems.AssertAsync(await RetryAsync(fixture.Owner.Client, id), HttpStatusCode.Conflict, ErrorCodes.ReleaseInProgress);

        // Even with the attempt recorded as failed, a queued job means a release is in progress.
        await ProjectApi.WithDbAsync(factory, db => db.Releases.Where(r => r.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReleaseStatus.Failed), Ct));
        await Problems.AssertAsync(await RetryAsync(fixture.Owner.Client, id), HttpStatusCode.Conflict, ErrorCodes.ReleaseInProgress);
    }

    // ---------- Rollback around a release (FR-44 with FR-42) ----------

    [Fact]
    public async Task FR44_A_released_project_rolls_back_without_rewriting_the_branch_or_the_tag()
    {
        var app = ReleaseEligible();
        using var fixture = await ReleasableAsync(app);
        var id = fixture.Project.Id;
        await ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        var main = await GitAsync(id, "rev-parse", "main");

        var versions = await ProjectApi.ReadAsync<List<ProjectVersionDto>>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/versions", UriKind.Relative), Ct));
        Assert.Equal(
            [(7, Phase.Release, RollbackTargetBlock.CurrentVersion), (6, Phase.Release, RollbackTargetBlock.ReleasePhase), (5, Phase.Review, (RollbackTargetBlock?)null)],
            versions.Take(3).Select(v => (v.Version, v.Phase, v.RollbackBlockedReason)));
        await Problems.AssertAsync(await RollbackAsync(fixture.Owner.Client, id, 6), HttpStatusCode.BadRequest, ErrorCodes.InvalidTargetVersion);

        var rollback = await ProjectApi.ReadAsync<RollbackDto>(await RollbackAsync(fixture.Owner.Client, id, 5));

        Assert.Equal((7, 5, Phase.Review), (rollback.FromVersion, rollback.ToVersion, rollback.RestoredPhase));
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Review, ProjectStatus.RolledBack, 5), (project.CurrentPhase, project.Status, project.ProjectVersion));
        Assert.Equal(main, await GitAsync(id, "rev-parse", "main"));
        Assert.Equal(main, await GitAsync(id, "rev-parse", $"{Tag}^{{commit}}"));
        Assert.Equal(ReleaseStatus.Succeeded, (await LatestAsync(fixture.Viewer.Client, id)).Status); // history is kept
    }

    [Fact]
    public async Task FR44_A_rollback_cancels_a_queued_release_and_is_blocked_while_the_release_job_runs()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory, reviewCycle: 1);
        await LifecycleSeed.AddTransitionAsync(factory, id, Phase.Execution, Phase.Review, 5, DateTimeOffset.UtcNow.AddMinutes(-10));
        await LifecycleSeed.AddTransitionAsync(factory, id, Phase.Planning, Phase.Execution, 4, DateTimeOffset.UtcNow.AddMinutes(-20));
        await LifecycleSeed.AddReportAsync(factory, id, 5, Domain.Review.ReviewDecision.ReleaseEligible);
        var gate = await LifecycleSeed.AddGateAsync(factory, id, GateType.PreRelease);
        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve"));
        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.Release);

        // Running: the merge can't be undone, so the rollback waits.
        await ProjectApi.WithDbAsync(factory, db => db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running), Ct));
        var running = await Problems.AssertAsync(await RollbackAsync(fixture.Owner.Client, id, 5), HttpStatusCode.Conflict, ErrorCodes.RollbackBlocked);
        Assert.Equal(["reason: release_running"], Details(running));
        Assert.Equal(Phase.Release, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);

        // Queued: the rollback cancels it and its attempt.
        await ProjectApi.WithDbAsync(factory, db => db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued), Ct));
        var rollback = await ProjectApi.ReadAsync<RollbackDto>(await RollbackAsync(fixture.Owner.Client, id, 5));

        Assert.Equal([job.Id], rollback.CancelledJobIds);
        Assert.Equal(Phase.Review, rollback.RestoredPhase);
        var release = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.ReleaseCancelled), (release.Status, release.ErrorCode));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ReleaseFailed));
    }

    [Fact]
    public async Task FR44_A_claim_committed_while_the_rollback_waits_blocks_it_instead_of_cancelling_a_running_release()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedQueuedReleaseAsync(fixture);
        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.Release);

        // A worker's claim is in flight: its statement holds the job row (running, not committed yet).
        await using var scope = factory.Services.CreateAsyncScope();
        var claimDb = scope.ServiceProvider.GetRequiredService<AideDbContext>();
        await using var claim = await claimDb.Database.BeginTransactionAsync(Ct);
        await claimDb.Database.ExecuteSqlAsync(
            $"UPDATE jobs SET status = 'running', attempts = attempts + 1, locked_by = 'race-test', locked_at = now() WHERE id = {job.Id}", Ct);

        // The rollback starts, reads the project and waits for the job row; then the claim commits.
        var rollback = Task.Run(() => RollbackAsync(fixture.Owner.Client, id, 5), Ct);
        await WaitForRollbackLockWaitAsync(rollback);
        await claim.CommitAsync(Ct);

        var blocked = await Problems.AssertAsync(await rollback, HttpStatusCode.Conflict, ErrorCodes.RollbackBlocked);
        Assert.Equal(["reason: release_running"], Details(blocked));
        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Release, ProjectStatus.Releasing, 6, (DateTimeOffset?)null), (project.CurrentPhase, project.Status, project.ProjectVersion, project.LastRollbackAt));
        Assert.Equal(JobStatus.Running, Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Id == job.Id).Status);
        Assert.Equal(ReleaseStatus.InProgress, (await LatestAsync(fixture.Viewer.Client, id)).Status);
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.RollbackCompleted));
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.ReleaseFailed));
    }

    [Fact]
    public async Task FR44_A_queued_release_retry_whose_attempt_already_merged_blocks_the_rollback()
    {
        var app = ReleaseEligible();
        using var fixture = await ReleasableAsync(app);
        var id = fixture.Project.Id;
        await ApproveReleaseGateAsync(app, fixture);

        // Tagging fails after the merge (the tag ref is locked): the merge is on record, the job will retry.
        var lockFile = Path.Combine(WorkingCopy(id), ".git", "refs", "tags", Tag + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        await File.WriteAllTextAsync(lockFile, string.Empty, Ct);
        var job = Assert.Single(await AgentJobs.RunQueuedAsync(app, id), j => j.Type == JobTypes.Release);
        Assert.NotEqual(JobStatus.Succeeded, job.Status);
        var main = await GitAsync(id, "rev-parse", "main");
        var attempt = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.InProgress, main), (attempt.Status, attempt.MergeCommitSha));
        Assert.Equal(string.Empty, await GitAsync(id, "tag", "--list"));

        // The job waits out its retry backoff (queued): cancelling it would strand a merged, untagged release.
        await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.RunAfter, DateTimeOffset.UtcNow.AddMinutes(1)), Ct));
        var blocked = await Problems.AssertAsync(await RollbackAsync(fixture.Owner.Client, id, 5), HttpStatusCode.Conflict, ErrorCodes.RollbackBlocked);
        Assert.Equal(["reason: release_partially_applied"], Details(blocked));
        Assert.Equal(JobStatus.Queued, Assert.Single(await LifecycleSeed.JobsAsync(app, id), j => j.Id == job.Id).Status);
        Assert.Equal((ReleaseStatus.InProgress, ProjectStatus.Releasing), ((await LatestAsync(fixture.Viewer.Client, id)).Status, (await LifecycleSeed.ProjectAsync(app, id)).Status));

        // The retry finishes the release: same merge, now tagged.
        File.Delete(lockFile);
        await RedeliverAsync(app, job);
        Assert.Equal((ReleaseStatus.Succeeded, main, Tag), ((await LatestAsync(fixture.Viewer.Client, id)).Status, (await LatestAsync(fixture.Viewer.Client, id)).MergeCommitSha, (await LatestAsync(fixture.Viewer.Client, id)).Tag));
        Assert.Equal(main, await GitAsync(id, "rev-parse", $"{Tag}^{{commit}}"));
        Assert.Equal(main, await GitAsync(id, "rev-parse", "main"));
    }

    // ---------- GitHub ----------

    [Fact]
    public async Task FR42_GitHub_merges_the_PR_through_the_API_and_pushes_the_annotated_tag()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var branch = new string[1];
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, Pulls, HttpStatusCode.OK, "[]")
            .On(HttpMethod.Post, Pulls, HttpStatusCode.Created, $$"""{"number":42,"html_url":"{{PrUrl}}"}""")
            .On(HttpMethod.Get, Pull, _ => (HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                number = 42,
                state = "open",
                merged = false,
                mergeable = true,
                html_url = PrUrl,
                head = new { sha = BareSha(remote, $"refs/heads/{branch[0]}") },
            })))
            .On(HttpMethod.Put, Pull + "/merge", _ => (HttpStatusCode.OK, JsonSerializer.Serialize(new { sha = MergeInBare(remote, branch[0]), merged = true })));
        await using var app = remote.App(ReleaseEligible(), api, out var git);
        using var fixture = await ProjectApi.FixtureAsync(app, git: new { provider = "github", repoUrl = BareRemote.RepoUrl, defaultBranch = "main", token = Token });
        var id = fixture.Project.Id;
        branch[0] = ExecutionRules.BranchName(fixture.Project.Slug, id);
        await StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ApproveReleaseGateAsync(app, fixture);

        await AgentJobs.RunQueuedAsync(app, id);

        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Release, ProjectStatus.Released, 7), (project.CurrentPhase, project.Status, project.ProjectVersion));
        var merge = api.Requests.Single(r => r.Method == "PUT" && r.Path == Pull + "/merge");
        Assert.Equal($"Bearer {Token}", merge.Authorization);
        using (var body = JsonDocument.Parse(merge.Body))
        {
            Assert.Equal("merge", body.RootElement.GetProperty("merge_method").GetString());
            Assert.Equal(await TestGit.RunAsync(remote.BarePath, Ct, "rev-parse", $"refs/heads/{branch[0]}"), body.RootElement.GetProperty("sha").GetString());
            Assert.Equal($"[AIDE] Release {Tag}: {fixture.Project.Name}", body.RootElement.GetProperty("commit_title").GetString());
        }

        // The tag is annotated, on the merge commit GitHub reported, and on the remote.
        var mainOnRemote = await TestGit.RunAsync(remote.BarePath, Ct, "rev-parse", "refs/heads/main");
        Assert.Equal("tag", await TestGit.RunAsync(remote.BarePath, Ct, "cat-file", "-t", $"refs/tags/{Tag}"));
        Assert.Equal(mainOnRemote, await TestGit.RunAsync(remote.BarePath, Ct, "rev-parse", $"refs/tags/{Tag}^{{commit}}"));
        var release = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, mainOnRemote, PrUrl), (release.Status, release.MergeCommitSha, release.PullRequestUrl));

        var push = Assert.Single(git.Calls, c => c.Arguments.Contains("push") && c.Arguments.Any(a => a.StartsWith("refs/tags/", StringComparison.Ordinal)));
        Assert.Contains($"Authorization: Basic {Basic}", push.Config!.Values);
        Assert.DoesNotContain(push.Arguments, a => a.Contains(Token, StringComparison.Ordinal) || a.Contains(Basic, StringComparison.Ordinal));
        var config = await File.ReadAllTextAsync(Path.Combine(WorkingCopy(id), ".git", "config"), Ct);
        Assert.DoesNotContain(Token, config, StringComparison.Ordinal);
        Assert.DoesNotContain("extraheader", config, StringComparison.OrdinalIgnoreCase);

        // Delivered again with the tag on GitHub: a no-op, no second merge.
        await UnrecordAsync(app, id);
        await RedeliverAsync(app, Assert.Single(await LifecycleSeed.JobsAsync(app, id), j => j.Type == JobTypes.Release));
        Assert.Equal(1, api.Count(HttpMethod.Put, Pull + "/merge"));
        Assert.Equal(ProjectStatus.Released, (await LifecycleSeed.ProjectAsync(app, id)).Status);
    }

    [Fact]
    public async Task FR43_GitHub_a_PR_that_can_not_be_merged_lists_the_conflicting_files()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, Pulls, HttpStatusCode.OK, "[]")
            .On(HttpMethod.Post, Pulls, HttpStatusCode.Created, $$"""{"number":42,"html_url":"{{PrUrl}}"}""")
            .On(HttpMethod.Get, Pull, HttpStatusCode.OK, $$"""{"number":42,"state":"open","merged":false,"mergeable":false,"html_url":"{{PrUrl}}"}""");
        await using var app = remote.App(ReleaseEligible(), api, out _);
        using var fixture = await ProjectApi.FixtureAsync(app, git: new { provider = "github", repoUrl = BareRemote.RepoUrl, defaultBranch = "main", token = Token });
        var id = fixture.Project.Id;
        await StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        // main on GitHub gets a conflicting src/part1.js after the branch was pushed.
        CommitInBare(remote, "src/part1.js", "// written on main\n");
        await ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var failed = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.MergeConflict), (failed.Status, failed.ErrorCode));
        Assert.Equal(["src/part1.js"], failed.ConflictingFiles);
        Assert.Equal(0, api.Count(HttpMethod.Put, Pull + "/merge"));
        Assert.Equal(ProjectStatus.Releasing, (await LifecycleSeed.ProjectAsync(app, id)).Status);
    }

    [Fact]
    public async Task FR42_GitHub_does_not_merge_when_the_PR_head_differs_from_the_fetched_branch()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, Pulls, HttpStatusCode.OK, "[]")
            .On(HttpMethod.Post, Pulls, HttpStatusCode.Created, $$"""{"number":42,"html_url":"{{PrUrl}}"}""")
            .On(HttpMethod.Get, Pull, HttpStatusCode.OK, JsonSerializer.Serialize(new { number = 42, state = "open", merged = false, mergeable = true, html_url = PrUrl, head = new { sha = new string('a', 40) } }));
        await using var app = remote.App(ReleaseEligible(), api, out _);
        using var fixture = await ProjectApi.FixtureAsync(app, git: new { provider = "github", repoUrl = BareRemote.RepoUrl, defaultBranch = "main", token = Token });
        var id = fixture.Project.Id;
        await StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ApproveReleaseGateAsync(app, fixture);

        var job = Assert.Single(await AgentJobs.RunQueuedAsync(app, id), j => j.Type == JobTypes.Release);

        // A transient failure (the next attempt fetches again); nothing was merged.
        Assert.Equal(JobStatus.Dead, job.Status); // the test runner dead-letters transient failures
        Assert.Contains("different commit", job.LastError, StringComparison.Ordinal);
        Assert.Equal(0, api.Count(HttpMethod.Put, Pull + "/merge"));
        Assert.Equal((ReleaseStatus.InProgress, (string?)null), ((await LatestAsync(fixture.Viewer.Client, id)).Status, (await LatestAsync(fixture.Viewer.Client, id)).MergeCommitSha));
    }

    // ---------- helpers ----------

    /// <summary>A project at v5 in review with a release-eligible report; approving pre_release queues the release job at v6.</summary>
    private async Task SeedQueuedReleaseAsync(ProjectFixture fixture)
    {
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Review, 5, ApprovalMode.Advisory, reviewCycle: 1);
        await LifecycleSeed.AddTransitionAsync(factory, id, Phase.Execution, Phase.Review, 5, DateTimeOffset.UtcNow.AddMinutes(-10));
        await LifecycleSeed.AddTransitionAsync(factory, id, Phase.Planning, Phase.Execution, 4, DateTimeOffset.UtcNow.AddMinutes(-20));
        await LifecycleSeed.AddReportAsync(factory, id, 5, Domain.Review.ReviewDecision.ReleaseEligible);
        var gate = await LifecycleSeed.AddGateAsync(factory, id, GateType.PreRelease);
        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, id, gate.Id, "approve"));
    }

    /// <summary>After a rollback to v5 (review): a new release-eligible review and the pre_release gate approved → release at v6.</summary>
    private static async Task ReReleaseAsync(WebApplicationFactory<Program> app, ProjectFixture fixture)
    {
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Review, 5, ApprovalMode.Advisory, reviewCycle: 1);
        await LifecycleSeed.AddReportAsync(app, id, 5, Domain.Review.ReviewDecision.ReleaseEligible);
        await LifecycleSeed.AddGateAsync(app, id, GateType.PreRelease);
        await ApproveReleaseGateAsync(app, fixture);
    }

    /// <summary>Waits until the rollback request is blocked on a row lock (its <c>FOR UPDATE</c> on the job rows).</summary>
    private async Task WaitForRollbackLockWaitAsync(Task rollback)
    {
        for (var i = 0; i < 200; i++)
        {
            Assert.False(rollback.IsCompleted, "The rollback finished without waiting for the job row lock.");
            var waiting = await ProjectApi.WithDbAsync(factory, db => db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_stat_activity
                WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%FOR UPDATE%'
                """).SingleAsync(Ct));
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail("The rollback never waited for the job row lock.");
    }

    private static List<string?> Details(JsonElement problem) => [.. problem.GetProperty("details").EnumerateArray().Select(d => d.GetString())];

    /// <summary>A Local project (advisory, version 4) whose 2-step plan ran, opened its PR and passed review: a pre_release gate is pending.</summary>
    private static async Task<ProjectFixture> ReleasableAsync(WebApplicationFactory<Program> app)
    {
        var fixture = await ProjectApi.FixtureAsync(app);
        await StartExecutionAsync(app, fixture);
        var jobs = await AgentJobs.RunQueuedAsync(app, fixture.Project.Id);
        Assert.True(jobs.All(j => j.Status == JobStatus.Succeeded), string.Join("; ", jobs.Select(j => $"{j.Type}:{j.Status}:{j.LastError}")));
        return fixture;
    }

    private static async Task StartExecutionAsync(WebApplicationFactory<Program> app, ProjectFixture fixture)
    {
        var id = fixture.Project.Id;
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
                DeclaredFiles = [new DeclaredFile($"src/part{n}.js", FileAction.Create)],
                Dependencies = n == 1 ? [] : [n - 1],
            }));
            await db.SaveChangesAsync(Ct);
        });
        await ProjectApi.ReadAsync<ExecutionStateDto>(
            await fixture.Contributor.Client.PostAsJsonAsync($"{fixture.Path}/execution/start", new { }, Ct), HttpStatusCode.Accepted);
    }

    private static async Task ApproveReleaseGateAsync(WebApplicationFactory<Program> app, ProjectFixture fixture)
    {
        var gate = Assert.Single(await LifecycleSeed.GatesAsync(app, fixture.Project.Id), g => g.Status == GateStatus.Pending);
        Assert.Equal(GateType.PreRelease, gate.Type);
        await ProjectApi.ReadAsync<GateDto>(await LifecycleSeed.DecideAsync(fixture.Owner.Client, fixture.Project.Id, gate.Id, "approve"));
        var project = await LifecycleSeed.ProjectAsync(app, fixture.Project.Id);
        Assert.Equal((Phase.Release, ProjectStatus.Releasing, 6), (project.CurrentPhase, project.Status, project.ProjectVersion));
    }

    /// <summary>Puts the project back to release/releasing at version 6 with the latest attempt in progress (the recording was lost).</summary>
    private static async Task UnrecordAsync(WebApplicationFactory<Program> app, Guid id)
    {
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Release, 6, ApprovalMode.Advisory, ProjectStatus.Releasing, reviewCycle: 1);
        await ProjectApi.WithDbAsync(app, async db =>
        {
            var latest = await db.Releases.Where(r => r.ProjectId == id).OrderByDescending(r => r.Attempt).FirstAsync(Ct);
            await db.Releases.Where(r => r.Id == latest.Id).ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, ReleaseStatus.InProgress)
                    .SetProperty(r => r.MergeCommitSha, (string?)null)
                    .SetProperty(r => r.CompletedAt, (DateTimeOffset?)null),
                Ct);
        });
    }

    private static async Task RedeliverAsync(WebApplicationFactory<Program> app, Job job)
    {
        await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.LastError, (string?)null), Ct));
        await AgentJobs.RunAsync(app, await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct)));
    }

    private Task<string> GitAsync(Guid id, params string[] arguments) => TestGit.RunAsync(WorkingCopy(id), Ct, arguments);

    private string WorkingCopy(Guid id) => Path.Combine(factory.WorkspacesRoot, id.ToString("D"));

    /// <summary>The commit <paramref name="revision"/> names in the bare repository.</summary>
    private static string BareSha(BareRemote remote, string revision) =>
        TestGit.RunAsync(remote.BarePath, CancellationToken.None, "rev-parse", revision).GetAwaiter().GetResult();

    /// <summary>What GitHub's merge API does: a merge commit of the project branch on main, in the bare repository.</summary>
    private static string MergeInBare(BareRemote remote, string branch)
    {
        var clone = Path.Combine(remote.Root, "merge-" + Guid.NewGuid().ToString("N"));
        Run(remote.Root, "clone", "-q", remote.BarePath, clone);
        Run(clone, "merge", "-q", "--no-ff", "-m", "Merge pull request #42", $"origin/{branch}");
        Run(clone, "push", "-q", "origin", "main");
        return Run(clone, "rev-parse", "HEAD");
    }

    /// <summary>A commit on main in the bare repository that writes <paramref name="path"/>.</summary>
    private static void CommitInBare(BareRemote remote, string path, string content)
    {
        var clone = Path.Combine(remote.Root, "edit-" + Guid.NewGuid().ToString("N"));
        Run(remote.Root, "clone", "-q", remote.BarePath, clone);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(clone, path))!);
        File.WriteAllText(Path.Combine(clone, path), content);
        Run(clone, "add", path);
        Run(clone, "commit", "-q", "-m", "Change on main");
        Run(clone, "push", "-q", "origin", "main");
    }

    private static string Run(string directory, params string[] arguments) =>
        TestGit.RunAsync(directory, CancellationToken.None, arguments).GetAwaiter().GetResult();

    private static Task<HttpResponseMessage> RetryAsync(HttpClient client, Guid id) =>
        client.PostAsJsonAsync($"/api/projects/{id}/release", new { }, Ct);

    private static Task<HttpResponseMessage> RollbackAsync(HttpClient client, Guid id, int targetVersion) =>
        client.PostAsJsonAsync($"/api/projects/{id}/rollback", new { targetVersion, reason = "Back to an earlier version" }, Ct);

    private static async Task<ReleaseDto> LatestAsync(HttpClient client, Guid id) =>
        await ProjectApi.ReadAsync<ReleaseDto>(await client.GetAsync(new Uri($"/api/projects/{id}/releases/latest", UriKind.Relative), Ct));

    private static async Task<ProjectStateDto> StateAsync(HttpClient client, Guid id) =>
        await ProjectApi.ReadAsync<ProjectStateDto>(await client.GetAsync(new Uri($"/api/projects/{id}/state", UriKind.Relative), Ct));
}
