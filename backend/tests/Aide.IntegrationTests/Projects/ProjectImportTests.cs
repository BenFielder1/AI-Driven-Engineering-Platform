using System.Net;
using System.Net.Http.Json;
using Aide.Api.Agents;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Refinement;
using Aide.Api.Features.Runs;
using Aide.Api.Features.Versions;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.RepoTools;
using Aide.Infrastructure.Agents;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Projects;

/// <summary>
/// FR-73 (Local import, §8 Testing), FR-70 (Repository Analyst), FR-71 (repo tools on the imported repository), FR-72 and
/// the FR-70 summary wait: an existing Local repository with tags <c>v2.2.0</c> and <c>v2.3.0</c> is imported from under
/// <c>Git:Local:ImportRoot</c>, with the Fake provider.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProjectImportTests(AideApiFactory factory) : IAsyncLifetime
{
    private readonly string _importRoot = Path.Combine(Path.GetTempPath(), "aide-tests-imports", Guid.NewGuid().ToString("N"));
    private string _v230 = string.Empty;
    private string _head = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var repo = Path.Combine(_importRoot, "legacy");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        await TestGit.RunAsync(repo, Ct, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# Legacy shop\n\nAn existing Node.js shop.\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "app.js"), "export const app = 'shop';\n", Ct);
        await TestGit.RunAsync(repo, Ct, "add", ".");
        await TestGit.RunAsync(repo, Ct, "commit", "-q", "-m", "First release");
        await TestGit.RunAsync(repo, Ct, "tag", "v2.2.0");
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "orders.js"), "export const orders = [];\n", Ct);
        await TestGit.RunAsync(repo, Ct, "add", ".");
        await TestGit.RunAsync(repo, Ct, "commit", "-q", "-m", "Orders");
        await TestGit.RunAsync(repo, Ct, "tag", "-a", "v2.3.0", "-m", "Release 2.3.0");
        _v230 = await TestGit.RunAsync(repo, Ct, "rev-parse", "v2.3.0^{commit}");
        await File.WriteAllTextAsync(Path.Combine(repo, "CHANGELOG.md"), "Unreleased work.\n", Ct);
        await TestGit.RunAsync(repo, Ct, "add", ".");
        await TestGit.RunAsync(repo, Ct, "commit", "-q", "-m", "Unreleased");
        _head = await TestGit.RunAsync(repo, Ct, "rev-parse", "HEAD");
    }

    public ValueTask DisposeAsync()
    {
        TestGit.DeleteTree(_importRoot);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> App(params IFakeToolScript[] scripts) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Git:Local:ImportRoot", _importRoot);
            b.ConfigureTestServices(services =>
            {
                foreach (var script in scripts)
                {
                    services.AddSingleton(script);
                }
            });
        });

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, string? releasedTag, string developmentVersion, string source = "legacy", string name = "Legacy shop") =>
        client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name = ProjectApi.UniqueName(name),
                idea = "Add CSV export of orders.",
                source = "import",
                git = new { mode = "local", localSourcePath = source },
                import = new { releasedTag, developmentVersion },
            },
            Ct);

    // ---------- FR-73 ----------

    [Fact]
    public async Task FR73_Importing_v2_3_0_gives_imported_2_3_0_and_in_development_2_4_0_in_refinement_with_the_analyst_queued()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);

        var project = await ProjectApi.ReadAsync<ProjectDto>(await ImportAsync(owner.Client, "v2.3.0", "2.4.0"), HttpStatusCode.Created);

        Assert.Equal(("2.3.0", "2.4.0", Phase.Refinement, 1, "Add CSV export of orders."), (project.ReleasedVersion, project.DevelopmentVersion, project.CurrentPhase, project.Checkpoint, project.Idea));
        var versions = await ProjectApi.ReadAsync<List<ReleaseVersionDto>>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/versions", UriKind.Relative), Ct));
        Assert.Equal([("2.4.0", ReleaseVersionStatus.InDevelopment), ("2.3.0", ReleaseVersionStatus.Imported)], versions.Select(v => (v.Version, v.Status)));
        Assert.Equal(("v2.3.0", _v230), (versions[1].Tag, versions[1].MergeCommitSha));
        Assert.Equal(("v2.3.0", _v230, (string?)null), (versions[0].BaseTag, versions[0].BaseCommitSha, versions[0].BranchName));
        Assert.Equal(project.ReleaseVersionId, versions[0].Id);

        // The imported baseline has no AIDE artifacts; the Repository Analyst is queued for the version in development.
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.Artifacts.AnyAsync(a => a.ProjectId == project.Id, Ct)));
        var analysis = Assert.Single(await LifecycleSeed.JobsAsync(app, project.Id));
        Assert.Equal((JobTypes.RepositoryAnalysis, versions[0].Id.ToString()), (analysis.Type, LifecycleSeed.Payload(analysis.Payload).GetProperty("releaseVersionId").GetString()));
        var created = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.ProjectCreated)).Payload);
        Assert.Equal(("import", "v2.3.0"), (created.GetProperty("source").GetString(), created.GetProperty("importedTag").GetString()));

        // The working copy holds the repository's history and tags, without the source as a remote.
        var workingCopy = Path.Combine(factory.WorkspacesRoot, project.Id.ToString("D"));
        Assert.Equal(_head, await TestGit.RunAsync(workingCopy, Ct, "rev-parse", "main"));
        Assert.Equal("v2.2.0\nv2.3.0", await TestGit.RunAsync(workingCopy, Ct, "tag", "--list"));
        Assert.Equal(string.Empty, await TestGit.RunAsync(workingCopy, Ct, "remote"));
    }

    [Fact]
    public async Task FR73_Given_an_imported_project_when_execution_prepares_the_working_copy_then_aide_slug_v2_4_0_is_created_from_the_default_branch_head()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await ImportAsync(owner.Client, "v2.3.0", "2.4.0"), HttpStatusCode.Created);

        await using var scope = app.Services.CreateAsyncScope();
        var prepared = await scope.ServiceProvider.GetRequiredService<Aide.Api.Features.Execution.WorkingCopyPreparer>().PrepareAsync(project.Id, Ct);

        Assert.True(prepared.IsSuccess, prepared.Error?.Message);
        Assert.Equal($"aide/{project.Slug}/v2.4.0", prepared.Value.Branch);
        var workingCopy = Path.Combine(factory.WorkspacesRoot, project.Id.ToString("D"));
        Assert.Equal(_head, await TestGit.RunAsync(workingCopy, Ct, "rev-parse", prepared.Value.Branch));
        var version = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.Id == project.ReleaseVersionId, Ct));
        Assert.Equal(prepared.Value.Branch, version.BranchName);
    }

    [Fact]
    public async Task FR73_Never_released_builds_on_the_default_branch_head_without_an_imported_version()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);

        var project = await ProjectApi.ReadAsync<ProjectDto>(await ImportAsync(owner.Client, null, "0.1.0"), HttpStatusCode.Created);

        Assert.Equal(((string?)null, "0.1.0"), (project.ReleasedVersion, project.DevelopmentVersion));
        var version = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == project.Id, Ct));
        Assert.Equal((ReleaseVersionStatus.InDevelopment, (string?)null, _head), (version.Status, version.BaseTag, version.BaseCommitSha));
        Assert.Contains(await LifecycleSeed.JobsAsync(app, project.Id), j => j.Type == JobTypes.RepositoryAnalysis);
    }

    [Theory]
    [InlineData("v9.9.9", "10.0.0", HttpStatusCode.BadRequest, ErrorCodes.TagNotFound)]
    [InlineData("v2.2.0", "2.3.0", HttpStatusCode.Conflict, ErrorCodes.TagExists)]
    public async Task FR73_A_released_tag_missing_from_the_repo_is_TAG_NOT_FOUND_and_an_existing_development_tag_TAG_EXISTS(
        string releasedTag, string developmentVersion, HttpStatusCode status, string code)
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var before = Directory.Exists(factory.WorkspacesRoot) ? Directory.GetDirectories(factory.WorkspacesRoot).Length : 0;

        await Problems.AssertAsync(await ImportAsync(owner.Client, releasedTag, developmentVersion), status, code);

        Assert.False(await ProjectApi.WithDbAsync(app, db => db.ProjectMembers.AnyAsync(m => m.UserId == owner.User!.Id, Ct)));
        Assert.Equal(before, Directory.Exists(factory.WorkspacesRoot) ? Directory.GetDirectories(factory.WorkspacesRoot).Length : 0); // the copy is removed
    }

    [Fact]
    public async Task FR73_Local_import_is_VALIDATION_without_an_import_root_outside_it_or_for_a_directory_that_isnt_a_repository()
    {
        using (var owner = await ApiSession.RegisterAsync(factory))
        {
            var disabled = await Problems.AssertAsync(await ImportAsync(owner.Client, "v2.3.0", "2.4.0"), HttpStatusCode.BadRequest, ErrorCodes.Validation);
            Assert.True(disabled.GetProperty("errors").TryGetProperty("git.localSourcePath", out _));
        }

        await using var app = App();
        using var user = await ApiSession.RegisterAsync(app);
        Directory.CreateDirectory(Path.Combine(_importRoot, "plain"));
        await Problems.AssertAsync(await ImportAsync(user.Client, "v2.3.0", "2.4.0", source: "../outside"), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await ImportAsync(user.Client, "v2.3.0", "2.4.0", source: "plain"), HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    // ---------- FR-70, FR-72 ----------

    [Fact]
    public async Task FR70_Given_an_imported_repo_when_2_4_0_starts_then_a_locked_codebase_summary_v1_exists_before_the_first_refinement_round_and_the_round_lists_it()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await ImportAsync(owner.Client, "v2.3.0", "2.4.0"), HttpStatusCode.Created);
        var before = await ProjectApi.ReadAsync<RefinementDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/refinement", UriKind.Relative), Ct));
        Assert.Equal(CodebaseSummaryState.Queued, before.CodebaseSummary.Status);

        await AgentJobs.RunQueuedAsync(app, project.Id);

        var summary = Assert.Single(await AgentJobs.ArtifactsAsync(app, project.Id, ArtifactType.CodebaseSummary));
        Assert.Equal((1, ArtifactStatus.Locked, project.ReleaseVersionId), (summary.Version, summary.Status, (Guid?)summary.ReleaseVersionId));
        Assert.True(CodebaseSummaryRules.Check(summary.Content).IsSuccess);
        Assert.Empty(await LifecycleSeed.GatesAsync(app, project.Id)); // auto-locked: no approval gate
        Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.CodebaseSummaryCreated));
        var analyst = Assert.Single(await AgentJobs.RunsAsync(app, project.Id));
        Assert.Equal(AgentKind.RepositoryAnalyst, analyst.Agent);
        var analystCalls = Aide.Api.Features.Execution.ExecutionRunReader.ToolCalls(analyst.ToolCalls);
        Assert.Equal([RepoToolNames.ListFiles, RepoToolNames.ReadFile], analystCalls.Select(c => c.Tool)); // explored the snapshot at v2.3.0
        Assert.True(analystCalls[1].Bytes > 0);

        var round = await ProjectApi.ReadAsync<RefinementRoundDto>(
            await owner.Client.PostAsJsonAsync($"/api/projects/{project.Id}/refinement/run", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(app, project.Id);

        var refinement = Assert.Single(await AgentJobs.RunsAsync(app, project.Id), r => r.Id == round.RunId);
        Assert.Contains(new InputArtifactRef(summary.Id, ArtifactType.CodebaseSummary, 1), refinement.InputArtifacts);
        var after = await ProjectApi.ReadAsync<RefinementDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/refinement", UriKind.Relative), Ct));
        Assert.Equal(new CodebaseSummaryStatusDto(CodebaseSummaryState.Completed, analyst.Id, summary.Id, 1), after.CodebaseSummary);
        Assert.NotNull(after.LatestDraft);
    }

    [Fact]
    public async Task FR70_Refinement_waits_for_the_summary_then_after_ten_minutes_continues_without_it_and_records_summary_wait_timeout()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await ImportAsync(owner.Client, "v2.3.0", "2.4.0"), HttpStatusCode.Created);
        await owner.Client.PostAsJsonAsync($"/api/projects/{project.Id}/refinement/run", new { }, Ct);
        var refinementJob = Assert.Single(await LifecycleSeed.JobsAsync(app, project.Id), j => j.Type == JobTypes.IdeaRefinement);

        // The analyst hasn't run: the round waits (re-queued, attempt not counted) and nothing is called yet.
        Assert.True(await AgentJobs.RunCoreAsync(app, refinementJob));
        var waiting = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == refinementJob.Id, Ct));
        Assert.Equal((JobStatus.Queued, 0), (waiting.Status, waiting.Attempts));
        Assert.True(waiting.RunAfter > DateTimeOffset.UtcNow);
        Assert.Empty(await AgentJobs.RunsAsync(app, project.Id));
        var state = await ProjectApi.ReadAsync<RefinementDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/refinement", UriKind.Relative), Ct));
        Assert.Equal((AgentRunStatus.Queued, CodebaseSummaryState.Queued), (state.LatestRound!.Status, state.CodebaseSummary.Status));

        // Ten minutes after the version started, the round goes on without the summary.
        await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.Where(v => v.Id == project.ReleaseVersionId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.CreatedAt, v => v.CreatedAt.AddMinutes(-11)), Ct));
        Assert.False(await AgentJobs.RunCoreAsync(app, waiting));

        var run = Assert.Single(await AgentJobs.RunsAsync(app, project.Id));
        Assert.Equal((AgentKind.IdeaRefinement, AgentRunStatus.Succeeded), (run.Agent, run.Status));
        Assert.DoesNotContain(run.InputArtifacts, a => a.Type == ArtifactType.CodebaseSummary);
        Assert.Equal([AgentRunWarnings.SummaryWaitTimeout], AgentRunner.ReadOutput(run.Output)!.Warnings);
        var completed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.RunCompleted)).Payload);
        Assert.Equal(AgentRunWarnings.SummaryWaitTimeout, Assert.Single(completed.GetProperty("warnings").EnumerateArray()).GetString());
        var detail = await ProjectApi.ReadAsync<AgentRunDetailDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/runs/{run.Id}", UriKind.Relative), Ct));
        Assert.Equal([AgentRunWarnings.SummaryWaitTimeout], detail.Warnings);
        var after = await ProjectApi.ReadAsync<RefinementDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/refinement", UriKind.Relative), Ct));
        Assert.Equal(CodebaseSummaryState.TimedOut, after.CodebaseSummary.Status);
    }

    // ---------- FR-71 ----------

    [Fact]
    public async Task FR71_Given_a_refinement_run_on_an_imported_repo_when_the_agent_reads_etc_passwd_then_the_tool_returns_UNSAFE_PATH_and_nothing_is_read()
    {
        await using var app = App(new FakeToolScript(AgentKind.IdeaRefinement, FakeToolScripts.PathEscape));
        var run = await RefinementRunAsync(app);

        var call = Assert.Single(Aide.Api.Features.Execution.ExecutionRunReader.ToolCalls(run.ToolCalls));
        Assert.Equal((RepoToolNames.ReadFile, "UNSAFE_PATH", 0), (call.Tool, call.Error, call.Bytes));
        Assert.Equal("../../etc/passwd", call.Arguments.GetProperty("path").GetString());
        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
    }

    [Fact]
    public async Task FR71_Given_the_26th_tool_call_in_a_run_then_the_tool_returns_TOOL_LIMIT_and_the_agent_answers_with_what_it_has()
    {
        await using var app = App(new FakeToolScript(AgentKind.IdeaRefinement, FakeToolScripts.Many(30)));
        var run = await RefinementRunAsync(app);

        var calls = Aide.Api.Features.Execution.ExecutionRunReader.ToolCalls(run.ToolCalls);
        Assert.Equal(26, calls.Count);
        Assert.All(calls.Take(25), c => Assert.Null(c.Error));
        Assert.Equal("TOOL_LIMIT", calls[25].Error);
        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.NotEmpty(await AgentJobs.ArtifactsAsync(app, run.ProjectId, ArtifactType.ProjectRequest));
    }

    // ---------- FR-26 with tool loops ----------

    [Fact]
    public async Task FR26_A_tool_loop_stops_with_BUDGET_EXCEEDED_once_its_turns_use_up_the_budget_and_counts_the_turns_made()
    {
        await using var app = App(new FakeToolScript(AgentKind.IdeaRefinement, FakeToolScripts.Many(30)));
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await ImportAsync(owner.Client, "v2.3.0", "2.4.0"), HttpStatusCode.Created);
        await AgentJobs.RunQueuedAsync(app, project.Id);
        var before = (await LifecycleSeed.ProjectAsync(app, project.Id)).TokensUsed;
        await ProjectApi.WithDbAsync(app, db => db.Projects.Where(p => p.Id == project.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TokenBudget, before + 1), Ct));
        await owner.Client.PostAsJsonAsync($"/api/projects/{project.Id}/refinement/run", new { }, Ct);

        var job = Assert.Single(await AgentJobs.RunQueuedAsync(app, project.Id), j => j.Type == JobTypes.IdeaRefinement);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.StartsWith(ErrorCodes.BudgetExceeded, job.LastError, StringComparison.Ordinal);
        var run = Assert.Single(await AgentJobs.RunsAsync(app, project.Id), r => r.Agent == AgentKind.IdeaRefinement);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.StartsWith(ErrorCodes.BudgetExceeded, run.Error, StringComparison.Ordinal);
        Assert.True(run.InputTokens > 0 && run.OutputTokens > 0);
        Assert.Empty(Aide.Api.Features.Execution.ExecutionRunReader.ToolCalls(run.ToolCalls)); // stopped after the first turn, before its calls ran
        Assert.Equal(before + run.InputTokens + run.OutputTokens, (await LifecycleSeed.ProjectAsync(app, project.Id)).TokensUsed);
        Assert.Contains(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.BudgetExceeded), e => LifecycleSeed.Payload(e.Payload).GetProperty("runId").GetString() == run.Id.ToString());
    }

    [Fact]
    public async Task FR26_Turns_paid_before_a_transient_provider_failure_are_counted_and_the_retry_adds_up()
    {
        var flaky = new FlakyRefinementResponder();
        await using var app = App(new FakeToolScript(AgentKind.IdeaRefinement, FakeToolScripts.Explore));
        await using var flakyApp = app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IFakeAgentResponder>(flaky)));
        using var owner = await ApiSession.RegisterAsync(flakyApp);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await ImportAsync(owner.Client, "v2.3.0", "2.4.0"), HttpStatusCode.Created);
        await AgentJobs.RunQueuedAsync(flakyApp, project.Id);
        var before = (await LifecycleSeed.ProjectAsync(flakyApp, project.Id)).TokensUsed;
        await owner.Client.PostAsJsonAsync($"/api/projects/{project.Id}/refinement/run", new { }, Ct);
        var job = Assert.Single(await LifecycleSeed.JobsAsync(flakyApp, project.Id), j => j.Type == JobTypes.IdeaRefinement);

        // Attempt 1: two tool turns, then the final turn fails transiently: the run stays running, the turns are counted.
        await AgentJobs.RunCoreAsync(flakyApp, job);
        var failed = Assert.Single(await AgentJobs.RunsAsync(flakyApp, project.Id), r => r.Agent == AgentKind.IdeaRefinement);
        Assert.Equal(AgentRunStatus.Running, failed.Status);
        Assert.True(failed.InputTokens > 0);
        var afterFailure = (await LifecycleSeed.ProjectAsync(flakyApp, project.Id)).TokensUsed;
        Assert.Equal(before + failed.InputTokens + failed.OutputTokens, afterFailure);

        // Attempt 2 succeeds: the run holds both attempts' tokens, and so does the project.
        await ProjectApi.WithDbAsync(flakyApp, db => db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued), Ct));
        await AgentJobs.RunQueuedAsync(flakyApp, project.Id);
        var run = Assert.Single(await AgentJobs.RunsAsync(flakyApp, project.Id), r => r.Agent == AgentKind.IdeaRefinement);
        Assert.Equal((AgentRunStatus.Succeeded, 2), (run.Status, run.Attempts));
        Assert.True(run.InputTokens > 2 * failed.InputTokens - 1);
        Assert.Equal(before + run.InputTokens + run.OutputTokens, (await LifecycleSeed.ProjectAsync(flakyApp, project.Id)).TokensUsed);
    }

    /// <summary>The Fake refinement reply, except that the first final answer fails as a transient provider error.</summary>
    private sealed class FlakyRefinementResponder : IFakeAgentResponder
    {
        private readonly FakeIdeaRefinementResponder _inner = new();
        private int _calls;

        public AgentKind Agent => AgentKind.IdeaRefinement;

        public string Respond(LlmRequest request) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new LlmProviderException("Overloaded.", transient: true, statusCode: 529, errorCode: ErrorCodes.ProviderError)
                : _inner.Respond(request);
    }

    /// <summary>Imports the repository, runs the analyst, then one refinement round; returns the round's run.</summary>
    private static async Task<AgentRun> RefinementRunAsync(WebApplicationFactory<Program> app)
    {
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await ImportAsync(owner.Client, "v2.3.0", "2.4.0"), HttpStatusCode.Created);
        await AgentJobs.RunQueuedAsync(app, project.Id);
        var round = await ProjectApi.ReadAsync<RefinementRoundDto>(
            await owner.Client.PostAsJsonAsync($"/api/projects/{project.Id}/refinement/run", new { }, Ct), HttpStatusCode.Accepted);
        await AgentJobs.RunQueuedAsync(app, project.Id);
        return Assert.Single(await AgentJobs.RunsAsync(app, project.Id), r => r.Id == round.RunId);
    }
}
