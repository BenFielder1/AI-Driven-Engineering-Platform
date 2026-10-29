using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Git;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Release;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.Domain.Review;
using Aide.Infrastructure.Agents;
using Aide.IntegrationTests.Releases;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Bitbucket;

/// <summary>
/// M28 slice 2 end to end with the Fake LLM: Bitbucket merge readiness from the mergeability checks (FR-138), commit statuses
/// as a review input (FR-139), the merge (200, 202 + task-status, 555 retried, 400, 409), the annotated tag with the release
/// notes and the <c>manual</c> strategy with the merge poll (FR-140). Bitbucket is a stub API over a local bare repository
/// (<c>Bitbucket:GitBaseUrlOverride</c>, tests only); nothing reaches a real Bitbucket.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class BitbucketReleaseTests(AideApiFactory factory)
{
    private const string RepoUuid = "{r-acme-widgets}";
    private const string FullName = "acme/widgets";
    private const string Token = "bb-release-0123456789";
    private const string Repo = "/2.0/repositories/acme/widgets";
    private const string PullRequest = Repo + "/pullrequests/3";
    private const string TaskStatus = PullRequest + "/merge/task-status/t-1";
    private const string Tag = "v0.1.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- FR-138 ----------

    [Fact]
    public async Task FR138_Given_a_Bitbucket_PR_whose_require_passing_builds_check_is_blocking_and_failed_then_reason_checks_failing()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var bitbucket = new BitbucketState(remote) { Opened = true };
        bitbucket.Checks = [new { type = "standard_merge_check", key = "require_passing_builds_to_merge", status = "FAILED", required = true, blocking = true }];
        var (app, fixture) = await SetUpAsync(bitbucket, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Review, 5, ApprovalMode.Advisory);
        await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.Where(v => v.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.PullRequestNumber, 3).SetProperty(v => v.PullRequestUrl, BitbucketState.PullRequestUrl), Ct));

        var readiness = await ProjectApi.ReadAsync<MergeReadinessDto>(
            await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/git/merge-readiness?refresh=true", UriKind.Relative), Ct));

        Assert.Equal((false, false), (readiness.CanMerge, readiness.WillAutoMerge));
        Assert.Equal([MergeReadinessReasonCode.ChecksFailing], readiness.Reasons.Select(r => r.Code));

        // §4.19 budget: the one mergeability call, with the binding connection's token.
        var calls = bitbucket.Api.Requests.Where(r => r.Path.StartsWith(PullRequest, StringComparison.Ordinal)).ToList();
        Assert.Equal([PullRequest + "/mergeability/checks"], calls.Select(r => r.Path));
        Assert.Equal($"Bearer {Token}", calls[0].Authorization);

        // An answer AIDE can't use is UPSTREAM_ERROR (502), never a 500.
        bitbucket.Garbled = true;
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<MergeReadinessThrottle>(app.Services).Forget(id);
        await Problems.AssertAsync(
            await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/git/merge-readiness?refresh=true", UriKind.Relative), Ct),
            HttpStatusCode.BadGateway,
            ErrorCodes.UpstreamError);
    }

    // ---------- FR-139 ----------

    [Fact]
    public async Task FR139_A_failed_build_required_by_the_mergeability_checks_is_a_major_testing_finding_and_others_minor()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var bitbucket = new BitbucketState(remote)
        {
            Checks = [new { type = "standard_merge_check", key = "require_passing_builds_to_merge", status = "FAILED", required = true, blocking = true }],
            Statuses = [new { key = "test", name = "test", state = "FAILED", url = "https://ci.example/7" }],
        };
        var (app, fixture) = await SetUpAsync(bitbucket, remote);
        using var session = fixture;
        var id = fixture.Project.Id;

        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var report = await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        var finding = Assert.Single(report.Violations, v => v.Message == "required build test failed");
        Assert.Equal((ReviewCategory.Testing, Severity.Major, "test: https://ci.example/7"), (finding.Category, finding.Severity, finding.Evidence));
        Assert.Contains(bitbucket.Api.Requests, r => r.Method == "GET" && r.Path.EndsWith("/statuses", StringComparison.Ordinal) && r.Authorization == $"Bearer {Token}");
    }

    // ---------- FR-137, FR-140: aide_merges ----------

    [Fact]
    public async Task FR137_FR140_Given_a_Bitbucket_project_when_the_release_completes_then_the_annotated_tag_v0_1_0_is_on_merge_commit_hash_with_the_notes()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var bitbucket = new BitbucketState(remote);
        var (app, fixture) = await SetUpAsync(bitbucket, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        // FR-137: one draft PR from aide/<slug>/v0.1.0 to main, linking back to AIDE, linked from the Release page.
        var created = GitLabHarness.Body(Assert.Single(bitbucket.Api.Requests, r => r.Method == "POST" && r.Path == Repo + "/pullrequests"));
        Assert.Equal(
            (bitbucket.Branch, "main", true, false),
            (created.GetProperty("source").GetProperty("branch").GetProperty("name").GetString(), created.GetProperty("destination").GetProperty("branch").GetProperty("name").GetString(),
                created.GetProperty("draft").GetBoolean(), created.GetProperty("close_source_branch").GetBoolean()));
        Assert.StartsWith($"aide/{fixture.Project.Slug}/v0.1.0", bitbucket.Branch, StringComparison.Ordinal);
        Assert.Contains($"/projects/{id}", created.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(((int?)3, (string?)BitbucketState.PullRequestUrl), await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == id).Select(v => new ValueTuple<int?, string?>(v.PullRequestNumber, v.PullRequestUrl)).SingleAsync(Ct)));

        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        // Marked ready (draft: false) before the merge; merge_commit strategy; the source branch kept.
        var requests = bitbucket.Api.Requests.ToList();
        var readyAt = requests.FindIndex(r => r.Method == "PUT" && r.Path == PullRequest && GitLabHarness.Body(r).TryGetProperty("draft", out var d) && !d.GetBoolean());
        var mergeAt = requests.FindIndex(r => r.Method == "POST" && r.Path == PullRequest + "/merge");
        Assert.InRange(readyAt, 0, mergeAt - 1);
        var merge = GitLabHarness.Body(requests[mergeAt]);
        Assert.Equal(("merge_commit", false), (merge.GetProperty("merge_strategy").GetString(), merge.GetProperty("close_source_branch").GetBoolean()));

        // The annotated tag on merge_commit.hash (full SHA), AIDE's notes as its message.
        var tag = GitLabHarness.Body(Assert.Single(requests, r => r.Method == "POST" && r.Path == Repo + "/refs/tags"));
        Assert.Equal((Tag, bitbucket.MergeSha), (tag.GetProperty("name").GetString(), tag.GetProperty("target").GetProperty("hash").GetString()));
        var notes = tag.GetProperty("message").GetString()!;
        Assert.StartsWith($"## {fixture.Project.Name} 0.1.0", notes, StringComparison.Ordinal);
        Assert.Contains("1. Step 1", notes, StringComparison.Ordinal);
        Assert.Equal(bitbucket.MergeSha, ReleaseFlowTests.BareSha(remote, $"refs/tags/{Tag}^{{commit}}"));
        Assert.Equal("tag", ReleaseFlowTests.Run(remote.BarePath, "cat-file", "-t", Tag));

        var released = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, bitbucket.MergeSha, BitbucketState.TagUrl), (released.Status, released.MergeCommitSha, released.GithubReleaseUrl));
        var stored = await ProjectApi.ReadAsync<ReleaseNotesDto>(await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{id}/releases/{released.Id}/notes", UriKind.Relative), Ct));
        Assert.Equal((released.Id, notes), (stored.ReleaseId, stored.Notes));
        await Problems.AssertAsync(
            await fixture.Viewer.Client.GetAsync(new Uri($"/api/projects/{id}/releases/{Guid.CreateVersion7()}/notes", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal((ProjectStatus.Released, "0.1.0"), ((await LifecycleSeed.ProjectAsync(app, id)).Status, (await LifecycleSeed.ProjectAsync(app, id)).ReleasedVersion));
        var completed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseCompleted)).Payload);
        Assert.Equal(("bitbucket", false), (completed.GetProperty("provider").GetString(), completed.GetProperty("mergedExternally").GetBoolean()));

        var text = await ProjectApi.WithDbAsync(app, async db =>
            string.Join('\n', await db.Events.AsNoTracking().Where(e => e.ProjectId == id).Select(e => e.Payload).ToListAsync(Ct))
            + string.Join('\n', await db.Jobs.AsNoTracking().Where(j => j.ProjectId == id).Select(j => j.Payload + (j.LastError ?? string.Empty)).ToListAsync(Ct)));
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR140_A_202_merge_is_followed_through_task_status_with_the_fast_forward_strategy()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var bitbucket = new BitbucketState(remote) { Asynchronous = true };
        var (app, fixture) = await SetUpAsync(bitbucket, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        var settings = await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "fast_forward" }));
        Assert.Equal(MergeStrategy.FastForward, settings.MergeStrategy);
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var merge = GitLabHarness.Body(Assert.Single(bitbucket.Api.Requests, r => r.Method == "POST" && r.Path == PullRequest + "/merge"));
        Assert.Equal("fast_forward", merge.GetProperty("merge_strategy").GetString());
        Assert.Equal(1, bitbucket.Api.Count(HttpMethod.Get, TaskStatus));
        var released = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, bitbucket.MergeSha), (released.Status, released.MergeCommitSha));
        Assert.Equal(bitbucket.MergeSha, ReleaseFlowTests.BareSha(remote, $"refs/tags/{Tag}^{{commit}}"));
        Assert.Empty(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseMergedExternally));
    }

    [Fact]
    public async Task FR140_A_202_merge_still_pending_blocks_rollback_then_the_retry_completes_it_as_AIDEs_own_merge()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var bitbucket = new BitbucketState(remote) { Asynchronous = true, PendingPolls = 10 };
        var (app, fixture) = await SetUpAsync(bitbucket, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);

        // The first attempt submits the merge, but Bitbucket is still merging after every task-status read: retry later.
        await AgentJobs.RunCoreAsync(app, await ReleaseJobAsync(app, id));
        var attempt = await ProjectApi.WithDbAsync(app, db => db.Releases.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        Assert.Equal(ReleaseStatus.InProgress, attempt.Status);
        Assert.NotNull(attempt.MergeSubmittedAt);
        Assert.Equal(JobStatus.Queued, (await ReleaseJobAsync(app, id)).Status);

        // A merge may be happening: the rollback waits for the release.
        var blocked = await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/rollback", new { targetCheckpoint = 1, reason = "Back" }, Ct), HttpStatusCode.Conflict, ErrorCodes.RollbackBlocked);
        Assert.Equal(["reason: release_partially_applied"], blocked.GetProperty("details").EnumerateArray().Select(d => d.GetString()));

        // The retry finds the pull request merged: AIDE's own merge (not RELEASE_MERGED_EXTERNALLY), tagged and released.
        await AgentJobs.RunCoreAsync(app, await ReleaseJobAsync(app, id));
        var released = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, bitbucket.MergeSha), (released.Status, released.MergeCommitSha));
        Assert.Equal(1, bitbucket.Api.Count(HttpMethod.Post, PullRequest + "/merge"));
        Assert.Empty(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseMergedExternally));
        var completed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseCompleted)).Payload);
        Assert.False(completed.GetProperty("mergedExternally").GetBoolean());
    }

    [Fact]
    public async Task FR140_A_555_merge_timeout_is_retried_and_the_release_completes()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var bitbucket = new BitbucketState(remote) { TimeoutsFirst = 1 };
        var (app, fixture) = await SetUpAsync(bitbucket, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        Assert.Equal(2, bitbucket.Api.Count(HttpMethod.Post, PullRequest + "/merge"));
        var released = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, bitbucket.MergeSha), (released.Status, released.MergeCommitSha));
        Assert.Empty(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseMergedExternally));
    }

    [Fact]
    public async Task FR140_A_400_not_mergeable_awaits_the_merge_with_the_readiness_reasons()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var bitbucket = new BitbucketState(remote);
        bitbucket.MergeResponse = _ =>
        {
            bitbucket.Checks = [new { type = "standard_merge_check", key = "require_approvals_to_merge", status = "FAILED", required = true, blocking = true }];
            return (HttpStatusCode.BadRequest, """{"type":"error","error":{"message":"You can't merge until you resolve all merge checks."}}""", null);
        };
        var (app, fixture) = await SetUpAsync(bitbucket, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var awaiting = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal(ReleaseStatus.AwaitingMerge, awaiting.Status);
        Assert.Contains(MergeReadinessReasonCode.ReviewRequired, awaiting.MergeReasons!.Select(r => r.Code));
        Assert.Equal(0, bitbucket.Api.Count(HttpMethod.Post, Repo + "/refs/tags"));
    }

    [Fact]
    public async Task FR140_A_409_refs_changed_fails_the_attempt_for_a_new_review_and_nothing_is_tagged()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var bitbucket = new BitbucketState(remote);
        bitbucket.MergeResponse = _ => (HttpStatusCode.Conflict, """{"type":"error","error":{"message":"The source branch changed."}}""", null);
        var (app, fixture) = await SetUpAsync(bitbucket, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var failed = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.GitError), (failed.Status, failed.ErrorCode));
        Assert.Contains("needs a new review", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, bitbucket.Api.Count(HttpMethod.Post, Repo + "/refs/tags"));
        Assert.Equal((Phase.Release, ProjectStatus.Releasing), ((await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(app, id)).Status));
    }

    // ---------- FR-140: manual ----------

    [Fact]
    public async Task FR140_Manual_strategy_marks_the_PR_ready_awaits_the_merge_then_tags_it_when_merged_on_Bitbucket()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var bitbucket = new BitbucketState(remote);
        var (app, fixture) = await SetUpManualAsync(bitbucket, remote);
        using var session = fixture;
        var id = fixture.Project.Id;

        Assert.Equal(0, bitbucket.Api.Count(HttpMethod.Post, PullRequest + "/merge"));
        Assert.False(bitbucket.Draft);
        var awaiting = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.AwaitingMerge, MergeStrategy.Manual, BitbucketState.PullRequestUrl), (awaiting.Status, awaiting.MergeStrategy, awaiting.PullRequestUrl));
        Assert.Equal(MergeReadinessReasonCode.ManualStrategy, awaiting.MergeReasons![0].Code);

        // Still open: the next poll is at least a minute away (the §4.19 cadence: 2 minutes in the first hour).
        var since = DateTimeOffset.UtcNow;
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));
        var poll = await PollJobAsync(app, id);
        Assert.Equal((JobStatus.Queued, 0), (poll.Status, poll.Attempts));
        Assert.True(poll.RunAfter >= since.AddMinutes(1), $"{poll.RunAfter:O}");

        // Merged on Bitbucket: the poll tags merge_commit.hash and releases.
        bitbucket.MergedExternally(ReleaseFlowTests.MergeInBare(remote, bitbucket.Branch!));
        await AgentJobs.RunCoreAsync(app, poll);

        var released = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, bitbucket.MergeSha), (released.Status, released.MergeCommitSha));
        Assert.Equal(bitbucket.MergeSha, ReleaseFlowTests.BareSha(remote, $"refs/tags/{Tag}^{{commit}}"));
        Assert.Equal("bitbucket", LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseMergedExternally)).Payload).GetProperty("provider").GetString());
        Assert.Equal(0, bitbucket.Api.Count(HttpMethod.Post, PullRequest + "/merge"));
    }

    [Fact]
    public async Task FR140_A_declined_PR_fails_with_PR_CLOSED_and_the_project_stays_in_release()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var bitbucket = new BitbucketState(remote);
        var (app, fixture) = await SetUpManualAsync(bitbucket, remote);
        using var session = fixture;
        var id = fixture.Project.Id;

        bitbucket.State = "DECLINED";
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var failed = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.PrClosed), (failed.Status, failed.ErrorCode));
        Assert.Equal((Phase.Release, ProjectStatus.Releasing), ((await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(app, id)).Status));
    }

    [Fact]
    public async Task FR136_FR140_A_PR_404_after_losing_repository_access_is_not_PR_CLOSED()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var bitbucket = new BitbucketState(remote);
        var (app, fixture) = await SetUpManualAsync(bitbucket, remote);
        using var session = fixture;
        var id = fixture.Project.Id;

        bitbucket.Hidden = true;
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        Assert.Equal(ReleaseStatus.AwaitingMerge, (await LatestAsync(fixture.Viewer.Client, id)).Status);
        Assert.Contains(bitbucket.Api.Requests, r => r.Method == "GET" && r.Path == Repo);
    }

    [Fact]
    public async Task S4_19_A_Bitbucket_429_requeues_the_merge_poll_without_counting_an_attempt()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var bitbucket = new BitbucketState(remote);
        var (app, fixture) = await SetUpManualAsync(bitbucket, remote);
        using var session = fixture;
        var id = fixture.Project.Id;

        bitbucket.RateLimited = true;
        var before = DateTimeOffset.UtcNow;
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var poll = await PollJobAsync(app, id);
        Assert.Equal((JobStatus.Queued, 0), (poll.Status, poll.Attempts));
        Assert.InRange(poll.RunAfter, before.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(11));
        Assert.StartsWith(ErrorCodes.BitbucketRateLimited, poll.LastError, StringComparison.Ordinal);
    }

    // ---------- helpers ----------

    private async Task<(WebApplicationFactory<Program> App, ProjectFixture Fixture)> SetUpAsync(BitbucketState bitbucket, BareRemote remote)
    {
        var app = BitbucketHarness.App(
            AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] })),
            bitbucket.Api,
            settings: new Dictionary<string, string>
            {
                ["Bitbucket:GitBaseUrlOverride"] = remote.BaseUrl,
                ["Bitbucket:AllowInsecureGitBaseUrl"] = "true",
            });
        var owner = await ApiSession.RegisterAsync(app);
        var connection = await BitbucketHarness.SeedConnectionAsync(app, owner.User!.Id, Token);
        var project = await ProjectApi.CreateAsync(owner.Client, git: new { mode = "bitbucket", connectionId = connection, repoId = RepoUuid, repoFullName = FullName });
        var contributor = await ApiSession.RegisterAsync(app);
        var viewer = await ApiSession.RegisterAsync(app);
        var outsider = await ApiSession.RegisterAsync(app);
        await ProjectApi.AddMemberInDbAsync(app, project.Id, contributor.User!.Id, Aide.Domain.Access.ProjectRole.Contributor);
        await ProjectApi.AddMemberInDbAsync(app, project.Id, viewer.User!.Id, Aide.Domain.Access.ProjectRole.Viewer);
        bitbucket.Branch = ReleaseVersionRules.BranchName(project.Slug, project.Id, SemVer.Parse(project.DevelopmentVersion!));
        return (app, new ProjectFixture(project, owner, contributor, viewer, outsider));
    }

    /// <summary>A Bitbucket project under <c>manual</c>, released up to awaiting its merge.</summary>
    private async Task<(WebApplicationFactory<Program> App, ProjectFixture Fixture)> SetUpManualAsync(BitbucketState bitbucket, BareRemote remote)
    {
        var (app, fixture) = await SetUpAsync(bitbucket, remote);
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, fixture.Project.Id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, fixture.Project.Id);
        Assert.Equal(ProjectStatus.AwaitingMerge, (await LifecycleSeed.ProjectAsync(app, fixture.Project.Id)).Status);
        return (app, fixture);
    }

    private static Task<Job> ReleaseJobAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.ProjectId == id && j.Type == JobTypes.Release, Ct));

    private static Task<Job> PollJobAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.ProjectId == id && j.Type == JobTypes.MergePoll, Ct));

    private static async Task<ReleaseDto> LatestAsync(HttpClient client, Guid id) =>
        await ProjectApi.ReadAsync<ReleaseDto>(await client.GetAsync(new Uri($"/api/projects/{id}/releases/latest", UriKind.Relative), Ct));

    /// <summary>
    /// The stub Bitbucket repository <c>acme/widgets</c> ({r-acme-widgets}): write permission, pull request #3 (draft until
    /// marked ready), its mergeability checks, commit statuses, the merge (a merge commit in the bare repository; 200, or 202
    /// with a task, or 555 first), and tags (an annotated tag in the bare repository).
    /// </summary>
    private sealed class BitbucketState
    {
        private readonly BareRemote _remote;

        public BitbucketState(BareRemote remote)
        {
            _remote = remote;
            var repoJson = BitbucketHarness.RepoJson("acme", "widgets");
            Api = new FakeGitHubApi()
                .On(HttpMethod.Get, "/2.0/repositories/acme/" + Uri.EscapeDataString(RepoUuid), HttpStatusCode.OK, repoJson)
                .On(HttpMethod.Get, Repo, _ => Hidden ? (HttpStatusCode.NotFound, """{"type":"error","error":{"message":"Repository not found"}}""") : (HttpStatusCode.OK, repoJson))
                .On(HttpMethod.Get, "/2.0/user/permissions/repositories", HttpStatusCode.OK, $$$"""{"values":[{"permission":"write","repository":{"uuid":"{{{RepoUuid}}}"}}]}""")
                .On(HttpMethod.Get, Repo + "/refs/branches/main", HttpStatusCode.OK, """{"name":"main","target":{"hash":"1111111111111111111111111111111111111111"}}""")
                .On(HttpMethod.Get, Repo + "/pullrequests", _ => (HttpStatusCode.OK, Opened ? $$"""{"values":[{{PullRequestJson()}}]}""" : """{"values":[]}"""))
                .On(HttpMethod.Post, Repo + "/pullrequests", _ =>
                {
                    Opened = true;
                    return (HttpStatusCode.Created, PullRequestJson());
                })
                .OnWithHeaders(HttpMethod.Get, PullRequest, _ => RateLimited
                    ? (HttpStatusCode.TooManyRequests, """{"type":"error","error":{"message":"Rate limit"}}""", (IReadOnlyDictionary<string, string>?)new Dictionary<string, string> { ["Retry-After"] = "600" })
                    : Hidden ? (HttpStatusCode.NotFound, """{"type":"error","error":{"message":"Not found"}}""", null) : (HttpStatusCode.OK, PullRequestJson(), null))
                .On(HttpMethod.Put, PullRequest, r =>
                {
                    if (GitLabHarness.Body(r).TryGetProperty("draft", out var draft))
                    {
                        Draft = draft.GetBoolean();
                    }

                    return (HttpStatusCode.OK, PullRequestJson());
                })
                .On(HttpMethod.Get, PullRequest + "/mergeability/checks", _ => (HttpStatusCode.OK, Garbled ? """{"nothing":1}""" : JsonSerializer.Serialize(new { values = Checks })))
                .OnWithHeaders(HttpMethod.Post, PullRequest + "/merge", r => MergeResponse!(r))
                .On(HttpMethod.Get, TaskStatus, _ => PendingPolls-- > 0
                    ? (HttpStatusCode.OK, """{"task_status":"PENDING"}""")
                    : (HttpStatusCode.OK, $$"""{"task_status":"SUCCESS","merge_result":{{PullRequestJson()}}}"""))
                .On(HttpMethod.Get, Repo + "/refs/tags/" + Tag, _ => TagCommit is { } commit
                    ? (HttpStatusCode.OK, TagJson(commit))
                    : (HttpStatusCode.NotFound, """{"type":"error","error":{"message":"Tag not found"}}"""))
                .On(HttpMethod.Post, Repo + "/refs/tags", r =>
                {
                    var body = GitLabHarness.Body(r);
                    var target = body.GetProperty("target").GetProperty("hash").GetString()!;
                    ReleaseFlowTests.Run(_remote.BarePath, "tag", "-a", body.GetProperty("name").GetString()!, "-m", body.GetProperty("message").GetString()!, target);
                    TagCommit = target;
                    return (HttpStatusCode.Created, TagJson(target));
                });
            Api.Fallback = r => r.Method == "GET" && r.Path.StartsWith(Repo + "/commit/", StringComparison.Ordinal) && r.Path.EndsWith("/statuses", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, JsonSerializer.Serialize(new { values = Statuses }))
                : null;
            MergeResponse = _ =>
            {
                if (TimeoutsFirst > 0)
                {
                    TimeoutsFirst--;
                    return ((HttpStatusCode)555, """{"type":"error","error":{"message":"Timeout"}}""", null);
                }

                MergeSha = ReleaseFlowTests.MergeInBare(_remote, Branch!);
                State = "MERGED";
                return Asynchronous
                    ? (HttpStatusCode.Accepted, "{}", new Dictionary<string, string> { ["Location"] = "https://api.bitbucket.org" + TaskStatus })
                    : (HttpStatusCode.OK, PullRequestJson(), null);
            };
        }

        public FakeGitHubApi Api { get; }

        public static string PullRequestUrl => "https://bitbucket.org/acme/widgets/pull-requests/3";

        public static string TagUrl => "https://bitbucket.org/acme/widgets/commits/tag/" + Tag;

        public string? Branch { get; set; }

        public bool Opened { get; set; }

        public bool Draft { get; private set; } = true;

        public bool Garbled { get; set; }

        public bool Hidden { get; set; }

        public bool RateLimited { get; set; }

        public bool Asynchronous { get; init; }

        public int TimeoutsFirst { get; set; }

        /// <summary>How many task-status reads answer PENDING before SUCCESS.</summary>
        public int PendingPolls { get; set; }

        public string State { get; set; } = "OPEN";

        public IReadOnlyList<object> Checks { get; set; } = [];

        public IReadOnlyList<object> Statuses { get; set; } = [];

        public string? MergeSha { get; private set; }

        public string? TagCommit { get; private set; }

        public Func<GitHubRequest, (HttpStatusCode, string, IReadOnlyDictionary<string, string>?)> MergeResponse { get; set; }

        public void MergedExternally(string sha)
        {
            State = "MERGED";
            MergeSha = sha;
        }

        private string? Head() =>
            Branch is null
                ? null
                : ReleaseFlowTests.Run(_remote.BarePath, "for-each-ref", "--format=%(objectname)", $"refs/heads/{Branch}") is { Length: > 0 } sha ? sha : null;

        private string PullRequestJson() => JsonSerializer.Serialize(new
        {
            id = 3,
            title = "Release",
            state = State,
            draft = Draft,
            links = new { html = new { href = PullRequestUrl } },
            source = new { branch = new { name = Branch }, commit = Head() is { } head ? new { hash = head[..12] } : null },
            destination = new { branch = new { name = "main" } },
            merge_commit = MergeSha is { } sha ? new { hash = sha } : null,
        });

        private static string TagJson(string commit) =>
            JsonSerializer.Serialize(new { name = Tag, target = new { hash = commit }, links = new { html = new { href = TagUrl } } });
    }
}
