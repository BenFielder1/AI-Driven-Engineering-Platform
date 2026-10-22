using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Git;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Release;
using Aide.Api.Features.Review;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Releases;
using Aide.Domain.Review;
using Aide.Infrastructure.Agents;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Releases;

/// <summary>
/// M13b end to end with the Fake LLM: merge strategies and awaiting merge (FR-65), the merge poll (§4.14: ETag, rate
/// limits, PR_CLOSED, MERGE_TIMEOUT), the GitHub Release (FR-64), merge readiness (FR-84) and check runs as a review input
/// (FR-85). GitHub is <see cref="FakeGitHubApi"/> over a local bare repository; nothing reaches github.com.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class MergeFlowTests(AideApiFactory factory)
{
    private const string Token = "ghp_m13bSecretToken0123456789abcdef0000";
    private const string Repo = "/repos/acme/widgets";
    private const string Pulls = Repo + "/pulls";
    private const string Pull = Pulls + "/42";
    private const string PrUrl = "https://github.com/acme/widgets/pull/42";
    private const string Tag = "v0.1.0";
    private const string ReleasePage = "https://github.com/acme/widgets/releases/tag/v0.1.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- FR-65 + §4.14 + FR-64 + FR-84: branch protection → awaiting merge → merged on GitHub → released ----------

    [Fact]
    public async Task FR65_FR64_S4_14_Branch_protection_requiring_a_review_awaits_merge_then_the_poll_tags_the_merge_commit_and_creates_the_GitHub_Release()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var github = new GitHubState(remote) { ReviewDecision = "REVIEW_REQUIRED", MergeStateStatus = "BLOCKED", RequiredApprovals = 1 };
        github.MergeResponse = _ => (HttpStatusCode.MethodNotAllowed, """{"message":"At least 1 approving review is required by reviewers with write access."}""");
        await using var app = remote.App(ReleaseEligible(), github.Api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        github.Branch = BranchOf(fixture);
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        // FR-84: readiness was checked when the pre-release gate was created, and reads review_required.
        var stored = await ProjectApi.ReadAsync<MergeReadinessDto>(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/git/merge-readiness"));
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MergeReadinessChecked));
        Assert.Equal((false, false, "BLOCKED", "REVIEW_REQUIRED"), (stored.CanMerge, stored.WillAutoMerge, stored.MergeStateStatus, stored.ReviewDecision));
        Assert.Equal([MergeReadinessReasonCode.ReviewRequired], stored.Reasons.Select(r => r.Code));
        Assert.Equal("1 approving review is required.", stored.Reasons[0].Message);

        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        var since = DateTimeOffset.UtcNow;
        await AgentJobs.RunQueuedAsync(app, id);

        // The merge was refused: awaiting merge (attempt, version, project), with the readiness reasons, nothing tagged.
        Assert.Equal(1, github.Api.Count(HttpMethod.Put, Pull + "/merge"));
        var awaiting = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.AwaitingMerge, PrUrl, (DateTimeOffset?)null, MergeStrategy.AideMerges), (awaiting.Status, awaiting.PullRequestUrl, awaiting.CompletedAt, awaiting.MergeStrategy));
        Assert.Equal([MergeReadinessReasonCode.ReviewRequired], awaiting.MergeReasons!.Select(r => r.Code));
        Assert.NotNull(awaiting.AwaitingMergeSince);
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Release, ProjectStatus.AwaitingMerge, 6, "0.1.0"), (project.CurrentPhase, project.Status, project.Checkpoint, project.DevelopmentVersion));
        Assert.Equal(ReleaseVersionStatus.AwaitingMerge, (await VersionAsync(app, id)).Status);
        Assert.Equal(string.Empty, await TestGit.RunAsync(remote.BarePath, Ct, "tag", "-l", Tag));
        var state = await ProjectApi.ReadAsync<ProjectStateDto>(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/state"));
        Assert.Equal((ReleaseStatus.AwaitingMerge, PrUrl), (state.LatestRelease!.Status, state.LatestRelease.PullRequestUrl));

        // RELEASE_AWAITING_MERGE (the frontend contract) and the Owners' release_awaiting_merge notification (FR-92).
        var awaitingEvent = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseAwaitingMerge)).Payload);
        Assert.Equal(
            (awaiting.Id, awaiting.ReleaseVersionId, "0.1.0", Tag, PrUrl, "review_required"),
            (awaitingEvent.GetProperty("releaseId").GetGuid(), awaitingEvent.GetProperty("releaseVersionId").GetGuid(), awaitingEvent.GetProperty("version").GetString(),
                awaitingEvent.GetProperty("tag").GetString(), awaitingEvent.GetProperty("pullRequestUrl").GetString(), awaitingEvent.GetProperty("reasons")[0].GetProperty("code").GetString()));
        Assert.Contains(await LifecycleSeed.NotificationJobsAsync(app, id), j => j.Payload.Contains("release_awaiting_merge", StringComparison.Ordinal));

        // §4.10: no rollback while awaiting merge; no retry either (the poll completes it).
        var blocked = await Problems.AssertAsync(
            await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/rollback", new { targetCheckpoint = 1, reason = "Back" }, Ct), HttpStatusCode.Conflict, ErrorCodes.RollbackBlocked);
        Assert.Equal(["reason: release_partially_applied"], blocked.GetProperty("details").EnumerateArray().Select(d => d.GetString()));
        await Problems.AssertAsync(await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/release", new { }, Ct), HttpStatusCode.Conflict, ErrorCodes.NotReleasing);

        // The merge poll is one deferred job: its first poll (2 minutes later) saw the PR open and kept its ETag.
        var poll = await PollJobAsync(app, id);
        Assert.Equal((JobStatus.Queued, 0), (poll.Status, poll.Attempts));
        Assert.InRange(poll.RunAfter, since.AddMinutes(2).AddSeconds(-30), DateTimeOffset.UtcNow.AddMinutes(2).AddSeconds(5));
        Assert.Equal(github.ETag, JsonDocument.Parse(poll.Payload).RootElement.GetProperty("eTag").GetString());

        // §4.14: the next poll is conditional; a 304 is no change.
        await AgentJobs.RunCoreAsync(app, poll);
        var conditional = github.Api.Requests.Last(r => r.Method == "GET" && r.Path == Pull);
        Assert.Equal(github.ETag, conditional.IfNoneMatch);
        Assert.Equal(1, github.NotModifiedCount);
        Assert.Equal(ReleaseStatus.AwaitingMerge, (await LatestAsync(fixture.Viewer.Client, id)).Status);

        // §4.14, NFR-REL2: a rate limit defers the poll to the reset without counting an attempt.
        var reset = DateTimeOffset.UtcNow.AddMinutes(7);
        github.RateLimitedUntil = reset;
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));
        poll = await PollJobAsync(app, id);
        Assert.Equal((JobStatus.Queued, 0), (poll.Status, poll.Attempts));
        Assert.InRange(poll.RunAfter, reset.AddSeconds(-2), reset.AddSeconds(2));
        Assert.Contains(ErrorCodes.GitHubRateLimited, poll.LastError, StringComparison.Ordinal);
        github.RateLimitedUntil = null;

        // A reviewer approves and merges on GitHub: the next poll tags merge_commit_sha, creates the GitHub Release, releases.
        var mergeSha = ReleaseFlowTests.MergeInBare(remote, github.Branch!);
        github.MergedAs(mergeSha);
        await AgentJobs.RunCoreAsync(app, poll);

        Assert.Equal("tag", await TestGit.RunAsync(remote.BarePath, Ct, "cat-file", "-t", $"refs/tags/{Tag}"));
        Assert.Equal(mergeSha, await TestGit.RunAsync(remote.BarePath, Ct, "rev-parse", $"refs/tags/{Tag}^{{commit}}"));
        var released = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, mergeSha, ReleasePage), (released.Status, released.MergeCommitSha, released.GithubReleaseUrl));
        project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((ProjectStatus.Released, "0.1.0", (string?)null, 6), (project.Status, project.ReleasedVersion, project.DevelopmentVersion, project.Checkpoint));
        var version = await VersionAsync(app, id);
        Assert.Equal((ReleaseVersionStatus.Released, Tag, mergeSha), (version.Status, version.Tag, version.MergeCommitSha));
        Assert.Equal(1, github.Api.Count(HttpMethod.Put, Pull + "/merge")); // never merged by AIDE after the refusal

        var merged = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseMergedExternally)).Payload);
        Assert.Equal(
            (released.Id, "0.1.0", Tag, mergeSha, ReleasePage),
            (merged.GetProperty("releaseId").GetGuid(), merged.GetProperty("version").GetString(), merged.GetProperty("tag").GetString(),
                merged.GetProperty("mergeCommitSha").GetString(), merged.GetProperty("githubReleaseUrl").GetString()));
        Assert.True(LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseCompleted)).Payload).GetProperty("mergedExternally").GetBoolean());

        // FR-64, §4.14: AIDE's own notes — version, plan steps with commit links, review score, link back to AIDE.
        var create = Assert.Single(github.Api.Requests, r => r.Method == "POST" && r.Path == Repo + "/releases");
        Assert.Equal($"Bearer {Token}", create.Authorization);
        using (var body = JsonDocument.Parse(create.Body))
        {
            var root = body.RootElement;
            Assert.Equal((Tag, "0.1.0", false, false), (root.GetProperty("tag_name").GetString(), root.GetProperty("name").GetString(), root.GetProperty("draft").GetBoolean(), root.GetProperty("generate_release_notes").GetBoolean()));
            var notes = root.GetProperty("body").GetString()!;
            Assert.StartsWith($"## {fixture.Project.Name} 0.1.0", notes, StringComparison.Ordinal);
            Assert.Contains("### Changes in this version", notes, StringComparison.Ordinal);
            Assert.Matches(@"1\. Step 1 \(\[`[0-9a-f]{7}`\]\(https://github\.com/acme/widgets/commit/[0-9a-f]{40}\)\)", notes);
            Assert.Contains("2. Step 2", notes, StringComparison.Ordinal);
            Assert.Contains("Compliance score **0.930** (review cycle 1).", notes, StringComparison.Ordinal);
            Assert.Contains($"[AIDE](http://localhost:8080/projects/{id}/release)", notes, StringComparison.Ordinal);
        }

        // The poll ended; no token anywhere it could leak.
        Assert.Equal(JobStatus.Succeeded, (await PollJobAsync(app, id)).Status);
        var storedText = await ProjectApi.WithDbAsync(app, async db =>
            string.Join('\n', await db.Events.AsNoTracking().Where(e => e.ProjectId == id).Select(e => e.Payload).ToListAsync(Ct))
            + string.Join('\n', await db.Jobs.AsNoTracking().Where(j => j.ProjectId == id).Select(j => j.Payload + (j.LastError ?? string.Empty)).ToListAsync(Ct)));
        Assert.DoesNotContain(Token, storedText, StringComparison.Ordinal);
    }

    // ---------- FR-65 manual strategy: ready for review, never merged; GitHub Release reused; PR_CLOSED ----------

    [Fact]
    public async Task FR65_Manual_strategy_marks_the_draft_PR_ready_never_merges_and_reuses_an_existing_GitHub_Release()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var github = new GitHubState(remote) { Draft = true, ExistingRelease = true };
        await using var app = remote.App(ReleaseEligible(), github.Api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        github.Branch = BranchOf(fixture);
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        Assert.Equal(0, github.Api.Count(HttpMethod.Put, Pull + "/merge"));
        var ready = Assert.Single(github.Api.Requests, r => r.Path == "/graphql" && r.Body.Contains("markPullRequestReadyForReview", StringComparison.Ordinal));
        Assert.Contains("PR_node42", ready.Body, StringComparison.Ordinal);
        Assert.False(github.Draft);
        var awaiting = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.AwaitingMerge, MergeStrategy.Manual), (awaiting.Status, awaiting.MergeStrategy));
        Assert.Equal(MergeReadinessReasonCode.ManualStrategy, awaiting.MergeReasons![0].Code);
        Assert.Equal(ProjectStatus.AwaitingMerge, (await LifecycleSeed.ProjectAsync(app, id)).Status);

        // Someone merges on GitHub: merge_commit_sha is the tag target; the existing GitHub Release is reused.
        var mergeSha = ReleaseFlowTests.MergeInBare(remote, github.Branch!);
        github.MergedAs(mergeSha);
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var released = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, mergeSha, ReleasePage), (released.Status, released.MergeCommitSha, released.GithubReleaseUrl));
        Assert.Equal(mergeSha, await TestGit.RunAsync(remote.BarePath, Ct, "rev-parse", $"refs/tags/{Tag}^{{commit}}"));
        Assert.Equal(0, github.Api.Count(HttpMethod.Post, Repo + "/releases"));
        Assert.Equal(0, github.Api.Count(HttpMethod.Put, Pull + "/merge"));
    }

    [Fact]
    public async Task FR65_A_PR_closed_without_merging_fails_with_PR_CLOSED_the_project_stays_in_release_and_can_retry()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var github = new GitHubState(remote);
        await using var app = remote.App(ReleaseEligible(), github.Api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        github.Branch = BranchOf(fixture);
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        Assert.Equal(ProjectStatus.AwaitingMerge, (await LifecycleSeed.ProjectAsync(app, id)).Status);

        github.State = "closed";
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var failed = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.PrClosed), (failed.Status, failed.ErrorCode));
        Assert.NotNull(failed.CompletedAt);
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Release, ProjectStatus.Releasing, 6, "0.1.0"), (project.CurrentPhase, project.Status, project.Checkpoint, project.DevelopmentVersion));
        Assert.Equal(ReleaseVersionStatus.InDevelopment, (await VersionAsync(app, id)).Status);
        var failedEvent = LifecycleSeed.Payload((await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseFailed)).Last().Payload);
        Assert.Equal(ErrorCodes.PrClosed, failedEvent.GetProperty("errorCode").GetString());
        Assert.Equal(JobStatus.Succeeded, (await PollJobAsync(app, id)).Status);

        // Reopened on GitHub: an Owner retries with POST /release.
        github.State = "open";
        var retried = await ProjectApi.ReadAsync<ReleaseDto>(await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/release", new { }, Ct), HttpStatusCode.Accepted);
        Assert.Equal((ReleaseStatus.InProgress, 2), (retried.Status, retried.Attempt));
    }

    // ---------- Local: manual merge detection and the 14-day timeout ----------

    [Fact]
    public async Task FR65_Local_manual_awaits_until_someone_merges_the_branch_and_tags_the_first_parent_merge_commit()
    {
        var app = ReleaseEligible();
        using var fixture = await ReleaseFlowTests.ReleasableAsync(app);
        var id = fixture.Project.Id;
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var awaiting = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal(ReleaseStatus.AwaitingMerge, awaiting.Status);
        Assert.Equal([MergeReadinessReasonCode.ManualStrategy], awaiting.MergeReasons!.Select(r => r.Code));
        var readiness = await ProjectApi.ReadAsync<MergeReadinessDto>(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/git/merge-readiness?refresh=true"));
        Assert.Equal((true, false), (readiness.CanMerge, readiness.WillAutoMerge));

        // Still unmerged: the poll waits.
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));
        Assert.Equal(ReleaseStatus.AwaitingMerge, (await LatestAsync(fixture.Viewer.Client, id)).Status);

        // Someone merges the version branch into main by hand, then main moves on: the tag goes on the merge commit.
        var branch = BranchOf(fixture);
        var workingCopy = Path.Combine(factory.WorkspacesRoot, id.ToString("D"));
        await TestGit.RunAsync(workingCopy, Ct, "checkout", "-q", "main");
        await TestGit.RunAsync(workingCopy, Ct, "merge", "-q", "--no-ff", "-m", "Merge by hand", branch);
        var mergeCommit = await TestGit.RunAsync(workingCopy, Ct, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(workingCopy, "AFTER.md"), "later\n", Ct);
        await TestGit.RunAsync(workingCopy, Ct, "add", "AFTER.md");
        await TestGit.RunAsync(workingCopy, Ct, "commit", "-q", "-m", "Later work on main");
        await TestGit.RunAsync(workingCopy, Ct, "checkout", "-q", branch);

        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var released = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, mergeCommit, (string?)null), (released.Status, released.MergeCommitSha, released.GithubReleaseUrl));
        Assert.Equal(mergeCommit, await TestGit.RunAsync(workingCopy, Ct, "rev-parse", $"{Tag}^{{commit}}"));
        Assert.Equal(ProjectStatus.Released, (await LifecycleSeed.ProjectAsync(app, id)).Status);
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseMergedExternally));
    }

    [Fact]
    public async Task S4_14_FR65_A_merge_still_missing_after_14_days_fails_with_MERGE_TIMEOUT_and_can_be_retried()
    {
        var app = ReleaseEligible();
        using var fixture = await ReleaseFlowTests.ReleasableAsync(app);
        var id = fixture.Project.Id;
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ProjectApi.WithDbAsync(app, db => db.Releases.Where(r => r.ProjectId == id && r.Status == ReleaseStatus.AwaitingMerge)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.AwaitingMergeSince, DateTimeOffset.UtcNow.AddDays(-14).AddMinutes(-1)), Ct));

        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var failed = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.MergeTimeout), (failed.Status, failed.ErrorCode));
        Assert.Equal((Phase.Release, ProjectStatus.Releasing), ((await LifecycleSeed.ProjectAsync(app, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(app, id)).Status));
        Assert.Equal(ErrorCodes.MergeTimeout, LifecycleSeed.Payload((await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseFailed)).Last().Payload).GetProperty("errorCode").GetString());
        await ProjectApi.ReadAsync<ReleaseDto>(await fixture.Owner.Client.PostAsJsonAsync($"{fixture.Path}/release", new { }, Ct), HttpStatusCode.Accepted);
    }

    // ---------- FR-85: check runs as a review input ----------

    [Fact]
    public async Task FR85_Review_waits_for_pending_checks_then_times_out_with_a_warning_and_a_failing_required_check_costs_testing_0_10()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var github = new GitHubState(remote) { RequiredChecks = ["ci / test"] };
        github.CheckRuns =
        [
            new { name = "ci / test", status = "completed", conclusion = "failure", details_url = "https://github.com/acme/widgets/actions/runs/1" },
            new { name = "e2e", status = "in_progress", conclusion = (string?)null, details_url = (string?)null },
        ];
        await using var app = remote.App(ReleaseEligible(), github.Api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        github.Branch = BranchOf(fixture);
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        // A pending check: the review job deferred itself 30 s ahead (not a counted attempt) and recorded nothing yet.
        var review = Assert.Single(await LifecycleSeed.JobsAsync(app, id), j => j.Type == JobTypes.Review);
        Assert.Equal((JobStatus.Queued, 0), (review.Status, review.Attempts));
        Assert.InRange(review.RunAfter, DateTimeOffset.UtcNow.AddSeconds(5), DateTimeOffset.UtcNow.AddSeconds(31));
        var waitingSince = JsonDocument.Parse(review.Payload).RootElement.GetProperty("checksWaitSince").GetDateTimeOffset();
        Assert.Empty(await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().Where(r => r.ProjectId == id).ToListAsync(Ct)));

        // Still pending 15 minutes later: review proceeds with checksTimedOut.
        var node = System.Text.Json.Nodes.JsonNode.Parse(review.Payload)!;
        node["checksWaitSince"] = waitingSince.AddMinutes(-16).ToString("O", CultureInfo.InvariantCulture);
        var payload = node.ToJsonString();
        await ProjectApi.WithDbAsync(app, db => db.Jobs.Where(j => j.Id == review.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Payload, payload), Ct));
        await AgentJobs.RunQueuedAsync(app, id);

        var report = await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        var finding = Assert.Single(report.Violations, v => v.Message == "required check ci / test failed");
        Assert.Equal((ReviewCategory.Testing, Severity.Major, FindingSource.Deterministic, "ci / test: https://github.com/acme/widgets/actions/runs/1"), (finding.Category, finding.Severity, finding.Source, finding.Evidence));
        Assert.Equal(0.83m, report.CategoryScores.Single(c => c.Category == ReviewCategory.Testing).Score);
        Assert.Equal(0.93m, report.CategoryScores.Single(c => c.Category == ReviewCategory.Security).Score);

        var detail = await ProjectApi.ReadAsync<ComplianceReportDetailDto>(await GetAsync(fixture.Viewer.Client, $"{fixture.Path}/compliance/reports/1"));
        Assert.True(detail.ExternalChecks!.ChecksTimedOut);
        Assert.InRange(detail.ExternalChecks.ChecksWaitedSeconds!.Value, 900, 1200);
        Assert.Equal(
            [new ExternalCheckDto("ci / test", CheckRunConclusion.Failure, true, "https://github.com/acme/widgets/actions/runs/1"), new ExternalCheckDto("e2e", null, false, null)],
            detail.ExternalChecks.Checks);
        Assert.Null(detail.ExternalChecks.Preview);
        Assert.True(LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReviewCompleted)).Payload).GetProperty("checksTimedOut").GetBoolean());
    }

    [Fact]
    public async Task FR85_Local_review_does_not_wait_and_records_no_external_checks()
    {
        var app = ReleaseEligible();
        using var fixture = await ReleaseFlowTests.ReleasableAsync(app);

        var report = await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().SingleAsync(r => r.ProjectId == fixture.Project.Id, Ct));
        Assert.Null(report.ExternalChecks);
        Assert.DoesNotContain(report.Violations, v => v.Message.StartsWith("required check", StringComparison.Ordinal));
    }

    // ---------- review fixes: rate-limited merge, human-merged PR under aide_merges, tag reuse only at the merge commit ----------

    [Fact]
    public async Task S4_14_FR65_A_rate_limited_merge_defers_the_release_job_instead_of_awaiting_merge_and_merges_after_the_reset()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var github = new GitHubState(remote);
        var merge = github.MergeResponse;
        github.MergeResponse = _ => (HttpStatusCode.Forbidden, """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}""");
        await using var app = remote.App(ReleaseEligible(), github.Api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        github.Branch = BranchOf(fixture);
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        // Not a refusal: the job waits for the reset (attempt not counted); the release stays in progress.
        var job = Assert.Single(await LifecycleSeed.JobsAsync(app, id), j => j.Type == JobTypes.Release);
        Assert.Equal((JobStatus.Queued, 0), (job.Status, job.Attempts));
        Assert.Contains(ErrorCodes.GitHubRateLimited, job.LastError, StringComparison.Ordinal);
        Assert.Equal(ReleaseStatus.InProgress, (await LatestAsync(fixture.Viewer.Client, id)).Status);
        Assert.Equal(ProjectStatus.Releasing, (await LifecycleSeed.ProjectAsync(app, id)).Status);
        Assert.Empty(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseAwaitingMerge));

        github.MergeResponse = merge;
        await AgentJobs.RunCoreAsync(app, job);

        var released = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal(ReleaseStatus.Succeeded, released.Status);
        Assert.Equal(2, github.Api.Count(HttpMethod.Put, Pull + "/merge"));
        Assert.Empty(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseMergedExternally));
    }

    [Fact]
    public async Task FR65_Under_aide_merges_a_PR_a_human_already_merged_is_tagged_and_recorded_as_merged_externally()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var github = new GitHubState(remote);
        await using var app = remote.App(ReleaseEligible(), github.Api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        github.Branch = BranchOf(fixture);
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        var mergeSha = ReleaseFlowTests.MergeInBare(remote, github.Branch);
        github.MergedAs(mergeSha);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var released = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, mergeSha), (released.Status, released.MergeCommitSha));
        Assert.Equal(0, github.Api.Count(HttpMethod.Put, Pull + "/merge"));
        Assert.Equal(mergeSha, await TestGit.RunAsync(remote.BarePath, Ct, "rev-parse", $"refs/tags/{Tag}^{{commit}}"));
        var merged = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseMergedExternally)).Payload);
        Assert.Equal(mergeSha, merged.GetProperty("mergeCommitSha").GetString());
        Assert.True(LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseCompleted)).Payload).GetProperty("mergedExternally").GetBoolean());
    }

    [Fact]
    public async Task FR64_A_tag_on_a_later_commit_that_contains_the_merge_is_TAG_EXISTS_and_nothing_is_recorded_on_it()
    {
        var app = ReleaseEligible();
        using var fixture = await ReleaseFlowTests.ReleasableAsync(app);
        var id = fixture.Project.Id;
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        // A human merges (M), commits N on main and tags N with the release tag.
        var branch = BranchOf(fixture);
        var workingCopy = Path.Combine(factory.WorkspacesRoot, id.ToString("D"));
        await TestGit.RunAsync(workingCopy, Ct, "checkout", "-q", "main");
        await TestGit.RunAsync(workingCopy, Ct, "merge", "-q", "--no-ff", "-m", "Merge by hand", branch);
        await File.WriteAllTextAsync(Path.Combine(workingCopy, "AFTER.md"), "later\n", Ct);
        await TestGit.RunAsync(workingCopy, Ct, "add", "AFTER.md");
        await TestGit.RunAsync(workingCopy, Ct, "commit", "-q", "-m", "Later work on main");
        var later = await TestGit.RunAsync(workingCopy, Ct, "rev-parse", "HEAD");
        await TestGit.RunAsync(workingCopy, Ct, "tag", "-a", "-m", "by hand", Tag, later);
        await TestGit.RunAsync(workingCopy, Ct, "checkout", "-q", branch);

        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var failed = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.TagExists, (string?)null), (failed.Status, failed.ErrorCode, failed.MergeCommitSha));
        var version = await VersionAsync(app, id);
        Assert.Equal((ReleaseVersionStatus.InDevelopment, (string?)null, (string?)null), (version.Status, version.MergeCommitSha, version.Tag));
        Assert.Equal(ProjectStatus.Releasing, (await LifecycleSeed.ProjectAsync(app, id)).Status);
        Assert.Equal(later, await TestGit.RunAsync(workingCopy, Ct, "rev-parse", $"{Tag}^{{commit}}"));
    }

    // ---------- helpers ----------

    private WebApplicationFactory<Program> ReleaseEligible() =>
        AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] }));

    private static Task<ProjectFixture> GitHubFixtureAsync(WebApplicationFactory<Program> app) =>
        ProjectApi.FixtureAsync(app, git: new { provider = "github_pat", repoUrl = BareRemote.RepoUrl, defaultBranch = "main", token = Token });

    private static string BranchOf(ProjectFixture fixture) =>
        ReleaseVersionRules.BranchName(fixture.Project.Slug, fixture.Project.Id, SemVer.Parse(fixture.Project.DevelopmentVersion!));

    private static Task<Job> PollJobAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.ProjectId == id && j.Type == JobTypes.MergePoll, Ct));

    private static Task<ReleaseVersion> VersionAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == id, Ct));

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path) => client.GetAsync(new Uri(path, UriKind.Relative), Ct);

    private static async Task<ReleaseDto> LatestAsync(HttpClient client, Guid id) =>
        await ProjectApi.ReadAsync<ReleaseDto>(await client.GetAsync(new Uri($"/api/projects/{id}/releases/latest", UriKind.Relative), Ct));

    /// <summary>The fake GitHub repository acme/widgets: PR #42's state (with ETags), rules, GraphQL, checks and releases.</summary>
    private sealed class GitHubState
    {
        private readonly BareRemote _remote;

        public GitHubState(BareRemote remote)
        {
            _remote = remote;
            Api = new FakeGitHubApi()
                .On(HttpMethod.Get, Pulls, HttpStatusCode.OK, "[]")
                .On(HttpMethod.Post, Pulls, HttpStatusCode.Created, $$"""{"number":42,"html_url":"{{PrUrl}}"}""")
                .On(HttpMethod.Get, Repo, _ => (HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    id = 1,
                    full_name = "acme/widgets",
                    name = "widgets",
                    owner = new { login = "acme", type = "Organization" },
                    default_branch = "main",
                    permissions = new { push = true },
                    allow_merge_commit = true,
                    allow_squash_merge = true,
                    allow_rebase_merge = false,
                })))
                .OnWithHeaders(HttpMethod.Get, Pull, PullResponse)
                .On(HttpMethod.Put, Pull + "/merge", r => MergeResponse!(r))
                .On(HttpMethod.Get, Repo + "/rules/branches/main", _ => (HttpStatusCode.OK, RulesJson()))
                .On(HttpMethod.Post, "/graphql", GraphQl)
                .On(HttpMethod.Get, Repo + "/releases/tags/" + Tag, _ => ExistingRelease || Created
                    ? (HttpStatusCode.OK, $$"""{"tag_name":"{{Tag}}","html_url":"{{ReleasePage}}"}""")
                    : (HttpStatusCode.NotFound, """{"message":"Not Found"}"""))
                .On(HttpMethod.Post, Repo + "/releases", _ =>
                {
                    Created = true;
                    return (HttpStatusCode.Created, $$"""{"tag_name":"{{Tag}}","html_url":"{{ReleasePage}}"}""");
                });
            Api.Fallback = r => r.Method == "GET" && r.Path.StartsWith(Repo + "/commits/", StringComparison.Ordinal)
                ? r.Path.EndsWith("/check-runs", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, JsonSerializer.Serialize(new { total_count = CheckRuns.Count, check_runs = CheckRuns }))
                    : r.Path.EndsWith("/status", StringComparison.Ordinal) ? (HttpStatusCode.OK, """{"state":"pending","statuses":[]}""") : null
                : null;
            MergeResponse = _ => (HttpStatusCode.OK, JsonSerializer.Serialize(new { sha = ReleaseFlowTests.MergeInBare(_remote, Branch!), merged = true }));
        }

        public FakeGitHubApi Api { get; }

        public string? Branch { get; set; }

        public string State { get; set; } = "open";

        public bool Merged { get; private set; }

        public string? MergeSha { get; private set; }

        public bool Draft { get; set; }

        public bool ExistingRelease { get; init; }

        public bool Created { get; private set; }

        public string? ReviewDecision { get; init; } = "APPROVED";

        public string? MergeStateStatus { get; init; } = "CLEAN";

        public int RequiredApprovals { get; init; }

        public IReadOnlyList<string> RequiredChecks { get; init; } = [];

        public IReadOnlyList<object> CheckRuns { get; set; } = [];

        public Func<GitHubRequest, (HttpStatusCode, string)> MergeResponse { get; set; }

        public DateTimeOffset? RateLimitedUntil { get; set; }

        public int NotModifiedCount { get; private set; }

        public string ETag => $"\"{State}-{Merged}-{Draft}\"";

        public void MergedAs(string sha)
        {
            Merged = true;
            MergeSha = sha;
            State = "closed";
        }

        private (HttpStatusCode, string, IReadOnlyDictionary<string, string>?) PullResponse(GitHubRequest request)
        {
            if (RateLimitedUntil is { } until)
            {
                return (HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""", new Dictionary<string, string>
                {
                    ["x-ratelimit-remaining"] = "0",
                    ["x-ratelimit-reset"] = until.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                });
            }

            if (request.IfNoneMatch == ETag)
            {
                NotModifiedCount++;
                return (HttpStatusCode.NotModified, string.Empty, new Dictionary<string, string> { ["ETag"] = ETag });
            }

            var head = Branch is null ? null : ReleaseFlowTests.BareSha(_remote, $"refs/heads/{Branch}");
            return (HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                number = 42,
                node_id = "PR_node42",
                state = State,
                merged = Merged,
                draft = Draft,
                mergeable = true,
                html_url = PrUrl,
                merge_commit_sha = MergeSha,
                head = new { sha = head },
            }), new Dictionary<string, string> { ["ETag"] = ETag });
        }

        private string RulesJson()
        {
            var rules = new List<object>();
            if (RequiredApprovals > 0)
            {
                rules.Add(new { type = "pull_request", parameters = new { required_approving_review_count = RequiredApprovals } });
            }

            if (RequiredChecks.Count > 0)
            {
                rules.Add(new { type = "required_status_checks", parameters = new { required_status_checks = RequiredChecks.Select(c => new { context = c }) } });
            }

            return JsonSerializer.Serialize(rules);
        }

        private (HttpStatusCode, string) GraphQl(GitHubRequest request)
        {
            if (request.Body.Contains("markPullRequestReadyForReview", StringComparison.Ordinal))
            {
                Draft = false;
                return (HttpStatusCode.OK, """{"data":{"markPullRequestReadyForReview":{"pullRequest":{"isDraft":false}}}}""");
            }

            return (HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                data = new { repository = new { pullRequest = new { mergeStateStatus = MergeStateStatus, reviewDecision = ReviewDecision, isDraft = Draft } } },
            }));
        }
    }
}
