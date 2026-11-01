using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Databases;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.Domain.Lifecycle;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Performance;

/// <summary>
/// §8 v4 Performance, measured as NFR-PERF2 was: p95 over sequential requests through the API against the Testcontainers
/// database, after a warm-up. Each target is a test of its own, so a slow one names its endpoint.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class V4PerformanceTests(AideApiFactory factory)
{
    private const int Samples = 40;
    private const int WarmUp = 5;
    private const int ManyProjects = 200;
    private const int Variables = 50;

    private static readonly string[] BothEnvironments = ["preview", "production"];

    private static readonly string[] BackendOnly = ["backend"];

    private static readonly object PatGit = new { mode = "github_pat", repoUrl = "https://github.com/acme/todo", token = "ghp_v4PerformanceToken001" };

    private static readonly object NetlifyRenderStack = new
    {
        frontend = new { pack = "fe-react-vite", path = "frontend" },
        backend = new { pack = "be-aspnet", path = "backend" },
        data = new { pack = "data-efcore-postgres" },
        deploy = new { frontendHost = "deploy-netlify", backendHost = "deploy-render", databaseHost = "render-postgres" },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri U(string path) => new(path, UriKind.Relative);

    private static async Task AssertP95Async(string name, TimeSpan limit, Func<Task> call)
    {
        for (var i = 0; i < WarmUp; i++)
        {
            await call();
        }

        var p95 = Latency.Percentile(await Latency.SampleAsync(Samples, call), 95);
        Assert.True(p95 < limit, $"p95 of {name} took {p95.TotalMilliseconds} ms (limit {limit.TotalMilliseconds} ms).");
    }

    private static Task AssertGetP95Async(HttpClient client, string path, TimeSpan limit) =>
        AssertP95Async(path, limit, async () => Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(U(path), Ct)).StatusCode));

    [Fact]
    public async Task S8_POST_stack_options_p95_is_under_50ms()
    {
        using var user = await ApiSession.RegisterAsync(factory);
        var body = new
        {
            frontend = new { pack = "fe-react-vite" },
            backend = new { pack = "be-aspnet" },
            data = new { pack = "data-efcore-postgres" },
            vcs = new { pack = "vcs-github" },
            deploy = new { frontendHost = "deploy-netlify", backendHost = "deploy-render", databaseHost = "render-postgres" },
        };

        await AssertP95Async("POST /api/stack/options", TimeSpan.FromMilliseconds(50), async () =>
            Assert.Equal(HttpStatusCode.OK, (await user.Client.PostAsJsonAsync("/api/stack/options", body, Ct)).StatusCode));
    }

    [Fact]
    public async Task S8_GET_llm_providers_p95_is_under_100ms()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        await AssertGetP95Async(user.Client, "/api/llm/providers", TimeSpan.FromMilliseconds(100));
    }

    /// <summary>The list's <c>pack</c> filter reads <c>projects.stack_packs</c>, so half of these carry the pack it asks for and the page has rows to build.</summary>
    [Fact]
    public async Task S8_FR117_The_projects_list_filtered_by_a_stack_pack_p95_is_under_300ms_for_200_projects()
    {
        using var owner = await ApiSession.RegisterAsync(factory);
        var ownerId = owner.User!.Id;
        await ProjectApi.WithDbAsync(factory, async db =>
        {
            var at = DateTimeOffset.UtcNow.AddDays(-1);
            for (var i = 0; i < ManyProjects; i++)
            {
                var project = new Project
                {
                    Name = $"Perf {i:D3} {Guid.NewGuid():N}",
                    Slug = $"perf-{Guid.NewGuid():N}",
                    Idea = "Measure the list.",
                    CurrentPhase = Phase.Execution,
                    Status = ProjectStatus.Building,
                    Checkpoint = 4,
                    DevelopmentVersion = "0.1.0",
                    StackPacks = i % 2 == 0 ? ["fe-nextjs", "vcs-github"] : ["fe-vue", "data-supabase", "vcs-github"],
                    CreatedAt = at.AddSeconds(i),
                    UpdatedAt = at.AddSeconds(i),
                };
                var version = new ReleaseVersion { ProjectId = project.Id, Version = "0.1.0", Status = ReleaseVersionStatus.InDevelopment, Request = "First version.", CreatedAt = at };
                project.CurrentReleaseVersionId = version.Id;
                db.Projects.Add(project);
                db.ReleaseVersions.Add(version);
                db.ProjectMembers.Add(new ProjectMembership { ProjectId = project.Id, UserId = ownerId, Role = ProjectRole.Owner, CreatedAt = at });
            }

            await db.SaveChangesAsync(Ct);
        });

        var page = await ProjectApi.ReadAsync<PagedResult<ProjectSummaryDto>>(await owner.Client.GetAsync(U("/api/projects?pack=fe-nextjs&limit=100"), Ct));
        Assert.Equal((ManyProjects / 2, 100), (page.Total, page.Items.Count));

        foreach (var path in new[] { "/api/projects?pack=fe-nextjs", "/api/projects?pack=fe-nextjs&limit=100", "/api/projects?pack=vcs-github&limit=100" })
        {
            await AssertGetP95Async(owner.Client, path, TimeSpan.FromMilliseconds(300));
        }
    }

    [Fact]
    public async Task S8_FR144_FR145_FR152_The_deployment_settings_p95_is_under_300ms_with_two_targets_two_databases_and_50_variables()
    {
        var app = DeploymentHarness.Fake(factory);
        using var owner = await ApiSession.RegisterAsync(app);
        var id = (await ProjectApi.CreateAsync(owner.Client, git: PatGit)).Id;
        await DeploymentHarness.SetStackAsync(owner.Client, id, NetlifyRenderStack);
        var netlify = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "nfp_seededToken0123", provider: PlatformProvider.Netlify);
        var render = await DeploymentHarness.SeedConnectionAsync(app, owner.User!.Id, token: "rnd_seededKey0123", provider: PlatformProvider.Render);
        await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "backend", new { provider = "render", connectionId = render, create = new { name = "todo-api" } });
        await DeploymentHarness.SetUpTargetAsync(app, owner.Client, id, "frontend", new { provider = "netlify", connectionId = netlify, create = new { name = "todo" } });
        foreach (var environment in new[] { "production", "preview" })
        {
            await ProjectApi.ReadAsync<DeploymentDatabaseDto>(
                await owner.Client.PutAsJsonAsync($"{DeploymentHarness.SettingsPath(id)}/databases/{environment}", new { provider = "render", connectionId = render, create = new { region = "oregon" } }, Ct),
                HttpStatusCode.Accepted);
            var job = await ProjectApi.WithDbAsync(app, db => db.Jobs.AsNoTracking()
                .Where(j => j.ProjectId == id && j.Type == JobTypes.DatabaseProvisioning)
                .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
                .FirstAsync(Ct));
            await DeploymentHarness.SettleAsync(app, job.Id);
        }

        for (var i = 0; i < Variables; i++)
        {
            var saved = await owner.Client.PutAsJsonAsync($"{DeploymentHarness.SettingsPath(id)}/env/PERF_VAR_{i:D2}", new { value = $"value-{i}", targets = BothEnvironments, sensitive = false, appliesTo = BackendOnly }, Ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        var settings = await DeploymentHarness.SettingsAsync(owner.Client, id);
        Assert.Equal(2, settings.Targets.Count);
        Assert.Equal(["available", "available"], settings.Databases.Select(d => d.Status));
        Assert.Equal(Variables, settings.Env.Variables.Count(v => v.Key.StartsWith("PERF_VAR_", StringComparison.Ordinal)));

        await AssertGetP95Async(owner.Client, DeploymentHarness.SettingsPath(id), TimeSpan.FromMilliseconds(300));
    }
}
