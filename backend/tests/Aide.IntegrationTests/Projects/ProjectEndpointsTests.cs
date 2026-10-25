using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Gates;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Security;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aide.IntegrationTests.Projects;

/// <summary>FR-5 (create), FR-6 (list), FR-7 (edit / delete) against PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProjectEndpointsTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ FR-5

    [Fact]
    public async Task FR5_Create_starts_in_refinement_active_v1_with_the_creator_as_Owner_and_records_PROJECT_CREATED()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var response = await owner.Client.PostAsJsonAsync(
            "/api/projects",
            new { name = "My Project", idea = "A todo API for teams.", git = new { provider = "local" } },
            Ct);
        var project = await ProjectApi.ReadAsync<ProjectDto>(response, HttpStatusCode.Created);

        Assert.Equal($"/api/projects/{project.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(Phase.Refinement, project.CurrentPhase);
        Assert.Equal(ProjectStatus.Active, project.Status);
        Assert.Equal(1, project.Checkpoint);
        Assert.Equal(ProjectRole.Owner, project.MyRole);
        Assert.Equal("A todo API for teams.", project.Idea);
        Assert.Matches("^my-project-[0-9a-f]{6}$", project.Slug);
        Assert.EndsWith(project.Id.ToString("N")[^6..], project.Slug, StringComparison.Ordinal);

        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var membership = await db.ProjectMembers.SingleAsync(m => m.ProjectId == project.Id, Ct);
            Assert.Equal((owner.User!.Id, ProjectRole.Owner), (membership.UserId, membership.Role));

            var transition = await db.StateTransitions.SingleAsync(t => t.ProjectId == project.Id, Ct);
            Assert.Null(transition.FromPhase);
            Assert.Equal(Phase.Refinement, transition.ToPhase);
            Assert.Equal(1, transition.Checkpoint);
            Assert.Equal(owner.User.Id.ToString(), transition.Actor);
            Assert.Equal("project created", transition.Reason);

            var created = await db.Events.SingleAsync(e => e.ProjectId == project.Id && e.Type == EventTypes.ProjectCreated, Ct);
            using var payload = JsonDocument.Parse(created.Payload);
            Assert.Equal(project.Id, payload.RootElement.GetProperty("projectId").GetGuid());
            Assert.Equal("My Project", payload.RootElement.GetProperty("name").GetString());
            Assert.Equal(owner.User.Id.ToString(), payload.RootElement.GetProperty("actor").GetString());
            Assert.Equal(owner.User.Id.ToString(), created.Actor);

            var git = await db.ProjectGitSettings.SingleAsync(s => s.ProjectId == project.Id, Ct);
            Assert.Equal((GitProvider.Local, "main", (string?)null), (git.Provider, git.DefaultBranch, git.TokenEncrypted));
            Assert.Empty((await db.ProjectNotificationSettings.SingleAsync(s => s.ProjectId == project.Id, Ct)).Channels);
            Assert.True(await db.ProjectLlmSettings.AnyAsync(s => s.ProjectId == project.Id, Ct));
        });
    }

    [Fact]
    public async Task FR5_Omitted_settings_take_the_defaults()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var project = await ProjectApi.CreateAsync(owner.Client);

        Assert.Equal(ApprovalMode.Advisory, project.ApprovalMode);
        Assert.Equal(0.90m, project.ComplianceThreshold);
        Assert.Equal(20, project.MaxFilesPerStep);
        Assert.Equal(3, project.MaxReviewCycles);
        Assert.Null(project.TokenBudget);
        Assert.Equal(0, project.TokensUsed);
        Assert.False(project.ExecutionPaused);
    }

    [Fact]
    public async Task FR5_Wizard_configuration_is_stored()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var response = await owner.Client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name = "Configured",
                idea = "Idea",
                approvalMode = "strict",
                complianceThreshold = 0.85,
                maxFilesPerStep = 5,
                maxReviewCycles = 4,
                tokenBudget = 50000,
                git = new { provider = "github_pat", repoUrl = "https://github.com/acme/todo.git", defaultBranch = "develop", token = "ghp_wizardtoken123" },
            },
            Ct);
        var project = await ProjectApi.ReadAsync<ProjectDto>(response, HttpStatusCode.Created);

        Assert.Equal((ApprovalMode.Strict, 0.85m, 5, 4, (long?)50000), (project.ApprovalMode, project.ComplianceThreshold, project.MaxFilesPerStep, project.MaxReviewCycles, project.TokenBudget));
        Assert.DoesNotContain("ghp_wizardtoken123", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var git = await db.ProjectGitSettings.SingleAsync(s => s.ProjectId == project.Id, Ct);
            Assert.Equal((GitProvider.GithubPat, "https://github.com/acme/todo.git", "develop"), (git.Provider, git.RepoUrl, git.DefaultBranch));
            Assert.NotNull(git.TokenEncrypted);
            Assert.DoesNotContain("ghp_wizardtoken123", git.TokenEncrypted, StringComparison.Ordinal);
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.Equal("ghp_wizardtoken123", scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(git.TokenEncrypted));
        });
    }

    [Theory]
    [InlineData("""{"name":"   ","idea":"Idea","git":{"provider":"local"}}""", "name")]
    [InlineData("""{"name":"N","idea":"","git":{"provider":"local"}}""", "idea")]
    [InlineData("""{"name":"N","idea":"Idea","git":{"provider":"github_pat"}}""", "git.repoUrl")]
    [InlineData("""{"name":"N","idea":"Idea","git":{"provider":"github_pat","repoUrl":"https://gitlab.com/a/b"}}""", "git.repoUrl")]
    [InlineData("""{"name":"N","idea":"Idea","git":{"provider":"local","defaultBranch":"bad..branch"}}""", "git.defaultBranch")]
    [InlineData("""{"name":"N","idea":"Idea","git":{"provider":"local","token":"  "}}""", "git.token")]
    [InlineData("""{"name":"N","idea":"Idea","complianceThreshold":0.905,"git":{"provider":"local"}}""", "complianceThreshold")]
    [InlineData("""{"name":"N","idea":"Idea","complianceThreshold":0.4,"git":{"provider":"local"}}""", null)]
    [InlineData("""{"name":"N","idea":"Idea","maxFilesPerStep":0,"git":{"provider":"local"}}""", null)]
    [InlineData("""{"name":"N","idea":"Idea","tokenBudget":10,"git":{"provider":"local"}}""", null)]
    [InlineData("""{"name":"N","idea":"Idea","approvalMode":"yolo","git":{"provider":"local"}}""", null)]
    [InlineData("""{"name":"N","idea":"Idea"}""", null)]
    public async Task FR5_Invalid_wizard_input_is_VALIDATION(string json, string? field)
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var response = await owner.Client.PostAsync(
            "/api/projects", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        if (field is not null)
        {
            Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), body.ToString());
        }
    }

    [Fact]
    public async Task FR5_Slug_is_kebab_case_ascii_with_an_id_suffix()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var project = await ProjectApi.CreateAsync(owner.Client, "  Héllo, Wörld!  API v2 ");

        Assert.Matches("^hello-world-api-v2-[0-9a-f]{6}$", project.Slug);
        Assert.Equal("Héllo, Wörld!  API v2", project.Name);
    }

    [Fact]
    public async Task FR159_Missing_LLM_key_defaults_new_projects_to_the_first_usable_enabled_provider()
    {
        // §4.24 table row 1 (amends FR-5): all four enabled, no key-free use of Anthropic/OpenAI, default anthropic → Ollama.
        await using var app = factory.WithWebHostBuilder(b => b.UseSetting("Llm:DefaultProvider", "anthropic"));
        using var owner = await ApiSession.RegisterAsync(app);

        var project = await ProjectApi.CreateAsync(owner.Client);

        var settings = await ProjectApi.ReadAsync<ProjectSettingsDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/settings", UriKind.Relative), Ct));
        Assert.Equal(LlmProvider.Ollama, settings.Llm.Provider);
        Assert.Equal("qwen2.5-coder", settings.Llm.DefaultModel);
        Assert.False(settings.Llm.Providers.Single(p => p.Provider == LlmProvider.Anthropic).Configured);
        Assert.True(settings.Llm.Providers.Single(p => p.Provider == LlmProvider.Ollama).Configured);
        Assert.Contains(factory.Logs.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information && e.Message.Contains("has no API key configured", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR159_CI_defaults_give_new_projects_the_Fake_provider()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var project = await ProjectApi.CreateAsync(owner.Client);

        var settings = await ProjectApi.ReadAsync<ProjectSettingsDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/settings", UriKind.Relative), Ct));
        Assert.Equal((LlmProvider.Fake, "fake-1"), (settings.Llm.Provider, settings.Llm.DefaultModel));
    }

    [Fact]
    public async Task FR5_A_system_key_makes_the_configured_default_provider_the_new_project_default()
    {
        // D-V2-5: a system key counts only when Llm:AllowSystemKeys is on (FR79_System_keys_are_used_only_when_allowed_and_nothing_is_bound).
        await using var app = factory.WithWebHostBuilder(b => b
            .UseSetting("Llm:DefaultProvider", "anthropic")
            .UseSetting("Llm:Providers:Anthropic:ApiKey", "sk-ant-system-test-key")
            .UseSetting("Llm:AllowSystemKeys", "true"));
        using var owner = await ApiSession.RegisterAsync(app);

        var project = await ProjectApi.CreateAsync(owner.Client);

        var settings = await ProjectApi.ReadAsync<ProjectSettingsDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}/settings", UriKind.Relative), Ct));
        Assert.Equal(LlmProvider.Anthropic, settings.Llm.Provider);
        Assert.Equal("claude-opus-5-5", settings.Llm.DefaultModel);
        Assert.Equal("claude-sonnet-5-5", settings.Llm.ModelsByAgent.IdeaRefinement);
        Assert.Null(settings.Llm.ModelsByAgent.SpecGenerator);
        Assert.False(settings.Llm.HasApiKey);
        Assert.True(settings.Llm.Providers.Single(p => p.Provider == LlmProvider.Anthropic).Configured);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.AllText.Contains("sk-ant-system-test-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR5_Anonymous_create_is_401()
    {
        using var anonymous = new ApiSession(factory);

        var response = await anonymous.Client.PostAsJsonAsync("/api/projects", new { name = "N", idea = "I", git = new { provider = "local" } }, Ct);

        await Problems.AssertAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    // ------------------------------------------------------------------ FR-6

    [Fact]
    public async Task FR6_ListShowsOnlyMemberProjects()
    {
        var prefix = ProjectApi.UniqueName("fr6");
        using var alice = await ApiSession.RegisterAsync(factory);
        using var bob = await ApiSession.RegisterAsync(factory);
        var mine = await ProjectApi.CreateAsync(alice.Client, $"{prefix} alice");
        await ProjectApi.CreateAsync(bob.Client, $"{prefix} bob");

        var page = await ListAsync(alice.Client, $"search={prefix}");

        var item = Assert.Single(page.Items);
        Assert.Equal(mine.Id, item.Id);
        Assert.Equal(ProjectRole.Owner, item.MyRole);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task FR6_Members_see_projects_shared_with_them_with_their_role()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var page = await ListAsync(fixture.Viewer.Client, $"search={fixture.Project.Name}");

        Assert.Equal(ProjectRole.Viewer, Assert.Single(page.Items).MyRole);
    }

    [Fact]
    public async Task FR6_Admins_see_every_project()
    {
        var prefix = ProjectApi.UniqueName("fr6adm");
        using var alice = await ApiSession.RegisterAsync(factory);
        using var bob = await ApiSession.RegisterAsync(factory);
        await ProjectApi.CreateAsync(alice.Client, $"{prefix} a");
        await ProjectApi.CreateAsync(bob.Client, $"{prefix} b");
        using var admin = await ApiSession.AdminAsync(factory);

        var page = await ListAsync(admin.Client, $"search={prefix}");

        Assert.Equal(2, page.Total);
        Assert.All(page.Items, i => Assert.Null(i.MyRole));
    }

    [Fact]
    public async Task FR6_Paging_sorting_and_search()
    {
        var prefix = ProjectApi.UniqueName("fr6pg");
        using var owner = await ApiSession.RegisterAsync(factory);
        foreach (var suffix in new[] { "charlie", "alpha", "bravo" })
        {
            await ProjectApi.CreateAsync(owner.Client, $"{prefix} {suffix}");
        }

        var first = await ListAsync(owner.Client, $"search={prefix}&sortBy=name&sortOrder=asc&limit=2&page=1");
        var second = await ListAsync(owner.Client, $"search={prefix}&sortBy=name&sortOrder=asc&limit=2&page=2");
        var newest = await ListAsync(owner.Client, $"search={prefix}&sortBy=created&sortOrder=desc");
        var searched = await ListAsync(owner.Client, $"search={prefix.ToUpperInvariant()}%20BRAVO");
        var beyond = await ListAsync(owner.Client, $"search={prefix}&page=99");

        Assert.Equal([$"{prefix} alpha", $"{prefix} bravo"], first.Items.Select(i => i.Name));
        Assert.Equal((3, 1, 2), (first.Total, first.Page, first.Limit));
        Assert.Equal([$"{prefix} charlie"], second.Items.Select(i => i.Name));
        Assert.Equal([$"{prefix} bravo", $"{prefix} alpha", $"{prefix} charlie"], newest.Items.Select(i => i.Name));
        Assert.Equal($"{prefix} bravo", Assert.Single(searched.Items).Name);
        Assert.Empty(beyond.Items);
        Assert.Equal(3, beyond.Total);
    }

    [Fact]
    public async Task FR6_Search_treats_wildcards_literally()
    {
        var prefix = ProjectApi.UniqueName("fr6wc");
        using var owner = await ApiSession.RegisterAsync(factory);
        await ProjectApi.CreateAsync(owner.Client, $"{prefix} 100% done");
        await ProjectApi.CreateAsync(owner.Client, $"{prefix} 100 done");

        var page = await ListAsync(owner.Client, $"search={Uri.EscapeDataString($"{prefix} 100%")}");

        Assert.Equal($"{prefix} 100% done", Assert.Single(page.Items).Name);
    }

    [Fact]
    public async Task FR6_Filters_by_phase_and_status()
    {
        var prefix = ProjectApi.UniqueName("fr6f");
        using var owner = await ApiSession.RegisterAsync(factory);
        var moved = await ProjectApi.CreateAsync(owner.Client, $"{prefix} moved");
        await ProjectApi.CreateAsync(owner.Client, $"{prefix} still");
        await ProjectApi.WithDbAsync(factory, db => db.Projects.Where(p => p.Id == moved.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CurrentPhase, Phase.Planning).SetProperty(p => p.Status, ProjectStatus.RolledBack), Ct));

        var planning = await ListAsync(owner.Client, $"search={prefix}&phase=planning");
        var rolledBack = await ListAsync(owner.Client, $"search={prefix}&status=rolled_back");
        var active = await ListAsync(owner.Client, $"search={prefix}&status=active&phase=refinement");

        Assert.Equal(moved.Id, Assert.Single(planning.Items).Id);
        Assert.Equal(ProjectStatus.RolledBack, Assert.Single(rolledBack.Items).Status);
        Assert.Equal($"{prefix} still", Assert.Single(active.Items).Name);
    }

    [Theory]
    [InlineData("phase=Planning")]
    [InlineData("status=rolledback")]
    [InlineData("sortBy=size")]
    [InlineData("sortOrder=up")]
    [InlineData("limit=101")]
    [InlineData("limit=0")]
    [InlineData("page=0")]
    public async Task FR6_Invalid_list_parameters_are_VALIDATION(string query)
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var response = await owner.Client.GetAsync(new Uri($"/api/projects?{query}", UriKind.Relative), Ct);

        await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    // ------------------------------------------------------------------ GET

    [Fact]
    public async Task FR7_Members_get_the_project_with_their_role_and_non_members_get_404()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        using var admin = await ApiSession.AdminAsync(factory);

        var asViewer = await ProjectApi.ReadAsync<ProjectDto>(await fixture.Viewer.Client.GetAsync(new Uri(fixture.Path, UriKind.Relative), Ct));
        var asAdmin = await ProjectApi.ReadAsync<ProjectDto>(await admin.Client.GetAsync(new Uri(fixture.Path, UriKind.Relative), Ct));
        var asOutsider = await fixture.Outsider.Client.GetAsync(new Uri(fixture.Path, UriKind.Relative), Ct);
        var missing = await fixture.Owner.Client.GetAsync(new Uri($"/api/projects/{Guid.NewGuid()}", UriKind.Relative), Ct);

        Assert.Equal(ProjectRole.Viewer, asViewer.MyRole);
        Assert.Null(asAdmin.MyRole);
        await Problems.AssertAsync(asOutsider, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(missing, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    // ------------------------------------------------------------------ FR-7 edit

    [Fact]
    public async Task FR7_Owner_edits_name_mode_threshold_and_limits_and_PROJECT_UPDATED_is_recorded()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var updated = await ProjectApi.ReadAsync<ProjectDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, fixture.Path, new
        {
            name = "Renamed project",
            approvalMode = "autonomous",
            complianceThreshold = 0.95,
            maxFilesPerStep = 10,
            maxReviewCycles = 5,
            tokenBudget = 200000,
        }));

        Assert.Equal(("Renamed project", ApprovalMode.Autonomous, 0.95m, 10, 5, (long?)200000),
            (updated.Name, updated.ApprovalMode, updated.ComplianceThreshold, updated.MaxFilesPerStep, updated.MaxReviewCycles, updated.TokenBudget));
        Assert.Equal(fixture.Project.Slug, updated.Slug);
        Assert.True(updated.UpdatedAt >= fixture.Project.UpdatedAt);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var evt = await db.Events.SingleAsync(e => e.ProjectId == fixture.Project.Id && e.Type == EventTypes.ProjectUpdated, Ct);
            Assert.Contains("approvalMode", evt.Payload, StringComparison.Ordinal);
            Assert.Equal(fixture.Owner.User!.Id.ToString(), evt.Actor);
        });

        var cleared = await ProjectApi.ReadAsync<ProjectDto>(await ProjectApi.PatchAsync(fixture.Owner.Client, fixture.Path, new { clearTokenBudget = true }));
        Assert.Null(cleared.TokenBudget);
        Assert.Equal("Renamed project", cleared.Name);
    }

    [Fact]
    public async Task FR7_ContributorPatch_403()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await ProjectApi.PatchAsync(fixture.Contributor.Client, fixture.Path, new { name = "Nope" });

        await Problems.AssertAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task FR7_Viewer_and_non_member_mutations_are_403_and_404()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Viewer.Client, fixture.Path, new { name = "Nope" }), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Viewer.Client.DeleteAsync(new Uri(fixture.Path, UriKind.Relative), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await fixture.Contributor.Client.DeleteAsync(new Uri(fixture.Path, UriKind.Relative), Ct), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await Problems.AssertAsync(await ProjectApi.PatchAsync(fixture.Outsider.Client, fixture.Path, new { name = "Nope" }), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await fixture.Outsider.Client.DeleteAsync(new Uri(fixture.Path, UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR7_Admins_may_edit_any_project()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        using var admin = await ApiSession.AdminAsync(factory);

        var updated = await ProjectApi.ReadAsync<ProjectDto>(await ProjectApi.PatchAsync(admin.Client, fixture.Path, new { maxReviewCycles = 2 }));

        Assert.Equal(2, updated.MaxReviewCycles);
        Assert.Null(updated.MyRole);
    }

    [Theory]
    [InlineData("""{"tokenBudget":5000,"clearTokenBudget":true}""", "tokenBudget")]
    [InlineData("""{"name":"  "}""", "name")]
    [InlineData("""{"complianceThreshold":0.999}""", "complianceThreshold")]
    [InlineData("""{"maxReviewCycles":11}""", null)]
    public async Task FR7_Invalid_edits_are_VALIDATION(string json, string? field)
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);

        var response = await fixture.Owner.Client.PatchAsync(
            new Uri(fixture.Path, UriKind.Relative), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        if (field is not null)
        {
            Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), body.ToString());
        }
    }

    [Fact]
    public async Task FR7_Concurrent_edit_is_CONFLICT()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client);
        var race = new ConcurrentProjectEdit(factory.ConnectionString, project.Id);
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.ConfigureDbContext<Aide.Infrastructure.Persistence.AideDbContext>(o => o.AddInterceptors(race))));
        using var session = await ApiSession.LoginAsync(app, owner.User!.Email);

        var response = await ProjectApi.PatchAsync(session.Client, $"/api/projects/{project.Id}", new { name = "Mine" });

        Assert.True(race.Fired);
        await Problems.AssertAsync(response, HttpStatusCode.Conflict, ErrorCodes.Conflict);
        var current = await ProjectApi.ReadAsync<ProjectDto>(await owner.Client.GetAsync(new Uri($"/api/projects/{project.Id}", UriKind.Relative), Ct));
        Assert.Equal("Theirs", current.Name);
    }

    // ------------------------------------------------------------------ FR-7 delete

    [Fact]
    public async Task FR7_Delete_cascades_to_everything_the_project_owns_and_removes_the_working_copy()
    {
        using var fixture = await ProjectApi.FixtureAsync(factory);
        var id = fixture.Project.Id;
        await ProjectStateTests.SeedWorkflowRowsAsync(factory, id);
        var workingCopy = Path.Combine(factory.WorkspacesRoot, id.ToString());
        Directory.CreateDirectory(Path.Combine(workingCopy, ".git", "objects"));
        await File.WriteAllTextAsync(Path.Combine(workingCopy, ".git", "objects", "obj"), "x", Ct);
        File.SetAttributes(Path.Combine(workingCopy, ".git", "objects", "obj"), FileAttributes.ReadOnly);

        var response = await fixture.Owner.Client.DeleteAsync(new Uri(fixture.Path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(Directory.Exists(workingCopy));
        await Problems.AssertAsync(await fixture.Owner.Client.GetAsync(new Uri(fixture.Path, UriKind.Relative), Ct), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            Assert.False(await db.ProjectMembers.AnyAsync(m => m.ProjectId == id, Ct));
            Assert.False(await db.ProjectGitSettings.AnyAsync(s => s.ProjectId == id, Ct));
            Assert.False(await db.ProjectLlmSettings.AnyAsync(s => s.ProjectId == id, Ct));
            Assert.False(await db.ProjectNotificationSettings.AnyAsync(s => s.ProjectId == id, Ct));
            Assert.False(await db.Artifacts.AnyAsync(a => a.ProjectId == id, Ct));
            Assert.False(await db.ApprovalGates.AnyAsync(g => g.ProjectId == id, Ct));
            Assert.False(await db.ExecutionSteps.AnyAsync(s => s.ProjectId == id, Ct));
            Assert.False(await db.AgentRuns.AnyAsync(r => r.ProjectId == id, Ct));
            Assert.False(await db.ComplianceReports.AnyAsync(r => r.ProjectId == id, Ct));
            Assert.False(await db.StateTransitions.AnyAsync(t => t.ProjectId == id, Ct));
            Assert.False(await db.Events.AnyAsync(e => e.ProjectId == id, Ct));

            var deleted = (await db.Events.Where(e => e.ProjectId == null && e.Type == EventTypes.ProjectDeleted).ToListAsync(Ct))
                .Single(e => e.Payload.Contains(id.ToString(), StringComparison.Ordinal));
            Assert.Equal(fixture.Owner.User!.Id.ToString(), deleted.Actor);
        });
    }

    [Fact]
    public async Task FR7_Delete_without_a_working_copy_succeeds()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var project = await ProjectApi.CreateAsync(owner.Client);

        var response = await owner.Client.DeleteAsync(new Uri($"/api/projects/{project.Id}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<PagedResult<ProjectSummaryDto>> ListAsync(HttpClient client, string query) =>
        await ProjectApi.ReadAsync<PagedResult<ProjectSummaryDto>>(await client.GetAsync(new Uri($"/api/projects?{query}", UriKind.Relative), Ct));

    /// <summary>Renames the project from another connection just before the API's UPDATE runs, so the API's
    /// <c>xmin</c> check fails (a concurrent edit).</summary>
    private sealed class ConcurrentProjectEdit(string connectionString, Guid projectId) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("UPDATE projects", StringComparison.Ordinal))
            {
                Fired = true;
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var rival = new NpgsqlCommand("UPDATE projects SET name = 'Theirs' WHERE id = @id", connection);
                rival.Parameters.AddWithValue("id", projectId);
                await rival.ExecuteNonQueryAsync(cancellationToken);
            }

            return result;
        }
    }
}
