using System.Net;
using Aide.Api.Features.Admin;
using Aide.Api.Features.Projects;
using Aide.Domain.Access;
using Aide.Domain.Entities;
using Aide.Domain.Lifecycle;
using Aide.Infrastructure.Persistence;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aide.IntegrationTests.Projects;

/// <summary>NFR-PERF2 (§8): the project list with its version and preset badges stays a fixed number of queries and p95 &lt; 300 ms.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProjectListPerformanceTests(AideApiFactory factory)
{
    private const int ManyProjects = 200;
    private const int Samples = 40;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// <paramref name="count"/> projects of <paramref name="ownerId"/>, each with a released 1.0.0 and 1.1.0 in development
    /// (every other one on a preset), so every row carries the released, development and preset badges.
    /// </summary>
    private static Task SeedAsync(WebApplicationFactory<Program> app, Guid ownerId, int count) =>
        ProjectApi.WithDbAsync(app, async db =>
        {
            var t0 = DateTimeOffset.UtcNow.AddDays(-1);
            for (var i = 0; i < count; i++)
            {
                var at = t0.AddSeconds(i);
                var project = new Project
                {
                    Name = $"Perf {i:D3} {Guid.NewGuid():N}",
                    Slug = $"perf-{Guid.NewGuid():N}",
                    Idea = "Measure the list.",
                    CurrentPhase = Phase.Execution,
                    Status = ProjectStatus.Building,
                    Checkpoint = 4,
                    ReleasedVersion = "1.0.0",
                    DevelopmentVersion = "1.1.0",
                    CreatedAt = at,
                    UpdatedAt = at,
                };
                var released = new ReleaseVersion
                {
                    ProjectId = project.Id,
                    Version = "1.0.0",
                    Status = ReleaseVersionStatus.Released,
                    Tag = "v1.0.0",
                    Request = "First version.",
                    CreatedAt = at.AddHours(-2),
                    ReleasedAt = at.AddHours(-1),
                };
                var development = new ReleaseVersion
                {
                    ProjectId = project.Id,
                    Version = "1.1.0",
                    Status = ReleaseVersionStatus.InDevelopment,
                    BaseTag = "v1.0.0",
                    Request = "Second version.",
                    PresetId = i % 2 == 0 ? "nextjs-supabase" : null,
                    PresetVersion = i % 2 == 0 ? "1.0.0" : null,
                    CreatedAt = at,
                };
                project.CurrentReleaseVersionId = development.Id;
                db.Projects.Add(project);
                db.ReleaseVersions.AddRange(released, development);
                db.ProjectMembers.Add(new ProjectMembership { ProjectId = project.Id, UserId = ownerId, Role = ProjectRole.Owner, CreatedAt = at });
            }

            await db.SaveChangesAsync(Ct);
        });

    [Fact]
    public async Task NFR_PERF2_The_project_list_with_badges_takes_a_fixed_number_of_queries_and_p95_under_300ms_for_200_projects()
    {
        var counter = new DbCommandCounter();
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.ConfigureDbContext<AideDbContext>(o => o.AddInterceptors(counter))));
        using var few = await ApiSession.RegisterAsync(app);
        using var many = await ApiSession.RegisterAsync(app);
        await SeedAsync(app, few.User!.Id, 10);
        await SeedAsync(app, many.User!.Id, ManyProjects);

        async Task<(PagedResult<ProjectSummaryDto> Page, int Queries)> CountAsync(ApiSession session, string path)
        {
            Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync(new Uri(path, UriKind.Relative), Ct)).StatusCode); // warm-up
            var before = counter.Count;
            var page = await ProjectApi.ReadAsync<PagedResult<ProjectSummaryDto>>(await session.Client.GetAsync(new Uri(path, UriKind.Relative), Ct));
            return (page, counter.Count - before);
        }

        var (fewPage, fewQueries) = await CountAsync(few, "/api/projects?limit=100");
        var (manyPage, manyQueries) = await CountAsync(many, "/api/projects?limit=100");

        Assert.Equal((10, 10, ManyProjects, 100), (fewPage.Total, fewPage.Items.Count, manyPage.Total, manyPage.Items.Count));
        Assert.Equal(fewQueries, manyQueries);
        Assert.InRange(manyQueries, 1, 6);
        Assert.All(manyPage.Items, p => Assert.Equal(("1.0.0", "1.1.0", ProjectRole.Owner), (p.ReleasedVersion, p.DevelopmentVersion, p.MyRole)));
        Assert.Contains(manyPage.Items, p => p.PresetId == "nextjs-supabase");
        Assert.Contains(manyPage.Items, p => p.PresetId is null);

        // §7.2: the KPI row is one aggregate query (plus the role lookup), however many projects there are.
        async Task<(ProjectKpisDto Kpis, int Queries)> KpisAsync(ApiSession session)
        {
            Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync(new Uri("/api/projects/kpis", UriKind.Relative), Ct)).StatusCode); // warm-up
            var before = counter.Count;
            var kpis = await ProjectApi.ReadAsync<ProjectKpisDto>(await session.Client.GetAsync(new Uri("/api/projects/kpis", UriKind.Relative), Ct));
            return (kpis, counter.Count - before);
        }

        var (fewKpis, fewKpiQueries) = await KpisAsync(few);
        var (manyKpis, manyKpiQueries) = await KpisAsync(many);
        Assert.Equal((10, ManyProjects, ManyProjects), (fewKpis.Total, manyKpis.Total, manyKpis.Active));
        Assert.Equal(fewKpiQueries, manyKpiQueries);
        Assert.InRange(manyKpiQueries, 1, 2);

        // §8: p95 over sequential requests, for the default page (20) and the largest (100), sorted and filtered as the list page does.
        foreach (var path in new[] { "/api/projects", "/api/projects?limit=100", "/api/projects?sortBy=name&sortOrder=asc&hasDevelopmentVersion=true", "/api/projects?preset=nextjs-supabase", "/api/projects/kpis" })
        {
            var samples = await Latency.SampleAsync(Samples, async () =>
                Assert.Equal(HttpStatusCode.OK, (await many.Client.GetAsync(new Uri(path, UriKind.Relative), Ct)).StatusCode));
            var p95 = Latency.Percentile(samples, 95);
            Assert.True(p95 < TimeSpan.FromMilliseconds(300), $"p95 of {path} over {ManyProjects} projects took {p95.TotalMilliseconds} ms.");
        }
    }
}
