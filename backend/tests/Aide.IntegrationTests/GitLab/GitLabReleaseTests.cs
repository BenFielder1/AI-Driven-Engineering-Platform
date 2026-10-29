using System.Net;
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

namespace Aide.IntegrationTests.GitLab;

/// <summary>
/// M27 slice 2b end to end with the Fake LLM: GitLab merge readiness (FR-138), CI statuses as a review input (FR-139), the
/// merge, the GitLab Release and the <c>manual</c> strategy with the merge poll (FR-140). GitLab is a stub API over a local
/// bare repository (<c>GitLab:GitBaseUrlOverride</c>, tests only); nothing reaches a real GitLab.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitLabReleaseTests(AideApiFactory factory)
{
    private const string RepoId = "77";
    private const string FullPath = "acme/widgets";
    private const string Token = "glpat-release-0123456789";
    private const string Project = "/api/v4/projects/" + RepoId;
    private const string MergeRequest = Project + "/merge_requests/3";
    private const string Tag = "v0.1.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- FR-138: readiness ----------

    [Fact]
    public async Task FR138_Given_a_GitLab_MR_with_detailed_merge_status_not_approved_when_readiness_is_checked_then_canMerge_is_false_with_review_required()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var gitlab = new GitLabState(remote) { DetailedStatus = "not_approved", Approved = false };
        var (app, fixture) = await SetUpAsync(gitlab, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Review, 5, ApprovalMode.Advisory);
        await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.Where(v => v.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.PullRequestNumber, 3).SetProperty(v => v.PullRequestUrl, gitlab.MergeRequestUrl), Ct));
        gitlab.Opened = true;

        var readiness = await ProjectApi.ReadAsync<MergeReadinessDto>(
            await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/git/merge-readiness?refresh=true", UriKind.Relative), Ct));

        Assert.Equal((false, false, "not_approved", (string?)null), (readiness.CanMerge, readiness.WillAutoMerge, readiness.MergeStateStatus, readiness.ReviewDecision));
        Assert.Equal([MergeReadinessReasonCode.ReviewRequired], readiness.Reasons.Select(r => r.Code));
        Assert.Contains(gitlab.Api.Requests, r => r.Path == MergeRequest + "/approvals" && r.Authorization == $"Bearer {Token}");
        Assert.Contains(gitlab.Api.Requests, r => r.Path == Project + "/protected_branches/main");
        Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.MergeReadinessChecked));
    }

    // ---------- FR-139: CI statuses as a review input ----------

    [Fact]
    public async Task FR139_Given_a_failing_test_backend_job_on_a_project_requiring_pipelines_to_succeed_then_a_major_finding_costs_testing_0_10()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var gitlab = new GitLabState(remote) { PipelinesMustSucceed = true };
        gitlab.Statuses =
        [
            new { name = "test:backend", status = "failed", allow_failure = false, target_url = gitlab.WebUrl + "/-/jobs/7" },
            new { name = "lint", status = "failed", allow_failure = true, target_url = gitlab.WebUrl + "/-/jobs/8" },
            new { name = "deploy:prod", status = "manual", allow_failure = false, target_url = gitlab.WebUrl + "/-/jobs/9" },
            new { name = "e2e:nightly", status = "skipped", allow_failure = false, target_url = gitlab.WebUrl + "/-/jobs/10" },
        ];
        var (app, fixture) = await SetUpAsync(gitlab, remote);
        using var session = fixture;
        var id = fixture.Project.Id;

        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var report = await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().SingleAsync(r => r.ProjectId == id, Ct));
        var required = Assert.Single(report.Violations, v => v.Message == "required pipeline job test:backend failed");
        Assert.Equal(
            (ReviewCategory.Testing, Severity.Major, FindingSource.Deterministic, $"test:backend: {gitlab.WebUrl}/-/jobs/7"),
            (required.Category, required.Severity, required.Source, required.Evidence));
        var optional = Assert.Single(report.Violations, v => v.Message == "pipeline job lint failed");
        Assert.Equal(Severity.Minor, optional.Severity);
        Assert.Equal(0.80m, report.CategoryScores.Single(c => c.Category == ReviewCategory.Testing).Score); // 0.93 − 0.10 (major) − 0.03 (minor)

        // An unrun manual job and a skipped one are neutral: no finding, and review didn't wait for them.
        Assert.DoesNotContain(report.Violations, v => v.Message.Contains("deploy:prod", StringComparison.Ordinal) || v.Message.Contains("e2e:nightly", StringComparison.Ordinal));
        Assert.Contains(gitlab.Api.Requests, r => r.Method == "GET" && r.Path.EndsWith("/statuses", StringComparison.Ordinal) && r.Authorization == $"Bearer {Token}");
    }

    [Fact]
    public async Task FR139_Review_waits_for_a_pending_GitLab_job_without_counting_an_attempt()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var gitlab = new GitLabState(remote) { Statuses = [new { name = "test:backend", status = "running", allow_failure = false, target_url = (string?)null }] };
        var (app, fixture) = await SetUpAsync(gitlab, remote);
        using var session = fixture;
        var id = fixture.Project.Id;

        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var review = Assert.Single(await LifecycleSeed.JobsAsync(app, id), j => j.Type == JobTypes.Review);
        Assert.Equal((JobStatus.Queued, 0), (review.Status, review.Attempts));
        Assert.True(JsonDocument.Parse(review.Payload).RootElement.TryGetProperty("checksWaitSince", out _));
        Assert.Empty(await ProjectApi.WithDbAsync(app, db => db.ComplianceReports.AsNoTracking().Where(r => r.ProjectId == id).ToListAsync(Ct)));
    }

    // ---------- FR-140: aide_merges ----------

    [Fact]
    public async Task FR140_Given_aide_merges_when_the_release_job_succeeds_then_the_MR_is_merged_a_GitLab_Release_v0_1_0_exists_on_the_merged_commit_and_it_reads_Released_0_1_0()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var gitlab = new GitLabState(remote);
        var (app, fixture) = await SetUpAsync(gitlab, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        // FR-138: readiness was checked when the pre-release gate was created.
        var stored = await ProjectApi.ReadAsync<MergeReadinessDto>(await fixture.Viewer.Client.GetAsync(new Uri($"{fixture.Path}/git/merge-readiness", UriKind.Relative), Ct));
        Assert.Equal((true, true, "mergeable"), (stored.CanMerge, stored.WillAutoMerge, stored.MergeStateStatus));

        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        // The draft MR was marked ready before the merge; the merge pinned the reviewed head and kept the source branch.
        var readyAt = gitlab.Api.Requests.ToList().FindIndex(r => r.Method == "PUT" && r.Path == MergeRequest && !GitLabHarness.Body(r).GetProperty("title").GetString()!.StartsWith("Draft:", StringComparison.Ordinal));
        var mergeAt = gitlab.Api.Requests.ToList().FindIndex(r => r.Method == "PUT" && r.Path == MergeRequest + "/merge");
        Assert.InRange(readyAt, 0, mergeAt - 1);
        var merge = GitLabHarness.Body(gitlab.Api.Requests[mergeAt]);
        Assert.Equal((gitlab.HeadAtMerge, false, false), (merge.GetProperty("sha").GetString(), merge.GetProperty("squash").GetBoolean(), merge.GetProperty("should_remove_source_branch").GetBoolean()));

        // The GitLab Release: tag v0.1.0 on the merged commit, annotated, AIDE's notes as the description.
        var create = Assert.Single(gitlab.Api.Requests, r => r.Method == "POST" && r.Path == Project + "/releases");
        Assert.Equal($"Bearer {Token}", create.Authorization);
        var release = GitLabHarness.Body(create);
        Assert.Equal((Tag, gitlab.MergeSha, "0.1.0"), (release.GetProperty("tag_name").GetString(), release.GetProperty("ref").GetString(), release.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(release.GetProperty("tag_message").GetString()));
        var notes = release.GetProperty("description").GetString()!;
        Assert.StartsWith($"## {fixture.Project.Name} 0.1.0", notes, StringComparison.Ordinal);
        Assert.Contains("1. Step 1", notes, StringComparison.Ordinal);
        Assert.Contains($"/projects/{id}/release", notes, StringComparison.Ordinal);
        Assert.Equal(gitlab.MergeSha, ReleaseFlowTests.BareSha(remote, $"refs/tags/{Tag}^{{commit}}"));

        var released = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, gitlab.MergeSha, gitlab.ReleaseUrl), (released.Status, released.MergeCommitSha, released.GithubReleaseUrl));
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((ProjectStatus.Released, "0.1.0", (string?)null), (project.Status, project.ReleasedVersion, project.DevelopmentVersion));
        var version = await VersionAsync(app, id);
        Assert.Equal((ReleaseVersionStatus.Released, Tag, gitlab.MergeSha), (version.Status, version.Tag, version.MergeCommitSha));
        Assert.Empty(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseMergedExternally));

        // FR-140: the release events name the Git provider for activity and notifications.
        var completed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseCompleted)).Payload);
        Assert.Equal(("gitlab", false), (completed.GetProperty("provider").GetString(), completed.GetProperty("mergedExternally").GetBoolean()));
        Assert.Equal("gitlab", LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseStarted)).Payload).GetProperty("provider").GetString());
        Assert.All(
            await LifecycleSeed.EventsAsync(app, id, EventTypes.MergeReadinessChecked),
            e => Assert.Equal("gitlab", LifecycleSeed.Payload(e.Payload).GetProperty("provider").GetString()));

        // The token stays out of events and job rows.
        var text = await ProjectApi.WithDbAsync(app, async db =>
            string.Join('\n', await db.Events.AsNoTracking().Where(e => e.ProjectId == id).Select(e => e.Payload).ToListAsync(Ct))
            + string.Join('\n', await db.Jobs.AsNoTracking().Where(j => j.ProjectId == id).Select(j => j.Payload + (j.LastError ?? string.Empty)).ToListAsync(Ct)));
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR140_A_409_head_moved_fails_the_attempt_for_a_new_review_and_nothing_is_tagged()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var gitlab = new GitLabState(remote);
        gitlab.MergeResponse = _ => (HttpStatusCode.Conflict, """{"message":"SHA does not match HEAD of source branch"}""");
        var (app, fixture) = await SetUpAsync(gitlab, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var failed = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.GitError), (failed.Status, failed.ErrorCode));
        Assert.Contains("needs a new review", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, gitlab.Api.Count(HttpMethod.Put, MergeRequest + "/merge"));
        Assert.Equal(0, gitlab.Api.Count(HttpMethod.Post, Project + "/releases"));
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Release, ProjectStatus.Releasing), (project.CurrentPhase, project.Status));
    }

    [Fact]
    public async Task FR140_A_405_not_mergeable_awaits_the_merge_with_the_readiness_reasons()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var gitlab = new GitLabState(remote);
        gitlab.MergeResponse = _ =>
        {
            gitlab.DetailedStatus = "not_approved";
            gitlab.Approved = false;
            return (HttpStatusCode.MethodNotAllowed, """{"message":"Method Not Allowed"}""");
        };
        var (app, fixture) = await SetUpAsync(gitlab, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var awaiting = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal(ReleaseStatus.AwaitingMerge, awaiting.Status);
        Assert.Contains(MergeReadinessReasonCode.ReviewRequired, awaiting.MergeReasons!.Select(r => r.Code));
        Assert.Equal(ProjectStatus.AwaitingMerge, (await LifecycleSeed.ProjectAsync(app, id)).Status);
        Assert.Equal(0, gitlab.Api.Count(HttpMethod.Post, Project + "/releases"));
    }

    // ---------- FR-140: manual strategy, the merge poll, PR_CLOSED ----------

    [Fact]
    public async Task FR140_Manual_strategy_marks_the_MR_ready_awaits_the_merge_then_releases_when_it_is_merged_on_GitLab()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var gitlab = new GitLabState(remote);
        var (app, fixture) = await SetUpAsync(gitlab, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        Assert.Equal(0, gitlab.Api.Count(HttpMethod.Put, MergeRequest + "/merge"));
        Assert.False(gitlab.Title.StartsWith("Draft:", StringComparison.Ordinal));
        var awaiting = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.AwaitingMerge, MergeStrategy.Manual, gitlab.MergeRequestUrl), (awaiting.Status, awaiting.MergeStrategy, awaiting.PullRequestUrl));
        Assert.Equal(MergeReadinessReasonCode.ManualStrategy, awaiting.MergeReasons![0].Code);
        Assert.Equal(ProjectStatus.AwaitingMerge, (await LifecycleSeed.ProjectAsync(app, id)).Status);

        // Still open: the poll waits on the §4.19 cadence (2 minutes in the first hour).
        var since = DateTimeOffset.UtcNow;
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));
        var poll = await PollJobAsync(app, id);
        Assert.Equal((JobStatus.Queued, 0), (poll.Status, poll.Attempts));
        Assert.InRange(poll.RunAfter, since.AddMinutes(2).AddSeconds(-5), DateTimeOffset.UtcNow.AddMinutes(2).AddSeconds(5));

        // Merged on GitLab: the poll releases the merged commit with the GitLab Release.
        gitlab.MergedExternally(ReleaseFlowTests.MergeInBare(remote, gitlab.Branch!));
        await AgentJobs.RunCoreAsync(app, poll);

        var released = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Succeeded, gitlab.MergeSha, gitlab.ReleaseUrl), (released.Status, released.MergeCommitSha, released.GithubReleaseUrl));
        Assert.Equal(gitlab.MergeSha, ReleaseFlowTests.BareSha(remote, $"refs/tags/{Tag}^{{commit}}"));
        Assert.Equal(ProjectStatus.Released, (await LifecycleSeed.ProjectAsync(app, id)).Status);
        var external = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseMergedExternally)).Payload);
        Assert.Equal("gitlab", external.GetProperty("provider").GetString());
        Assert.Equal("gitlab", LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseAwaitingMerge)).Payload).GetProperty("provider").GetString());
        Assert.Equal(0, gitlab.Api.Count(HttpMethod.Put, MergeRequest + "/merge"));
        Assert.Equal(JobStatus.Succeeded, (await PollJobAsync(app, id)).Status);
    }

    [Fact]
    public async Task FR140_A_merge_request_closed_without_merging_fails_with_PR_CLOSED_and_the_project_stays_in_release()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var gitlab = new GitLabState(remote);
        var (app, fixture) = await SetUpAsync(gitlab, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        Assert.Equal(ProjectStatus.AwaitingMerge, (await LifecycleSeed.ProjectAsync(app, id)).Status);

        gitlab.State = "closed";
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var failed = await LatestAsync(fixture.Viewer.Client, id);
        Assert.Equal((ReleaseStatus.Failed, ErrorCodes.PrClosed), (failed.Status, failed.ErrorCode));
        var project = await LifecycleSeed.ProjectAsync(app, id);
        Assert.Equal((Phase.Release, ProjectStatus.Releasing), (project.CurrentPhase, project.Status));
        Assert.Equal(JobStatus.Succeeded, (await PollJobAsync(app, id)).Status);
    }

    [Fact]
    public async Task FR136_FR140_A_merge_request_404_after_losing_project_access_is_not_PR_CLOSED()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var gitlab = new GitLabState(remote);
        var (app, fixture) = await SetUpAsync(gitlab, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        // The connection lost access: GitLab answers 404 for the merge request and the project.
        gitlab.Hidden = true;
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        Assert.Equal(ReleaseStatus.AwaitingMerge, (await LatestAsync(fixture.Viewer.Client, id)).Status);
        Assert.Equal(ProjectStatus.AwaitingMerge, (await LifecycleSeed.ProjectAsync(app, id)).Status);
        Assert.DoesNotContain(await LifecycleSeed.EventsAsync(app, id, EventTypes.ReleaseFailed), e => e.Payload.Contains(ErrorCodes.PrClosed, StringComparison.Ordinal));
        Assert.Contains(gitlab.Api.Requests, r => r.Method == "GET" && r.Path == Project);
    }

    [Fact]
    public async Task S4_19_FR140_The_merge_poll_backs_off_to_the_rate_limit_reset_when_under_10_percent_remains()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var gitlab = new GitLabState(remote);
        var (app, fixture) = await SetUpAsync(gitlab, remote);
        using var session = fixture;
        var id = fixture.Project.Id;
        await ProjectApi.ReadAsync<GitSettingsDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { mergeStrategy = "manual" }));
        await ReleaseFlowTests.StartExecutionAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);
        await ReleaseFlowTests.ApproveReleaseGateAsync(app, fixture);
        await AgentJobs.RunQueuedAsync(app, id);

        var reset = DateTimeOffset.UtcNow.AddMinutes(20);
        gitlab.RateLimit = new Dictionary<string, string>
        {
            ["RateLimit-Remaining"] = "5",
            ["RateLimit-Limit"] = "2000",
            ["RateLimit-Reset"] = reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await AgentJobs.RunCoreAsync(app, await PollJobAsync(app, id));

        var poll = await PollJobAsync(app, id);
        Assert.Equal((JobStatus.Queued, 0), (poll.Status, poll.Attempts));
        Assert.InRange(poll.RunAfter, reset.AddSeconds(-2), reset.AddSeconds(2));
    }

    // ---------- helpers ----------

    /// <summary>The app over <paramref name="gitlab"/> with the bare remote as the git host, and a GitLab project bound to its Owner's connection.</summary>
    private async Task<(WebApplicationFactory<Program> App, ProjectFixture Fixture)> SetUpAsync(GitLabState gitlab, BareRemote remote)
    {
        var app = GitLabHarness.App(
            AgentJobs.WithResponders(factory, new FakeCodeReviewResponder(new FakeCodeReviewScript { ScoresByCycle = [0.93m] })),
            gitlab.Api,
            settings: new Dictionary<string, string>
            {
                ["GitLab:GitBaseUrlOverride"] = remote.BaseUrl,
                ["GitLab:AllowInsecureGitBaseUrl"] = "true",
            });
        var instance = await GitLabHarness.SeedInstanceAsync(app, gitlab.BaseUrl, oauth: false);
        var owner = await ApiSession.RegisterAsync(app);
        var connection = await GitLabHarness.SeedConnectionAsync(app, owner.User!.Id, instance, Token);
        var project = await ProjectApi.CreateAsync(
            owner.Client, git: new { mode = "gitlab", instanceId = instance, connectionId = connection, repoId = RepoId, repoFullName = FullPath });
        var contributor = await ApiSession.RegisterAsync(app);
        var viewer = await ApiSession.RegisterAsync(app);
        var outsider = await ApiSession.RegisterAsync(app);
        await ProjectApi.AddMemberInDbAsync(app, project.Id, contributor.User!.Id, Aide.Domain.Access.ProjectRole.Contributor);
        await ProjectApi.AddMemberInDbAsync(app, project.Id, viewer.User!.Id, Aide.Domain.Access.ProjectRole.Viewer);
        gitlab.Branch = ReleaseVersionRules.BranchName(project.Slug, project.Id, SemVer.Parse(project.DevelopmentVersion!));
        return (app, new ProjectFixture(project, owner, contributor, viewer, outsider));
    }

    private static Task<Job> PollJobAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.ProjectId == id && j.Type == JobTypes.MergePoll, Ct));

    private static Task<ReleaseVersion> VersionAsync(WebApplicationFactory<Program> app, Guid id) =>
        ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == id, Ct));

    private static async Task<ReleaseDto> LatestAsync(HttpClient client, Guid id) =>
        await ProjectApi.ReadAsync<ReleaseDto>(await client.GetAsync(new Uri($"/api/projects/{id}/releases/latest", UriKind.Relative), Ct));

    /// <summary>
    /// The stub GitLab project <c>acme/widgets</c> (id 77): merge request !3 (draft until marked ready), its readiness,
    /// statuses, the merge (a merge commit in the bare repository), tags and the GitLab Release (an annotated tag in the bare
    /// repository).
    /// </summary>
    private sealed class GitLabState
    {
        private readonly BareRemote _remote;

        public GitLabState(BareRemote remote)
        {
            _remote = remote;
            BaseUrl = GitLabHarness.UniqueBaseUrl();
            Api = new FakeGitHubApi()
                .On(HttpMethod.Get, Project, _ => Hidden ? (HttpStatusCode.NotFound, """{"message":"404 Project Not Found"}""") : (HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    id = 77,
                    path_with_namespace = FullPath,
                    default_branch = "main",
                    web_url = WebUrl,
                    empty_repo = false,
                    merge_method = "merge",
                    squash_option = "default_off",
                    only_allow_merge_if_pipeline_succeeds = PipelinesMustSucceed,
                    permissions = new { project_access = new { access_level = 40 }, group_access = (object?)null },
                })))
                .On(HttpMethod.Get, Project + "/repository/branches/main", HttpStatusCode.OK, """{"name":"main","commit":{"id":"1111111111111111111111111111111111111111"}}""")
                .On(HttpMethod.Get, Project + "/repository/tree", HttpStatusCode.OK, """[{"name":"README.md","type":"blob","path":"README.md"}]""")
                .On(HttpMethod.Get, Project + "/merge_requests", _ => (HttpStatusCode.OK, Opened ? $"[{MergeRequestJson()}]" : "[]"))
                .On(HttpMethod.Post, Project + "/merge_requests", r =>
                {
                    Opened = true;
                    Title = GitLabHarness.Body(r).GetProperty("title").GetString()!;
                    return (HttpStatusCode.Created, MergeRequestJson());
                })
                .OnWithHeaders(HttpMethod.Get, MergeRequest, _ => Hidden
                    ? (HttpStatusCode.NotFound, """{"message":"404 Not found"}""", null)
                    : (HttpStatusCode.OK, MergeRequestJson(), RateLimit))
                .On(HttpMethod.Put, MergeRequest, r =>
                {
                    if (GitLabHarness.Body(r).TryGetProperty("title", out var title))
                    {
                        Title = title.GetString()!;
                    }

                    return (HttpStatusCode.OK, MergeRequestJson());
                })
                .On(HttpMethod.Get, MergeRequest + "/approvals", _ => (HttpStatusCode.OK, JsonSerializer.Serialize(new { approved = Approved })))
                .On(HttpMethod.Get, Project + "/protected_branches/main", HttpStatusCode.OK, """{"name":"main","merge_access_levels":[{"access_level":40}]}""")
                .On(HttpMethod.Put, MergeRequest + "/merge", r => MergeResponse!(r))
                .On(HttpMethod.Get, Project + "/repository/tags/" + Tag, _ => TagCommit is { } commit
                    ? (HttpStatusCode.OK, JsonSerializer.Serialize(new { name = Tag, commit = new { id = commit } }))
                    : (HttpStatusCode.NotFound, """{"message":"404 Tag Not Found"}"""))
                .On(HttpMethod.Get, Project + "/releases/" + Tag, _ => ReleaseCreated
                    ? (HttpStatusCode.OK, ReleaseJson())
                    : (HttpStatusCode.NotFound, """{"message":"404 Not Found"}"""))
                .On(HttpMethod.Post, Project + "/releases", r =>
                {
                    var body = GitLabHarness.Body(r);
                    var target = body.GetProperty("ref").GetString()!;
                    ReleaseFlowTests.Run(_remote.BarePath, "tag", "-a", Tag, "-m", body.GetProperty("tag_message").GetString()!, target);
                    TagCommit = target;
                    ReleaseCreated = true;
                    return (HttpStatusCode.Created, ReleaseJson());
                });
            Api.Fallback = r => r.Method == "GET" && r.Path.StartsWith(Project + "/repository/commits/", StringComparison.Ordinal) && r.Path.EndsWith("/statuses", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, JsonSerializer.Serialize(Statuses))
                : null;
            MergeResponse = _ =>
            {
                HeadAtMerge = Head();
                var sha = ReleaseFlowTests.MergeInBare(_remote, Branch!);
                State = "merged";
                MergeSha = sha;
                return (HttpStatusCode.OK, MergeRequestJson());
            };
        }

        public FakeGitHubApi Api { get; }

        public string BaseUrl { get; }

        public string WebUrl => $"{BaseUrl}/{FullPath}";

        public string MergeRequestUrl => $"{WebUrl}/-/merge_requests/3";

        public string ReleaseUrl => $"{WebUrl}/-/releases/{Tag}";

        public string? Branch { get; set; }

        public bool Opened { get; set; }

        /// <summary>The connection lost access: the project and the merge request answer 404.</summary>
        public bool Hidden { get; set; }

        public string Title { get; private set; } = "Draft: Release";

        public string State { get; set; } = "opened";

        public string DetailedStatus { get; set; } = "mergeable";

        public bool Approved { get; set; } = true;

        public bool PipelinesMustSucceed { get; init; }

        public IReadOnlyList<object> Statuses { get; set; } = [];

        public string? MergeSha { get; private set; }

        public string? HeadAtMerge { get; private set; }

        public string? TagCommit { get; private set; }

        public bool ReleaseCreated { get; private set; }

        public IReadOnlyDictionary<string, string>? RateLimit { get; set; }

        public Func<GitHubRequest, (HttpStatusCode, string)> MergeResponse { get; set; }

        public void MergedExternally(string sha)
        {
            State = "merged";
            MergeSha = sha;
        }

        private string? Head() => Branch is null ? null : ReleaseFlowTests.BareSha(_remote, $"refs/heads/{Branch}");

        private string MergeRequestJson() => JsonSerializer.Serialize(new
        {
            iid = 3,
            title = Title,
            web_url = MergeRequestUrl,
            state = State,
            draft = Title.StartsWith("Draft:", StringComparison.Ordinal),
            detailed_merge_status = State == "opened" ? DetailedStatus : "not_open",
            has_conflicts = false,
            blocking_discussions_resolved = true,
            source_branch = Branch,
            target_branch = "main",
            sha = HeadAtMerge ?? Head(),
            merge_commit_sha = MergeSha,
            squash_commit_sha = (string?)null,
            head_pipeline = new { id = 9, status = "success" },
        });

        private string ReleaseJson() =>
            JsonSerializer.Serialize(new { tag_name = Tag, name = "0.1.0", _links = new { self = ReleaseUrl } });
    }
}
