using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aide.Api.Features.Execution;
using Aide.Api.Features.Git;
using Aide.Api.Features.Projects;
using Aide.Api.Jobs;
using Aide.Domain.Access;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Execution;
using Aide.Domain.Gates;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Git;

/// <summary>
/// FR-34–FR-36 end to end with the Fake LLM: Local (branch comparison as the "PR") and GitHub, where git clones from and
/// pushes to a local bare repository (<see cref="BareRemote"/>, file:// allowed by a tests-only switch) and the REST API is
/// <see cref="FakeGitHubApi"/>. Nothing reaches github.com.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitProviderFlowTests(AideApiFactory factory)
{
    private const string Token = "ghp_phase9SecretToken0123456789abcdef";
    private const string Pulls = "/repos/acme/widgets/pulls";
    private const string PrUrl = "https://github.com/acme/widgets/pull/42";

    private static readonly string Basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{Token}"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- Local ----------

    [Fact]
    public async Task FR36_Local_last_step_records_the_branch_comparison_and_advances_to_review()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        var branch = ExecutionRules.BranchName(fixture.Project.Slug, id);
        await SeedExecutionAsync(factory, id, 2);
        await ControlAsync(fixture.Contributor.Client, id, "start");

        var jobs = await AgentJobs.RunQueuedAsync(factory, id);

        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Review, ProjectStatus.Reviewing, 5), (project.CurrentPhase, project.Status, project.ProjectVersion));
        var transition = (await LifecycleSeed.TransitionsAsync(factory, id)).Single(t => t.ToPhase == Phase.Review);
        Assert.Equal((Phase.Execution, SystemActor.Name, PullRequestJobHandler.AdvanceReason), (transition.FromPhase, transition.Actor, transition.Reason));
        Assert.Equal(JobStatus.Succeeded, Assert.Single(jobs, j => j.Type == JobTypes.PullRequest).Status);
        Assert.Equal(JobStatus.Succeeded, Assert.Single(jobs, j => j.Type == JobTypes.Review).Status); // entering review runs cycle 1 (FR-38)

        var opened = Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestOpened));
        var payload = LifecycleSeed.Payload(opened.Payload);
        Assert.Equal("local", payload.GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("number").ValueKind);
        Assert.Equal((2, 2, branch), (payload.GetProperty("commitCount").GetInt32(), payload.GetProperty("fileCount").GetInt32(), payload.GetProperty("headBranch").GetString()));

        var state = await StateAsync(fixture.Viewer.Client, id);
        Assert.Equal(new PullRequestSummaryDto(GitProvider.Local, "main", branch, null, null, opened.CreatedAt), state.PullRequest);

        var pr = await PullRequestAsync(fixture.Viewer.Client, id);
        Assert.True(pr.ComparisonAvailable);
        Assert.Equal((GitProvider.Local, "main", branch, (int?)null, (string?)null, false), (pr.Provider, pr.BaseBranch, pr.HeadBranch, pr.Number, pr.Url, pr.Truncated));
        Assert.Equal(["[AIDE][Phase:execution][Step:2] Step 2", "[AIDE][Phase:execution][Step:1] Step 1"], pr.Commits.Select(c => c.Subject));
        Assert.All(pr.Commits, c => Assert.Null(c.Url));
        Assert.Equal(
            [new PullRequestFileDto("src/part1.js", PullRequestFileStatus.Added), new PullRequestFileDto("src/part2.js", PullRequestFileStatus.Added)],
            pr.Files);
        Assert.Equal(await TestGit.RunAsync(WorkingCopy(id), Ct, "rev-parse", branch), pr.HeadSha);
        Assert.Equal(await TestGit.RunAsync(WorkingCopy(id), Ct, "rev-parse", "main"), pr.BaseSha);
    }

    [Fact]
    public async Task FR36_A_redelivered_pull_request_job_records_one_PR_and_advances_once()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(factory, id, 1);
        await ControlAsync(fixture.Contributor.Client, id, "start");
        var jobs = await AgentJobs.RunQueuedAsync(factory, id);

        await AgentJobs.RunAsync(factory, jobs.Single(j => j.Type == JobTypes.PullRequest));

        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestOpened));
        Assert.Single(await LifecycleSeed.TransitionsAsync(factory, id), t => t.ToPhase == Phase.Review);
        Assert.Equal(5, (await LifecycleSeed.ProjectAsync(factory, id)).ProjectVersion);
        Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.Review);
    }

    [Fact]
    public async Task FR36_The_pull_request_is_NOT_FOUND_before_it_is_opened_and_for_outsiders()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;

        await Problems.AssertAsync(await GetPullRequestAsync(fixture.Viewer.Client, id), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Null((await StateAsync(fixture.Viewer.Client, id)).PullRequest);

        await SeedExecutionAsync(factory, id, 1);
        await ControlAsync(fixture.Contributor.Client, id, "start");
        await AgentJobs.RunQueuedAsync(factory, id);

        Assert.Equal(HttpStatusCode.OK, (await GetPullRequestAsync(fixture.Viewer.Client, id)).StatusCode);
        await Problems.AssertAsync(await GetPullRequestAsync(fixture.Outsider.Client, id), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR36_Advancing_to_review_by_hand_before_the_PR_exists_queues_the_pull_request_job()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(factory, id, 1);
        await ControlAsync(fixture.Contributor.Client, id, "start");
        await AgentJobs.RunAsync(factory, Assert.Single(await LifecycleSeed.JobsAsync(factory, id)));
        var queued = Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.PullRequest);
        await ProjectApi.WithDbAsync(factory, db => db.Jobs.Where(j => j.Id == queued.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled), Ct));

        var advanced = await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id, "Ready for review");

        Assert.Equal(HttpStatusCode.OK, advanced.StatusCode);
        var requeued = Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.PullRequest && j.Status == JobStatus.Queued);
        Assert.Equal(PullRequestJobPayload.PhaseAdvanced, LifecycleSeed.Payload(requeued.Payload).GetProperty("trigger").GetString());
        await AgentJobs.RunQueuedAsync(factory, id);
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestOpened));
        Assert.Single(await LifecycleSeed.TransitionsAsync(factory, id), t => t.ToPhase == Phase.Review);
        Assert.Equal((Phase.Review, 5), ((await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(factory, id)).ProjectVersion));
    }

    // ---------- GitHub ----------

    [Fact]
    public async Task FR35_GitHub_start_clones_over_HTTPS_with_the_token_and_never_persists_it()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        await using var app = remote.App(factory, HappyGitHub(), out var git);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        var branch = ExecutionRules.BranchName(fixture.Project.Slug, id);
        await SeedExecutionAsync(factory, id, 1);

        await ControlAsync(fixture.Contributor.Client, id, "start");

        Assert.Equal(branch, await TestGit.RunAsync(WorkingCopy(id), Ct, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal("Initial import", await TestGit.RunAsync(WorkingCopy(id), Ct, "log", "-1", "--format=%s", branch));
        Assert.True(File.Exists(Path.Combine(WorkingCopy(id), "README.md")));
        Assert.Equal(remote.BaseUrl + "acme/widgets.git", await TestGit.RunAsync(WorkingCopy(id), Ct, "remote", "get-url", "origin"));

        var clone = Assert.Single(git.Calls, c => c.Arguments.Contains("clone"));
        Assert.Contains($"Authorization: Basic {Basic}", clone.Config!.Values);
        Assert.DoesNotContain(clone.Arguments, a => a.Contains(Token, StringComparison.Ordinal) || a.Contains(Basic, StringComparison.Ordinal));
        await AssertTokenNeverStoredAsync(id);
    }

    [Fact]
    public async Task FR34_FR36_GitHub_steps_are_pushed_one_PR_is_opened_and_the_project_enters_review()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var api = HappyGitHub();
        await using var app = remote.App(factory, api, out var git);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        var branch = ExecutionRules.BranchName(fixture.Project.Slug, id);
        await SeedExecutionAsync(factory, id, 2);
        await ControlAsync(fixture.Contributor.Client, id, "start");

        await AgentJobs.RunQueuedAsync(app, id);

        // FR-34: every accepted step was pushed; the remote branch is the local one.
        Assert.Equal(
            await TestGit.RunAsync(WorkingCopy(id), Ct, "rev-parse", branch),
            await TestGit.RunAsync(remote.BarePath, Ct, "rev-parse", $"refs/heads/{branch}"));
        Assert.All(await LifecycleSeed.EventsAsync(factory, id, EventTypes.StepCompleted), e => Assert.True(LifecycleSeed.Payload(e.Payload).GetProperty("pushed").GetBoolean()));
        Assert.True(git.Calls.Count(c => c.Arguments.Contains("push")) >= 2);
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.GitPushFailed));

        // FR-36: one PR from the project branch to main, described from the spec, the plan and the steps.
        Assert.Equal(1, api.Count(HttpMethod.Post, Pulls));
        var lookup = api.Requests.First(r => r.Method == "GET" && r.Path == Pulls);
        Assert.Contains($"head=acme:{branch}", lookup.Query, StringComparison.Ordinal);
        Assert.Contains("base=main", lookup.Query, StringComparison.Ordinal);
        Assert.Contains("state=open", lookup.Query, StringComparison.Ordinal);
        var create = api.Requests.Single(r => r.Method == "POST" && r.Path == Pulls);
        Assert.Equal($"Bearer {Token}", create.Authorization);
        using var body = JsonDocument.Parse(create.Body);
        Assert.Equal($"[AIDE] {fixture.Project.Name} — Implementation (2 steps)", body.RootElement.GetProperty("title").GetString());
        Assert.Equal((branch, "main"), (body.RootElement.GetProperty("head").GetString(), body.RootElement.GetProperty("base").GetString()));
        var description = body.RootElement.GetProperty("body").GetString()!;
        Assert.Contains("> Express API.", description, StringComparison.Ordinal);
        Assert.Contains("### Plan: implementation_plan v1 (2 steps)", description, StringComparison.Ordinal);
        Assert.Contains("| 2 | Step 2 | `", description, StringComparison.Ordinal);

        var settings = await ProjectApi.WithDbAsync(factory, db => db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == id, Ct));
        Assert.Equal((42, PrUrl), (settings.PullRequestNumber, settings.PullRequestUrl));
        var state = await StateAsync(fixture.Viewer.Client, id);
        Assert.Equal((Phase.Review, GitProvider.Github, 42, PrUrl), (state.Phase, state.PullRequest!.Provider, state.PullRequest.Number, state.PullRequest.Url));
        var pr = await PullRequestAsync(fixture.Viewer.Client, id);
        Assert.Equal(2, pr.Commits.Count);
        Assert.All(pr.Commits, c => Assert.Equal($"https://github.com/acme/widgets/commit/{c.Sha}", c.Url));
        await AssertTokenNeverStoredAsync(id);
    }

    [Fact]
    public async Task FR36_An_open_PR_for_the_project_branch_is_reused_not_duplicated()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, Pulls, HttpStatusCode.OK, """[{"number":7,"html_url":"https://github.com/acme/widgets/pull/7"}]""")
            .On(HttpMethod.Post, Pulls, HttpStatusCode.Created, """{"number":99,"html_url":"https://github.com/acme/widgets/pull/99"}""");
        await using var app = remote.App(factory, api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(factory, id, 1);
        await ControlAsync(fixture.Contributor.Client, id, "start");

        await AgentJobs.RunQueuedAsync(app, id);

        Assert.Equal(0, api.Count(HttpMethod.Post, Pulls));
        var opened = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestOpened)).Payload);
        Assert.Equal((7, true), (opened.GetProperty("number").GetInt32(), opened.GetProperty("reused").GetBoolean()));
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);
    }

    [Fact]
    public async Task FR36_A_redelivered_job_after_GitHub_created_the_PR_reuses_it()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var created = 0;
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, Pulls, _ => (HttpStatusCode.OK, created == 0 ? "[]" : $$"""[{"number":42,"html_url":"{{PrUrl}}"}]"""))
            .On(HttpMethod.Post, Pulls, _ =>
            {
                Interlocked.Increment(ref created);
                return (HttpStatusCode.Created, $$"""{"number":42,"html_url":"{{PrUrl}}"}""");
            });
        await using var app = remote.App(factory, api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(factory, id, 1);
        await ControlAsync(fixture.Contributor.Client, id, "start");
        var job = (await AgentJobs.RunQueuedAsync(app, id)).Single(j => j.Type == JobTypes.PullRequest);

        // The PR exists on GitHub but the job's transaction never committed: the job is delivered again.
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            await db.Events.Where(e => e.ProjectId == id && e.Type == EventTypes.PullRequestOpened).ExecuteDeleteAsync(Ct);
            await db.StateTransitions.Where(t => t.ProjectId == id && t.ToPhase == Phase.Review).ExecuteDeleteAsync(Ct);
            await db.Jobs.Where(j => j.ProjectId == id && j.Type == JobTypes.Review).ExecuteDeleteAsync(Ct);
            await db.ProjectGitSettings.Where(s => s.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(g => g.PullRequestNumber, (int?)null).SetProperty(g => g.PullRequestUrl, (string?)null), Ct);
        });
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Execution, 4, ApprovalMode.Advisory);
        await AgentJobs.RunAsync(app, job);

        Assert.Equal(1, created);
        var opened = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestOpened)).Payload);
        Assert.Equal((42, true), (opened.GetProperty("number").GetInt32(), opened.GetProperty("reused").GetBoolean()));
        Assert.Equal((Phase.Review, 5), ((await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase, (await LifecycleSeed.ProjectAsync(factory, id)).ProjectVersion));
    }

    [Fact]
    public async Task FR36_GitHub_5xx_is_transient_401_is_permanent_and_a_later_retry_opens_the_PR()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, Pulls, HttpStatusCode.OK, "[]")
            .On(HttpMethod.Post, Pulls, HttpStatusCode.BadGateway, """{"message":"Server Error"}""");
        await using var app = remote.App(factory, api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(factory, id, 1);
        await ControlAsync(fixture.Contributor.Client, id, "start");

        var job = (await AgentJobs.RunQueuedAsync(app, id)).Single(j => j.Type == JobTypes.PullRequest);
        // The test runner records a transient failure as dead (no retries); the worker retries it with backoff first.
        Assert.Equal(JobStatus.Dead, job.Status);
        Assert.Contains("HTTP 502", job.LastError, StringComparison.Ordinal);
        Assert.Equal(Phase.Execution, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);

        api.On(HttpMethod.Post, Pulls, HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        job = await RedeliverAsync(app, job);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("invalid or expired", job.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, job.LastError, StringComparison.Ordinal);

        api.On(HttpMethod.Post, Pulls, HttpStatusCode.Created, $$"""{"number":42,"html_url":"{{PrUrl}}"}""");
        job = await RedeliverAsync(app, job);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);
    }

    [Fact]
    public async Task FR34_A_rejected_push_keeps_the_step_completed_records_GIT_PUSH_FAILED_and_holds_the_PR()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        await using var app = remote.App(factory, HappyGitHub(), out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        var branch = ExecutionRules.BranchName(fixture.Project.Slug, id);
        await SeedExecutionAsync(factory, id, 1);
        await ControlAsync(fixture.Contributor.Client, id, "start");

        // Someone else pushes to the project branch on the remote: AIDE's push is no longer a fast-forward.
        var other = Path.Combine(remote.Root, "other");
        await TestGit.RunAsync(remote.Root, Ct, "clone", "-q", remote.BarePath, other);
        await TestGit.RunAsync(other, Ct, "checkout", "-q", "-b", branch);
        await File.WriteAllTextAsync(Path.Combine(other, "foreign.txt"), "x\n", Ct);
        await TestGit.RunAsync(other, Ct, "add", "foreign.txt");
        await TestGit.RunAsync(other, Ct, "commit", "-q", "-m", "Foreign commit");
        await TestGit.RunAsync(other, Ct, "push", "-q", "origin", branch);

        await AgentJobs.RunAsync(app, Assert.Single(await LifecycleSeed.JobsAsync(factory, id)));

        var step = Assert.Single(await ProjectApi.WithDbAsync(factory, db => db.ExecutionSteps.AsNoTracking().Where(s => s.ProjectId == id).ToListAsync(Ct)));
        Assert.Equal(StepStatus.Completed, step.Status);
        Assert.False(LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.StepCompleted)).Payload).GetProperty("pushed").GetBoolean());
        var failed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.GitPushFailed)).Payload);
        Assert.False(failed.GetProperty("transient").GetBoolean());
        Assert.Contains("rejected", failed.GetProperty("reason").GetString(), StringComparison.Ordinal);

        // The PR job pushes first: still rejected, so it fails for good and the project stays in execution.
        var prJob = Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.PullRequest);
        await AgentJobs.RunAsync(app, prJob);
        prJob = Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.PullRequest);
        Assert.Equal(JobStatus.Failed, prJob.Status);
        Assert.Contains("could not be pushed", prJob.LastError, StringComparison.Ordinal);
        Assert.Equal(Phase.Execution, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);

        // A pull request job that ends without succeeding is visible in the feed.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AideDbContext>();
            foreach (var hook in scope.ServiceProvider.GetServices<IJobEndedHook>())
            {
                await hook.OnJobEndedAsync(db, new JobEnded(prJob.Id, JobTypes.PullRequest, id, false, prJob.LastError), Ct);
            }

            await db.SaveChangesAsync(Ct);
        }

        var ended = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestFailed)).Payload);
        Assert.Contains("could not be pushed", ended.GetProperty("reason").GetString(), StringComparison.Ordinal);
        await AssertTokenNeverStoredAsync(id);
    }

    [Fact]
    public async Task FR35_GitHub_execution_without_a_token_is_PHASE_PRECONDITION_FAILED()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        await using var app = remote.App(factory, HappyGitHub(), out var git);
        using var fixture = await ProjectApi.FixtureAsync(app, git: new { provider = "github", repoUrl = BareRemote.RepoUrl, defaultBranch = "main" });
        var id = fixture.Project.Id;
        await SeedExecutionAsync(factory, id, 1);

        var response = await PostAsync(fixture.Contributor.Client, id, "start");

        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.PhasePreconditionFailed);
        Assert.Empty(git.Calls);
        Assert.False(Directory.Exists(Path.Combine(WorkingCopy(id), ".git")));
    }

    [Fact]
    public async Task FR36_Changing_the_repository_forgets_the_recorded_PR()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        const string GadgetsPulls = "/repos/acme/gadgets/pulls";
        const string GadgetsUrl = "https://github.com/acme/gadgets/pull/5";
        var api = HappyGitHub()
            .On(HttpMethod.Get, GadgetsPulls, HttpStatusCode.OK, "[]")
            .On(HttpMethod.Post, GadgetsPulls, HttpStatusCode.Created, $$"""{"number":5,"html_url":"{{GadgetsUrl}}"}""");
        await using var app = remote.App(factory, api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(factory, id, 1);
        await ControlAsync(fixture.Contributor.Client, id, "start");
        await AgentJobs.RunQueuedAsync(app, id);
        Assert.Equal((Phase.Review, 42), ((await StateAsync(fixture.Viewer.Client, id)).Phase, (await StateAsync(fixture.Viewer.Client, id)).PullRequest!.Number));

        var unchanged = await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { defaultBranch = "main" });
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal(42, await PrNumberAsync(id));
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestForgotten));

        await TestGit.RunAsync(remote.Root, Ct, "clone", "-q", "--bare", remote.BarePath, Path.Combine(remote.Root, BareRemote.Owner, "gadgets.git"));
        var changed = await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { repoUrl = "https://github.com/acme/gadgets" });

        // The old PR is forgotten everywhere it is read, and the job is queued again for the new repository.
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Null(await PrNumberAsync(id));
        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestForgotten));
        Assert.Null((await StateAsync(fixture.Viewer.Client, id)).PullRequest);
        await Problems.AssertAsync(await GetPullRequestAsync(fixture.Viewer.Client, id), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        var requeued = Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.PullRequest && j.Status == JobStatus.Queued);
        Assert.Equal(PullRequestJobPayload.SettingsChanged, LifecycleSeed.Payload(requeued.Payload).GetProperty("trigger").GetString());

        await AgentJobs.RunQueuedAsync(app, id);

        Assert.Equal(1, api.Count(HttpMethod.Post, GadgetsPulls));
        Assert.Equal(2, (await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestOpened)).Count);
        var state = await StateAsync(fixture.Viewer.Client, id);
        Assert.Equal((Phase.Review, 5, GadgetsUrl), (state.Phase, state.PullRequest!.Number, state.PullRequest.Url));
        Assert.Equal((5, GadgetsUrl), ((await PullRequestAsync(fixture.Viewer.Client, id)).Number, (await PullRequestAsync(fixture.Viewer.Client, id)).Url));
        Assert.Single(await LifecycleSeed.TransitionsAsync(factory, id), t => t.ToPhase == Phase.Review);
    }

    // ---------- FR-36: superseded plans and retries ----------

    [Fact]
    public async Task FR36_A_rollback_while_the_PR_job_runs_records_nothing_and_advances_nothing()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        Guid id = default;
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, Pulls, HttpStatusCode.OK, "[]")
            .On(HttpMethod.Post, Pulls, _ =>
            {
                // While GitHub opens the PR, the project is rolled back to planning and the plan's steps are reset.
                ProjectApi.WithDbAsync(factory, async db =>
                {
                    await db.Projects.Where(p => p.Id == id).ExecuteUpdateAsync(
                        s => s.SetProperty(p => p.CurrentPhase, Phase.Planning).SetProperty(p => p.Status, PhaseOrder.StatusFor(Phase.Planning)).SetProperty(p => p.ProjectVersion, 3), Ct);
                    await db.ExecutionSteps.Where(st => st.ProjectId == id).ExecuteUpdateAsync(st => st.SetProperty(x => x.Status, StepStatus.Pending), Ct);
                }).GetAwaiter().GetResult();
                return (HttpStatusCode.Created, $$"""{"number":42,"html_url":"{{PrUrl}}"}""");
            });
        await using var app = remote.App(factory, api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        id = fixture.Project.Id;
        await SeedExecutionAsync(factory, id, 1);
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Execution, 4, ApprovalMode.Strict);
        await ControlAsync(fixture.Contributor.Client, id, "start");

        var jobs = await AgentJobs.RunQueuedAsync(app, id);

        Assert.Equal(JobStatus.Succeeded, Assert.Single(jobs, j => j.Type == JobTypes.PullRequest).Status);
        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Planning, 3), (project.CurrentPhase, project.ProjectVersion));
        Assert.Empty(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestOpened));
        Assert.DoesNotContain(await LifecycleSeed.TransitionsAsync(factory, id), t => t.Actor == SystemActor.Name);
        Assert.Empty(await LifecycleSeed.GatesAsync(factory, id));
        Assert.Null(await PrNumberAsync(id));
        Assert.Null((await StateAsync(fixture.Viewer.Client, id)).PullRequest);
    }

    [Fact]
    public async Task FR36_The_system_advance_is_refused_with_WRONG_PHASE_when_the_project_left_execution()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Planning, 3, ApprovalMode.Strict);

        await using var scope = factory.Services.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<Aide.Api.Features.Lifecycle.LifecycleService>();
        var advanced = await lifecycle.AdvanceAsync(id, PullRequestJobHandler.AdvanceReason, SystemActor.Name, Ct, expectedFrom: Phase.Execution);

        Assert.Equal(ErrorCodes.WrongPhase, advanced.Error?.Code);
        Assert.Equal(Phase.Planning, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);
        Assert.DoesNotContain(await LifecycleSeed.TransitionsAsync(factory, id), t => t.Actor == SystemActor.Name);
        Assert.Empty(await LifecycleSeed.GatesAsync(factory, id));
    }

    [Fact]
    public async Task FR36_A_failed_PR_job_can_be_retried_in_review_and_only_records_the_PR()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(factory, id, 1);
        await ControlAsync(fixture.Contributor.Client, id, "start");
        await AgentJobs.RunAsync(factory, Assert.Single(await LifecycleSeed.JobsAsync(factory, id)));
        await DeadLetterPullRequestJobsAsync(id);
        Assert.Equal(HttpStatusCode.OK, (await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id, "Ready for review")).StatusCode);
        await DeadLetterPullRequestJobsAsync(id);
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);

        await Problems.AssertAsync(await RetryPullRequestAsync(fixture.Viewer.Client, id), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await RetryPullRequestAsync(fixture.Outsider.Client, id), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        var retried = await RetryPullRequestAsync(fixture.Contributor.Client, id);
        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        var queued = Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.PullRequest && j.Status == JobStatus.Queued);
        Assert.Equal(PullRequestJobPayload.Retried, LifecycleSeed.Payload(queued.Payload).GetProperty("trigger").GetString());
        await Problems.AssertAsync(await RetryPullRequestAsync(fixture.Contributor.Client, id), HttpStatusCode.Conflict, ErrorCodes.Conflict);

        await AgentJobs.RunAsync(factory, queued);

        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestOpened));
        var project = await LifecycleSeed.ProjectAsync(factory, id);
        Assert.Equal((Phase.Review, 5), (project.CurrentPhase, project.ProjectVersion));
        Assert.Single(await LifecycleSeed.TransitionsAsync(factory, id), t => t.ToPhase == Phase.Review);
        Assert.NotNull((await StateAsync(fixture.Viewer.Client, id)).PullRequest);
        await Problems.AssertAsync(await RetryPullRequestAsync(fixture.Contributor.Client, id), HttpStatusCode.Conflict, ErrorCodes.PhasePreconditionFailed);
    }

    [Fact]
    public async Task FR36_A_failed_PR_job_retried_in_execution_opens_the_PR_and_advances_to_review()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await Problems.AssertAsync(await RetryPullRequestAsync(fixture.Contributor.Client, id), HttpStatusCode.Conflict, ErrorCodes.PhasePreconditionFailed);
        await SeedExecutionAsync(factory, id, 2);
        await ControlAsync(fixture.Contributor.Client, id, "start");
        await Problems.AssertAsync(await RetryPullRequestAsync(fixture.Contributor.Client, id), HttpStatusCode.Conflict, ErrorCodes.PhasePreconditionFailed);
        await AgentJobs.RunAsync(factory, Assert.Single(await LifecycleSeed.JobsAsync(factory, id)));
        await AgentJobs.RunAsync(factory, Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.Execution && j.Status == JobStatus.Queued));
        await DeadLetterPullRequestJobsAsync(id);

        Assert.Equal(HttpStatusCode.Accepted, (await RetryPullRequestAsync(fixture.Contributor.Client, id)).StatusCode);
        await AgentJobs.RunQueuedAsync(factory, id);

        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestOpened));
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);
        var transition = Assert.Single(await LifecycleSeed.TransitionsAsync(factory, id), t => t.ToPhase == Phase.Review);
        Assert.Equal(SystemActor.Name, transition.Actor);
    }

    [Fact]
    public async Task FR36_Saving_a_new_token_in_review_without_a_PR_queues_the_PR_job_again()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, Pulls, HttpStatusCode.OK, "[]")
            .On(HttpMethod.Post, Pulls, HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
        await using var app = remote.App(factory, api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(factory, id, 1);
        await ControlAsync(fixture.Contributor.Client, id, "start");
        var failed = (await AgentJobs.RunQueuedAsync(app, id)).Single(j => j.Type == JobTypes.PullRequest);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal(HttpStatusCode.OK, (await LifecycleSeed.AdvanceAsync(fixture.Contributor.Client, id, "Review anyway")).StatusCode);
        await AgentJobs.RunQueuedAsync(app, id);
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);
        Assert.DoesNotContain(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.PullRequest && j.Status == JobStatus.Queued);

        api.On(HttpMethod.Post, Pulls, HttpStatusCode.Created, $$"""{"number":42,"html_url":"{{PrUrl}}"}""");
        var saved = await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { token = Token });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var queued = Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.PullRequest && j.Status == JobStatus.Queued);
        Assert.Equal(PullRequestJobPayload.SettingsChanged, LifecycleSeed.Payload(queued.Payload).GetProperty("trigger").GetString());
        await AgentJobs.RunQueuedAsync(app, id);
        Assert.Equal((Phase.Review, 42), ((await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase, await PrNumberAsync(id)));
        Assert.Single(await LifecycleSeed.TransitionsAsync(factory, id), t => t.ToPhase == Phase.Review);

        // With the PR recorded, saving settings again queues nothing.
        Assert.Equal(HttpStatusCode.OK, (await ProjectApi.PatchAsync(fixture.Owner.Client, $"{fixture.Path}/settings/git", new { token = Token })).StatusCode);
        Assert.DoesNotContain(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.PullRequest && j.Status == JobStatus.Queued);
    }

    [Fact]
    public async Task FR36_A_PR_job_with_no_token_fails_for_good_not_transiently()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        await using var app = remote.App(factory, HappyGitHub(), out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(factory, id, 1);
        await ControlAsync(fixture.Contributor.Client, id, "start");
        await AgentJobs.RunAsync(app, Assert.Single(await LifecycleSeed.JobsAsync(factory, id)));
        await ProjectApi.WithDbAsync(factory, db => db.ProjectGitSettings.Where(s => s.ProjectId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.TokenEncrypted, (string?)null), Ct));

        await AgentJobs.RunAsync(app, Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.PullRequest));

        var job = Assert.Single(await LifecycleSeed.JobsAsync(factory, id), j => j.Type == JobTypes.PullRequest);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("No access token", job.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR36_A_reused_open_PR_gets_the_current_title_and_description_and_one_event_per_plan()
    {
        using var remote = await BareRemote.CreateAsync(Ct);
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, Pulls, HttpStatusCode.OK, """[{"number":7,"html_url":"https://github.com/acme/widgets/pull/7"}]""")
            .On(HttpMethod.Patch, Pulls + "/7", HttpStatusCode.OK, """{"number":7}""");
        await using var app = remote.App(factory, api, out _);
        using var fixture = await GitHubFixtureAsync(app);
        var id = fixture.Project.Id;
        await SeedExecutionAsync(factory, id, 1);
        await ControlAsync(fixture.Contributor.Client, id, "start");
        var job = (await AgentJobs.RunQueuedAsync(app, id)).Single(j => j.Type == JobTypes.PullRequest);

        var patch = Assert.Single(api.Requests, r => r.Method == "PATCH");
        using (var body = JsonDocument.Parse(patch.Body))
        {
            Assert.Equal($"[AIDE] {fixture.Project.Name} — Implementation (1 step)", body.RootElement.GetProperty("title").GetString());
            Assert.Contains("### Plan: implementation_plan v1 (1 steps)", body.RootElement.GetProperty("body").GetString(), StringComparison.Ordinal);
        }

        // Delivered again in execution (e.g. its advance was lost): the PR is reused, but PULL_REQUEST_OPENED isn't repeated.
        await ProjectApi.WithDbAsync(factory, db => db.StateTransitions.Where(t => t.ProjectId == id && t.ToPhase == Phase.Review).ExecuteDeleteAsync(Ct));
        await LifecycleSeed.SetProjectAsync(factory, id, Phase.Execution, 4, ApprovalMode.Advisory);
        await RedeliverAsync(app, job);

        Assert.Single(await LifecycleSeed.EventsAsync(factory, id, EventTypes.PullRequestOpened));
        Assert.Equal(Phase.Review, (await LifecycleSeed.ProjectAsync(factory, id)).CurrentPhase);
        Assert.Equal(2, api.Count(HttpMethod.Patch, Pulls + "/7"));
    }

    // ---------- helpers ----------

    private static FakeGitHubApi HappyGitHub() => new FakeGitHubApi()
        .On(HttpMethod.Get, Pulls, HttpStatusCode.OK, "[]")
        .On(HttpMethod.Post, Pulls, HttpStatusCode.Created, $$"""{"number":42,"html_url":"{{PrUrl}}"}""");

    private static Task<ProjectFixture> GitHubFixtureAsync(WebApplicationFactory<Program> app) =>
        ProjectApi.FixtureAsync(app, git: new { provider = "github", repoUrl = BareRemote.RepoUrl, defaultBranch = "main", token = Token });

    /// <summary>The project in execution (advisory, version 4) with request, spec and a locked plan of <paramref name="count"/> chained steps.</summary>
    private static async Task SeedExecutionAsync(WebApplicationFactory<Program> app, Guid id, int count)
    {
        await LifecycleSeed.SetProjectAsync(app, id, Phase.Execution, 4, ApprovalMode.Advisory);
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ProjectRequest, content: "# Todo API\n\n## Summary\nManage todos.");
        await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.TechnicalSpecification, content: "# Spec\n\nExpress API.");
        var plan = await LifecycleSeed.AddArtifactAsync(app, id, ArtifactType.ImplementationPlan, content: AgentJobs.PlanMarkdown(count));
        await ProjectApi.WithDbAsync(app, async db =>
        {
            db.ExecutionSteps.AddRange(Enumerable.Range(1, count).Select(n => new ExecutionStep
            {
                ProjectId = id,
                PlanArtifactId = plan.Id,
                StepNumber = n,
                Title = $"Step {n}",
                Task = $"Build part {n}.",
                DeclaredFiles = [new DeclaredFile($"src/part{n}.js", FileAction.Create)],
                Dependencies = n == 1 ? [] : [n - 1],
                Status = StepStatus.Pending,
            }));
            await db.SaveChangesAsync(Ct);
        });
    }

    /// <summary>Queues a finished job again and delivers it; returns the job row afterwards.</summary>
    private async Task<Job> RedeliverAsync(WebApplicationFactory<Program> app, Job job)
    {
        await ProjectApi.WithDbAsync(factory, db => db.Jobs.Where(j => j.Id == job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.LastError, (string?)null), Ct));
        await AgentJobs.RunAsync(app, await ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct)));
        return await ProjectApi.WithDbAsync(factory, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct));
    }

    /// <summary>The token, raw or as the basic-auth credential, is in no Git config, event, job error or log line.</summary>
    private async Task AssertTokenNeverStoredAsync(Guid id)
    {
        var config = await File.ReadAllTextAsync(Path.Combine(WorkingCopy(id), ".git", "config"), Ct);
        Assert.DoesNotContain(Token, config, StringComparison.Ordinal);
        Assert.DoesNotContain(Basic, config, StringComparison.Ordinal);
        Assert.DoesNotContain("extraheader", config, StringComparison.OrdinalIgnoreCase);
        var stored = await ProjectApi.WithDbAsync(factory, async db =>
            string.Join('\n', await db.Events.AsNoTracking().Where(e => e.ProjectId == id).Select(e => e.Payload).ToListAsync(Ct))
            + string.Join('\n', await db.Jobs.AsNoTracking().Where(j => j.ProjectId == id).Select(j => j.LastError ?? string.Empty).ToListAsync(Ct)));
        Assert.DoesNotContain(Token, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Basic, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains(Token, StringComparison.Ordinal) || e.AllText.Contains(Basic, StringComparison.Ordinal));
    }

    private Task<int?> PrNumberAsync(Guid id) =>
        ProjectApi.WithDbAsync(factory, db => db.ProjectGitSettings.Where(s => s.ProjectId == id).Select(s => s.PullRequestNumber).SingleAsync(Ct));

    private string WorkingCopy(Guid id) => Path.Combine(factory.WorkspacesRoot, id.ToString("D"));

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string action) =>
        client.PostAsJsonAsync($"/api/projects/{id}/execution/{action}", new { }, Ct);

    private static async Task ControlAsync(HttpClient client, Guid id, string action) =>
        await ProjectApi.ReadAsync<ExecutionStateDto>(await PostAsync(client, id, action), HttpStatusCode.Accepted);

    private static async Task<ProjectStateDto> StateAsync(HttpClient client, Guid id) =>
        await ProjectApi.ReadAsync<ProjectStateDto>(await client.GetAsync(new Uri($"/api/projects/{id}/state", UriKind.Relative), Ct));

    private static Task<HttpResponseMessage> GetPullRequestAsync(HttpClient client, Guid id) =>
        client.GetAsync(new Uri($"/api/projects/{id}/git/pull-request", UriKind.Relative), Ct);

    private static async Task<PullRequestDto> PullRequestAsync(HttpClient client, Guid id) =>
        await ProjectApi.ReadAsync<PullRequestDto>(await GetPullRequestAsync(client, id));

    private static Task<HttpResponseMessage> RetryPullRequestAsync(HttpClient client, Guid id) =>
        client.PostAsJsonAsync($"/api/projects/{id}/git/pull-request", new { }, Ct);

    /// <summary>Every queued pull request job of the project ends dead (its retries ran out).</summary>
    private Task<int> DeadLetterPullRequestJobsAsync(Guid id) =>
        ProjectApi.WithDbAsync(factory, db => db.Jobs.Where(j => j.ProjectId == id && j.Type == JobTypes.PullRequest && j.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Dead), Ct));
}
