using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Analysis;
using Aide.Api.Features.Artifacts;
using Aide.Api.Features.Projects;
using Aide.Api.Features.Stack;
using Aide.Api.Features.Versions;
using Aide.Domain.Access;
using Aide.Domain.Agents;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.Domain.Stack;
using Aide.Infrastructure.Agents;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Analysis;

/// <summary>
/// FR-178–FR-185 (§4.27): a Local repository with a Vue frontend in <c>frontend/</c> and Supabase migrations is imported
/// with "Infer from the repository", under <c>Git:Local:ImportRoot</c>, with the Fake provider (its Stack Analyst echoes
/// the detector, or follows a <c>[fake-stack:…]</c> marker in the idea).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class StackInferenceTests(AideApiFactory factory) : IAsyncLifetime
{
    private readonly string _importRoot = Path.Combine(Path.GetTempPath(), "aide-tests-infer", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var repo = Path.Combine(_importRoot, "vue-supabase");
        Directory.CreateDirectory(Path.Combine(repo, "frontend", "src"));
        Directory.CreateDirectory(Path.Combine(repo, "supabase", "migrations"));
        await TestGit.RunAsync(repo, Ct, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# Notes\n\nA Vue app on Supabase.\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "frontend", "package.json"), """{ "name": "notes", "dependencies": { "vue": "^3.5.13" }, "devDependencies": { "vite": "^7.0.0" } }""", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "frontend", "index.html"), "<div id=\"app\"></div>\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "frontend", "src", "main.ts"), "import { createApp } from 'vue';\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "supabase", "config.toml"), "project_id = \"notes\"\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "supabase", "migrations", "20260101000000_init.sql"), "create table notes (id uuid primary key);\n", Ct);
        await TestGit.RunAsync(repo, Ct, "add", ".");
        await TestGit.RunAsync(repo, Ct, "commit", "-q", "-m", "First");

        var empty = Path.Combine(_importRoot, "empty");
        Directory.CreateDirectory(empty);
        await TestGit.RunAsync(empty, Ct, "init", "-q", "-b", "main");
    }

    public ValueTask DisposeAsync()
    {
        TestGit.DeleteTree(_importRoot);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> App() => factory.WithWebHostBuilder(b => b.UseSetting("Git:Local:ImportRoot", _importRoot));

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string idea = "Add sharing of notes.", object? stack = null, string source = "vue-supabase", string? stackSource = "infer") =>
        client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name = ProjectApi.UniqueName("infer"),
                idea,
                source = "import",
                git = new { mode = "local", localSourcePath = source },
                import = new { releasedTag = (string?)null, developmentVersion = "0.1.0" },
                stack,
                stackSource,
            },
            Ct);

    private static async Task<ProjectDto> InferredAsync(WebApplicationFactory<Program> app, ApiSession owner, string idea = "Add sharing of notes.")
    {
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(owner.Client, idea), HttpStatusCode.Created);
        await AgentJobs.RunQueuedAsync(app, project.Id);
        return project;
    }

    private static async Task<AnalysisDto> AnalysisAsync(HttpClient client, Guid projectId) =>
        await ProjectApi.ReadAsync<AnalysisDto>(await client.GetAsync(new Uri($"/api/projects/{projectId}/analysis", UriKind.Relative), Ct));

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Guid projectId, Guid artifactId, object? body = null) =>
        client.PostAsJsonAsync($"/api/projects/{projectId}/artifacts/{artifactId}/approve", body ?? new { }, Ct);

    private static Task<HttpResponseMessage> SaveProfileAsync(HttpClient client, Guid projectId, StackProfileDocument document, int? baseVersion = null) =>
        client.PostAsJsonAsync($"/api/projects/{projectId}/artifacts/stack_profile", new { content = StackProfile.Render("Edited.", document), baseVersion }, Ct);

    private static async Task<StackProfileDocument> LatestDocumentAsync(WebApplicationFactory<Program> app, Guid projectId)
    {
        var latest = (await AgentJobs.ArtifactsAsync(app, projectId, ArtifactType.StackProfile)).Last();
        return StackProfile.Parse(latest.Content).Value.Document;
    }

    // ---------------------------------------------------------------- FR-178

    [Fact]
    public async Task FR178_Given_Infer_when_the_project_is_created_then_the_version_infers_its_stack_in_analysis_with_no_composition()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);

        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(owner.Client), HttpStatusCode.Created);

        Assert.Equal(Phase.Analysis, project.CurrentPhase);
        var version = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.Id == project.ReleaseVersionId, Ct));
        Assert.Equal((StackSource.Infer, (string?)null), (version.StackSource, version.StackComposition));
        Assert.Equal(JobTypes.RepositoryAnalysis, Assert.Single(await LifecycleSeed.JobsAsync(app, project.Id)).Type);
        var stack = await ProjectApi.ReadAsync<ProjectStackDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/stack", UriKind.Relative), Ct));
        Assert.Null(stack.Composition);
    }

    [Fact]
    public async Task FR178_A_stack_sent_alongside_Infer_is_VALIDATION_and_creates_nothing()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);

        var body = await Problems.AssertAsync(
            await CreateAsync(owner.Client, stack: new { frontend = new { pack = "fe-vue" } }),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);

        Assert.True(body.GetProperty("errors").TryGetProperty("stackSource", out _));
        Assert.False(await ProjectApi.WithDbAsync(app, db => db.ProjectMembers.AnyAsync(m => m.UserId == owner.User!.Id, Ct)));
    }

    [Fact]
    public async Task FR178_Infer_on_a_repository_with_no_code_is_STACK_INFER_NO_CODE()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);

        var response = await owner.Client.PostAsJsonAsync(
            "/api/projects",
            new { name = ProjectApi.UniqueName("infer"), idea = "Build notes.", git = new { provider = "local" }, stackSource = "infer" },
            Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.StackInferNoCode);
    }

    [Fact]
    public async Task FR178_D_V5_2_Without_Infer_an_import_starts_in_refinement_as_before()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);

        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(owner.Client, stackSource: null), HttpStatusCode.Created);
        await AgentJobs.RunQueuedAsync(app, project.Id);

        Assert.Equal(Phase.Refinement, project.CurrentPhase);
        Assert.DoesNotContain(await LifecycleSeed.JobsAsync(app, project.Id), j => j.Type == JobTypes.StackAnalysis);
        var analysis = await AnalysisAsync(owner.Client, project.Id);
        Assert.Equal((StackSource.Manual, false, (StackProfileViewDto?)null), (analysis.StackSource, analysis.CanApprove, analysis.Profile));
        Assert.Equal(Aide.Domain.Artifacts.CodebaseSummaryState.Completed, analysis.Summary.Status);
    }

    // ---------------------------------------------------------------- FR-180, FR-182

    [Fact]
    public async Task FR180_FR182_After_the_Repository_Analyst_the_Stack_Analyst_drafts_fe_vue_Backend_none_and_Supabase_BaaS()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner);

        var jobs = await LifecycleSeed.JobsAsync(app, project.Id);
        Assert.Equal([JobTypes.RepositoryAnalysis, JobTypes.StackAnalysis], jobs.Where(j => j.Type is JobTypes.RepositoryAnalysis or JobTypes.StackAnalysis).Select(j => j.Type));
        var run = Assert.Single(await AgentJobs.RunsAsync(app, project.Id), r => r.Agent == AgentKind.StackAnalyst);
        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.Contains(run.InputArtifacts, a => a.Type == ArtifactType.CodebaseSummary);

        var analysis = await AnalysisAsync(owner.Client, project.Id);
        Assert.Equal((Phase.Analysis, StackSource.Infer, true), (analysis.Phase, analysis.StackSource, analysis.HasExistingCode));
        Assert.Equal(StackAnalysisRunState.Succeeded, analysis.Run!.Status);
        var document = analysis.Profile!.Document!;
        Assert.Equal((ArtifactStatus.Draft, true), (analysis.Profile.Status, analysis.Profile.AuthoredByAgent));
        Assert.Equal(("fe-vue", "frontend"), (document.Layers.Frontend.Option, document.Layers.Frontend.Path));
        Assert.NotEmpty(document.Layers.Frontend.Evidence);
        Assert.Equal(StackProfileLayerStatus.None, document.Layers.Backend.Status);
        Assert.Equal(("data-supabase", "baas"), (document.Layers.Data.Option, document.Layers.Data.Mode));
        Assert.Equal(StackProfileLayerStatus.Fixed, document.Layers.Vcs.Status);
        Assert.Equal("supabase", document.Hosts.DatabaseHost);
        Assert.Empty(analysis.NeedsInput);
        Assert.True(analysis.CanApprove);
        Assert.False(analysis.CanContinue);
        Assert.NotNull(analysis.Suggested);
        Assert.Equal("fe-vue", analysis.Selection!.Frontend!.Pack);

        var created = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.StackProfileCreated)).Payload);
        Assert.Equal(analysis.Profile.ArtifactId.ToString(), created.GetProperty("artifactId").GetString());
        Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.StackDetected));
    }

    [Fact]
    public async Task FR180_A_reply_naming_be_django_is_retried_once_with_the_allowed_options_then_marked_unsupported()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner, $"Add sharing. {FakeStackAnalystResponder.DjangoMarker}");

        var runs = (await AgentJobs.RunsAsync(app, project.Id)).Where(r => r.Agent == AgentKind.StackAnalyst).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Contains("be-express", runs[1].UserFeedback, StringComparison.Ordinal);
        var analysis = await AnalysisAsync(owner.Client, project.Id);
        var blank = Assert.Single(analysis.NeedsInput);
        Assert.Equal(("backend", StackProfileLayerStatus.Unsupported, "Django"), (blank.Layer, blank.Status, blank.Technology));
    }

    [Fact]
    public async Task FR180_Two_invalid_replies_fail_the_run_with_STACK_PROFILE_INVALID_and_Retry_requeues_it()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner, $"Add sharing. {FakeStackAnalystResponder.InvalidMarker}");

        var analysis = await AnalysisAsync(owner.Client, project.Id);
        Assert.Equal((StackAnalysisRunState.Failed, ErrorCodes.StackProfileInvalid), (analysis.Run!.Status, analysis.Run.ErrorCode));
        Assert.Null(analysis.Profile);
        Assert.Equal("fe-vue", analysis.Suggested!.Layers.Frontend.Option); // "Fill in manually" starts from the detector

        var queued = await ProjectApi.ReadAsync<StackAnalysisQueuedDto>(
            await owner.Client.PostAsync(new Uri($"/api/projects/{project.Id}/analysis/run", UriKind.Relative), null, Ct), HttpStatusCode.Accepted);
        Assert.Contains(await LifecycleSeed.JobsAsync(app, project.Id), j => j.Id == queued.JobId && j.Type == JobTypes.StackAnalysis);
        await Problems.AssertAsync(
            await owner.Client.PostAsync(new Uri($"/api/projects/{project.Id}/analysis/run", UriKind.Relative), null, Ct),
            HttpStatusCode.Conflict,
            ErrorCodes.InvalidState);
    }

    [Fact]
    public async Task FR182_Analysis_is_for_members_and_running_it_for_writers()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner);
        using var viewer = await ApiSession.RegisterAsync(app);
        using var outsider = await ApiSession.RegisterAsync(app);
        using var anonymous = new ApiSession(app);
        await ProjectApi.AddMemberInDbAsync(app, project.Id, viewer.User!.Id, ProjectRole.Viewer);
        var path = new Uri($"/api/projects/{project.Id}/analysis", UriKind.Relative);
        var run = new Uri($"/api/projects/{project.Id}/analysis/run", UriKind.Relative);

        Assert.Equal(HttpStatusCode.OK, (await viewer.Client.GetAsync(path, Ct)).StatusCode);
        await Problems.AssertAsync(await viewer.Client.PostAsync(run, null, Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await outsider.Client.GetAsync(path, Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await anonymous.Client.GetAsync(path, Ct), HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        await Problems.AssertAsync(await owner.Client.GetAsync(new Uri($"/api/projects/{Guid.NewGuid()}/analysis", UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    // ---------------------------------------------------------------- FR-179, FR-181, FR-183, FR-185

    [Fact]
    public async Task FR179_Before_approval_the_project_stays_in_analysis_and_refinement_can_not_run()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner);

        var refused = await Problems.AssertAsync(await LifecycleSeed.AdvanceAsync(owner.Client, project.Id), HttpStatusCode.Conflict, ErrorCodes.PhasePreconditionFailed);
        Assert.Contains("stack profile is not approved", refused.GetProperty("title").GetString(), StringComparison.Ordinal);
        await Problems.AssertAsync(
            await owner.Client.PostAsJsonAsync($"/api/projects/{project.Id}/refinement/run", new { }, Ct),
            HttpStatusCode.Conflict,
            ErrorCodes.WrongPhase);
    }

    [Fact]
    public async Task FR183_FR185_Given_an_edited_host_when_the_Owner_approves_then_the_composition_is_pinned_and_a_Contributor_continues_to_refinement()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner);
        using var contributor = await ApiSession.RegisterAsync(app);
        await ProjectApi.AddMemberInDbAsync(app, project.Id, contributor.User!.Id, ProjectRole.Contributor);

        // FR-183: a Contributor edits the draft (a host); the edit is a new draft authored by them.
        var drafted = await LatestDocumentAsync(app, project.Id);
        var edited = await ProjectApi.ReadAsync<ArtifactDto>(
            await SaveProfileAsync(contributor.Client, project.Id, drafted with { Hosts = drafted.Hosts with { FrontendHost = "deploy-vercel" } }, baseVersion: 1),
            HttpStatusCode.Created);
        Assert.Equal((2, ArtifactStatus.Draft), (edited.Version, edited.Status));

        await Problems.AssertAsync(await ApproveAsync(contributor.Client, project.Id, edited.Id), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        var approved = await ProjectApi.ReadAsync<ArtifactDto>(await ApproveAsync(owner.Client, project.Id, edited.Id));
        Assert.Equal(ArtifactStatus.Locked, approved.Status);

        var stack = await ProjectApi.ReadAsync<ProjectStackDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/stack", UriKind.Relative), Ct));
        Assert.Equal(("fe-vue", "deploy-vercel"), (stack.Composition!.Frontend!.Pack, stack.Composition.Deploy.FrontendHost));
        Assert.NotNull(stack.CatalogHash);
        var approvedEvent = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.StackProfileApproved)).Payload);
        Assert.False(approvedEvent.GetProperty("generalist").GetBoolean());
        Assert.Contains("fe-vue", (await LifecycleSeed.ProjectAsync(app, project.Id)).StackPacks);
        Assert.True((await AnalysisAsync(owner.Client, project.Id)).CanContinue);

        var advanced = await LifecycleSeed.AdvanceAsync(contributor.Client, project.Id);
        Assert.Equal(HttpStatusCode.OK, advanced.StatusCode);
        Assert.Equal(Phase.Refinement, (await LifecycleSeed.ProjectAsync(app, project.Id)).CurrentPhase);

        // The locked profile isn't rewritten and can't be drafted outside analysis.
        await Problems.AssertAsync(await SaveProfileAsync(owner.Client, project.Id, drafted), HttpStatusCode.Conflict, ErrorCodes.WrongPhase);
    }

    [Fact]
    public async Task FR183_An_edit_naming_an_unknown_option_or_a_missing_folder_is_VALIDATION_with_field_errors_and_a_stale_base_is_CONFLICT()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner);
        var drafted = await LatestDocumentAsync(app, project.Id);

        var body = await Problems.AssertAsync(
            await SaveProfileAsync(owner.Client, project.Id, drafted with
            {
                Layers = drafted.Layers with
                {
                    Backend = new StackProfileLayer { Option = "be-django", Path = "backend", Status = StackProfileLayerStatus.User },
                    Frontend = drafted.Layers.Frontend with { Path = "web" },
                },
            }),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);
        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("layers.backend.option", out _));
        Assert.True(errors.TryGetProperty("layers.frontend.path", out _));

        await Problems.AssertAsync(await SaveProfileAsync(owner.Client, project.Id, drafted, baseVersion: 7), HttpStatusCode.Conflict, ErrorCodes.Conflict);
    }

    [Fact]
    public async Task FR183_Prisma_with_ASPNET_forced_through_the_API_is_STACK_INCOMPATIBLE()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner);
        var drafted = await LatestDocumentAsync(app, project.Id);

        var edited = await ProjectApi.ReadAsync<ArtifactDto>(
            await SaveProfileAsync(owner.Client, project.Id, drafted with
            {
                Layers = drafted.Layers with
                {
                    Backend = new StackProfileLayer { Option = "be-aspnet", Path = "supabase", Status = StackProfileLayerStatus.User },
                    Data = new StackProfileLayer { Option = "data-prisma", Status = StackProfileLayerStatus.User },
                },
                Hosts = StackProfileHosts.Empty,
            }),
            HttpStatusCode.Created);

        var problem = await Problems.AssertAsync(await ApproveAsync(owner.Client, project.Id, edited.Id), HttpStatusCode.BadRequest, ErrorCodes.StackIncompatible);
        Assert.Contains(problem.GetProperty("violations").EnumerateArray(), v => v.GetProperty("rule").GetString() == "R3");
        Assert.Equal(ArtifactStatus.Draft, (await AgentJobs.ArtifactsAsync(app, project.Id, ArtifactType.StackProfile)).Last().Status);
    }

    [Fact]
    public async Task FR181_Given_Backend_unknown_Approve_is_refused_with_STACK_PROFILE_INCOMPLETE_naming_backend()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner, $"Add sharing. {FakeStackAnalystResponder.UnknownMarker}");

        var analysis = await AnalysisAsync(owner.Client, project.Id);
        Assert.False(analysis.CanApprove);
        Assert.Contains("backend", analysis.ApproveBlockedReason, StringComparison.Ordinal);
        var blank = Assert.Single(analysis.NeedsInput);
        Assert.Equal(("backend", StackProfileLayerStatus.Unknown), (blank.Layer, blank.Status));
        Assert.False(string.IsNullOrWhiteSpace(blank.Question));

        var problem = await Problems.AssertAsync(await ApproveAsync(owner.Client, project.Id, analysis.Profile!.ArtifactId), HttpStatusCode.Conflict, ErrorCodes.StackProfileIncomplete);
        Assert.Equal(["backend"], problem.GetProperty("details").EnumerateArray().Select(d => d.GetString()));
    }

    [Fact]
    public async Task FR181_Given_a_Django_backend_when_the_Owner_continues_with_No_stack_then_the_profile_is_locked_with_generalist_and_refinement_follows()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner, $"Add sharing. {FakeStackAnalystResponder.UnsupportedMarker}");
        var draft = (await AnalysisAsync(owner.Client, project.Id)).Profile!;

        await Problems.AssertAsync(await ApproveAsync(owner.Client, project.Id, draft.ArtifactId), HttpStatusCode.Conflict, ErrorCodes.StackProfileIncomplete);
        var approved = await ProjectApi.ReadAsync<ArtifactDto>(await ApproveAsync(owner.Client, project.Id, draft.ArtifactId, new { generalist = true }));

        Assert.Equal((ArtifactStatus.Locked, draft.Version + 1), (approved.Status, approved.Version));
        Assert.True(StackProfile.Parse(approved.Content).Value.Document.Generalist);
        var versions = await AgentJobs.ArtifactsAsync(app, project.Id, ArtifactType.StackProfile);
        Assert.Equal(ArtifactStatus.Superseded, versions.Single(v => v.Id == draft.ArtifactId).Status);
        Assert.Null((await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.Id == project.ReleaseVersionId, Ct))).StackComposition);

        Assert.Equal(HttpStatusCode.OK, (await LifecycleSeed.AdvanceAsync(owner.Client, project.Id)).StatusCode);
        Assert.Equal(Phase.Refinement, (await LifecycleSeed.ProjectAsync(app, project.Id)).CurrentPhase);
    }

    [Fact]
    public async Task FR183_Rejecting_an_agent_draft_re_runs_the_Stack_Analyst_with_the_comment()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner);
        var draft = (await AnalysisAsync(owner.Client, project.Id)).Profile!;

        var rejected = await ProjectApi.ReadAsync<ArtifactDto>(await owner.Client.PostAsJsonAsync($"/api/projects/{project.Id}/artifacts/{draft.ArtifactId}/reject", new { comment = "The frontend also has an admin app." }, Ct));
        Assert.Equal(ArtifactStatus.Rejected, rejected.Status);
        var rejectedEvent = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.StackProfileRejected)).Payload);
        Assert.True(rejectedEvent.GetProperty("rerun").GetBoolean());

        await AgentJobs.RunQueuedAsync(app, project.Id);

        var runs = (await AgentJobs.RunsAsync(app, project.Id)).Where(r => r.Agent == AgentKind.StackAnalyst).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal("The frontend also has an admin app.", runs[1].UserFeedback);
        var latest = (await AnalysisAsync(owner.Client, project.Id)).Profile!;
        Assert.Equal((draft.Version + 1, ArtifactStatus.Draft), (latest.Version, latest.Status));
        Assert.Contains("Revised after review feedback", latest.Narrative, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR180_Running_the_analysis_outside_analysis_is_WRONG_PHASE()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.ReadAsync<ProjectDto>(await CreateAsync(owner.Client, stackSource: null), HttpStatusCode.Created);

        await Problems.AssertAsync(
            await owner.Client.PostAsync(new Uri($"/api/projects/{project.Id}/analysis/run", UriKind.Relative), null, Ct),
            HttpStatusCode.Conflict,
            ErrorCodes.WrongPhase);
    }

    [Fact]
    public async Task FR183_Rejecting_a_human_edited_profile_re_runs_the_Stack_Analyst_with_the_comment_and_the_rejected_draft()
    {
        var capture = new Stack.CompositionAgentTests.CapturingResponder(new FakeStackAnalystResponder());
        await using var app = AgentJobs.WithResponders(App(), capture);
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner);
        var drafted = await LatestDocumentAsync(app, project.Id);
        var edited = await ProjectApi.ReadAsync<ArtifactDto>(
            await SaveProfileAsync(owner.Client, project.Id, drafted with { Hosts = drafted.Hosts with { FrontendHost = "deploy-netlify" } }, baseVersion: 1),
            HttpStatusCode.Created);

        var rejected = await ProjectApi.ReadAsync<ArtifactDto>(await owner.Client.PostAsJsonAsync($"/api/projects/{project.Id}/artifacts/{edited.Id}/reject", new { comment = "Keep Netlify, but the data layer is Prisma." }, Ct));

        Assert.Equal(ArtifactStatus.Rejected, rejected.Status);
        Assert.True(LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.StackProfileRejected)).Payload).GetProperty("rerun").GetBoolean());
        await AgentJobs.RunQueuedAsync(app, project.Id);

        var rerun = (await AgentJobs.RunsAsync(app, project.Id)).Where(r => r.Agent == AgentKind.StackAnalyst).Last();
        Assert.Equal("Keep Netlify, but the data layer is Prisma.", rerun.UserFeedback);
        Assert.Contains(rerun.InputArtifacts, a => a.ArtifactId == edited.Id && a.Type == ArtifactType.StackProfile);
        var prompt = string.Join('\n', capture.Requests[^1].Messages.Select(m => m.Content));
        Assert.Contains("deploy-netlify", prompt[prompt.IndexOf("<previous_profile>", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("keep every correction that agrees with the", capture.Requests[^1].System, StringComparison.Ordinal);
        var latest = (await AnalysisAsync(owner.Client, project.Id)).Profile!;
        Assert.Equal((3, ArtifactStatus.Draft), (latest.Version, latest.Status));
    }

    // ---------------------------------------------------------------- FR-186

    /// <summary>Approves the version's agent draft (fe-vue), then marks the version released on its base commit.</summary>
    private static async Task<string> ReleaseInferredVersionAsync(WebApplicationFactory<Program> app, ApiSession owner, ProjectDto project)
    {
        var draft = (await AnalysisAsync(owner.Client, project.Id)).Profile!;
        await ProjectApi.ReadAsync<ArtifactDto>(await ApproveAsync(owner.Client, project.Id, draft.ArtifactId));
        return await ProjectApi.WithDbAsync(app, async db =>
        {
            var version = await db.ReleaseVersions.SingleAsync(v => v.ProjectId == project.Id, Ct);
            version.Status = ReleaseVersionStatus.Released;
            version.Tag = "v" + version.Version;
            version.MergeCommitSha = version.BaseCommitSha;
            version.ReleasedAt = DateTimeOffset.UtcNow;
            var row = await db.Projects.SingleAsync(p => p.Id == project.Id, Ct);
            row.CurrentPhase = Phase.Release;
            row.Status = ProjectStatus.Released;
            row.Checkpoint = 6;
            row.ReleasedVersion = version.Version;
            row.DevelopmentVersion = null;
            await db.SaveChangesAsync(Ct);
            return version.MergeCommitSha!;
        });
    }

    private static Task<HttpResponseMessage> StartVersionAsync(HttpClient client, Guid projectId, object? stack = null) =>
        client.PostAsJsonAsync($"/api/projects/{projectId}/versions", new { version = "0.2.0", request = "Move the frontend to React.", stackSource = "infer", stack }, Ct);

    [Fact]
    public async Task FR186_A_new_version_that_infers_its_stack_enters_analysis_at_the_new_base_commit_with_the_previous_composition()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner);
        var baseCommit = await ReleaseInferredVersionAsync(app, owner, project);

        await Problems.AssertAsync(await StartVersionAsync(owner.Client, project.Id, stack: new { frontend = new { pack = "fe-vue" } }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var started = await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(await StartVersionAsync(owner.Client, project.Id), HttpStatusCode.Created);

        Assert.Equal((Phase.Analysis, ProjectStatus.Active, 1), (started.State.Phase, started.State.Status, started.State.Checkpoint));
        Assert.Equal((StackSource.Infer, baseCommit), (started.ReleaseVersion.StackSource, started.ReleaseVersion.BaseCommitSha));
        var version = await ProjectApi.WithDbAsync(app, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.Id == started.ReleaseVersion.Id, Ct));
        Assert.Null(version.StackComposition);
        Assert.Empty((await LifecycleSeed.ProjectAsync(app, project.Id)).StackPacks);

        await AgentJobs.RunQueuedAsync(app, project.Id);

        var analysis = await AnalysisAsync(owner.Client, project.Id);
        Assert.Equal(started.ReleaseVersion.Id, analysis.ReleaseVersionId);
        Assert.Equal("fe-vue", analysis.PreviousComposition!.Frontend!.Pack);
        Assert.Null(analysis.Composition);
        Assert.Equal((ArtifactStatus.Draft, baseCommit), (analysis.Profile!.Status, analysis.Profile.Document!.BaseCommitSha));
    }

    [Fact]
    public async Task FR186_FR117_Approving_different_layers_in_a_new_version_records_STACK_CHANGED_and_needs_Stack_Migration()
    {
        await using var app = App();
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await InferredAsync(app, owner);
        await ReleaseInferredVersionAsync(app, owner, project);
        await ProjectApi.ReadAsync<StartReleaseVersionResultDto>(await StartVersionAsync(owner.Client, project.Id), HttpStatusCode.Created);
        await AgentJobs.RunQueuedAsync(app, project.Id);
        var analysis = await AnalysisAsync(owner.Client, project.Id);
        var drafted = await LatestDocumentAsync(app, project.Id);

        // The user moves the frontend from Vue to React (the code was migrated since 0.1.0).
        var react = drafted with { Layers = drafted.Layers with { Frontend = drafted.Layers.Frontend with { Option = "fe-react-vite", Status = StackProfileLayerStatus.User } } };
        var edited = await ProjectApi.ReadAsync<ArtifactDto>(await SaveProfileAsync(owner.Client, project.Id, react, analysis.Profile!.Version), HttpStatusCode.Created);
        await ProjectApi.ReadAsync<ArtifactDto>(await ApproveAsync(owner.Client, project.Id, edited.Id));

        var changed = LifecycleSeed.Payload(Assert.Single(await LifecycleSeed.EventsAsync(app, project.Id, EventTypes.StackChanged)).Payload);
        Assert.Contains("frontend", changed.GetProperty("layersChanged").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("fe-vue", changed.GetProperty("from").GetProperty("frontend").GetProperty("pack").GetString());
        var stack = await ProjectApi.ReadAsync<ProjectStackDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/stack", UriKind.Relative), Ct));
        Assert.Equal("fe-react-vite", stack.Composition!.Frontend!.Pack);
        Assert.True(stack.StackChange.IsStackChange);
        Assert.Contains("frontend", stack.StackChange.LayersChanged);

        // FR-117, FR-123: the spec of a stack change must have "Stack Migration".
        await using var scope = app.Services.CreateAsyncScope();
        var context = await scope.ServiceProvider.GetRequiredService<VersionStackLoader>().CurrentAsync(project.Id, Ct, readBase: false);
        Assert.Contains(StackCoreSpecSections.StackMigrationTitle, context.Stack!.SpecSectionTitles);
    }
}
