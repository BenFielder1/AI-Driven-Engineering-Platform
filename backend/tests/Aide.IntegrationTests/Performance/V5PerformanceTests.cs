using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Analysis;
using Aide.Api.Features.Projects;
using Aide.Domain.Artifacts;
using Aide.Domain.Lifecycle;
using Aide.IntegrationTests.Support;

namespace Aide.IntegrationTests.Performance;

/// <summary>
/// §8 v5 Performance, measured as <see cref="V4PerformanceTests"/> does: p95 over sequential requests through the API against the
/// Testcontainers database, after a warm-up. The tracker timeouts (search, <c>sources/check</c>) are in
/// <see cref="Trackers.TrackerLimitsTests"/>.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class V5PerformanceTests(AideApiFactory factory) : IAsyncLifetime
{
    private const int Samples = 40;
    private const int WarmUp = 5;

    private readonly string _importRoot = Path.Combine(Path.GetTempPath(), "aide-tests-perf5", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var repo = Path.Combine(_importRoot, "vue-supabase");
        Directory.CreateDirectory(Path.Combine(repo, "frontend", "src"));
        Directory.CreateDirectory(Path.Combine(repo, "supabase", "migrations"));
        await TestGit.RunAsync(repo, Ct, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# Notes\n\nA Vue app on Supabase.\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "frontend", "package.json"), """{ "name": "notes", "dependencies": { "vue": "^3.5.13" }, "devDependencies": { "vite": "^7.0.0" } }""", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "frontend", "src", "main.ts"), "import { createApp } from 'vue';\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "supabase", "migrations", "20260101000000_init.sql"), "create table notes (id uuid primary key);\n", Ct);
        await TestGit.RunAsync(repo, Ct, "add", ".");
        await TestGit.RunAsync(repo, Ct, "commit", "-q", "-m", "First");
    }

    public ValueTask DisposeAsync()
    {
        TestGit.DeleteTree(_importRoot);
        return ValueTask.CompletedTask;
    }

    /// <summary>The Analysis page of a version that inferred its stack, with the Stack Analyst's draft (the page's heaviest state).</summary>
    [Fact]
    public async Task S8_GET_analysis_p95_is_under_300ms()
    {
        await using var app = factory.WithWebHostBuilder(b => b.UseSetting("Git:Local:ImportRoot", _importRoot));
        using var owner = await ApiSession.RegisterAsync(app);
        var project = await ProjectApi.ReadAsync<ProjectDto>(
            await owner.Client.PostAsJsonAsync(
                "/api/projects",
                new
                {
                    name = ProjectApi.UniqueName("perf5"),
                    idea = "Add sharing of notes.",
                    source = "import",
                    git = new { mode = "local", localSourcePath = "vue-supabase" },
                    import = new { releasedTag = (string?)null, developmentVersion = "0.1.0" },
                    stackSource = "infer",
                },
                Ct),
            HttpStatusCode.Created);
        await AgentJobs.RunQueuedAsync(app, project.Id);
        var path = new Uri($"/api/projects/{project.Id}/analysis", UriKind.Relative);

        var analysis = await ProjectApi.ReadAsync<AnalysisDto>(await owner.Client.GetAsync(path, Ct));
        Assert.Equal((Phase.Analysis, ArtifactStatus.Draft), (analysis.Phase, analysis.Profile!.Status));

        for (var i = 0; i < WarmUp; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync(path, Ct)).StatusCode);
        }

        var p95 = Latency.Percentile(await Latency.SampleAsync(Samples, async () => Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync(path, Ct)).StatusCode)), 95);
        Assert.True(p95 < TimeSpan.FromMilliseconds(300), $"p95 of GET /api/projects/:id/analysis took {p95.TotalMilliseconds} ms (limit 300 ms).");
    }
}
