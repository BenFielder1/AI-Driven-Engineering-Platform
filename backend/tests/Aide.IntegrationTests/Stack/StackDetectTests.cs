using System.Net;
using System.Net.Http.Json;
using Aide.Api.Features.Stack;
using Aide.Domain.Common;
using Aide.Domain.Stack;
using Aide.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aide.IntegrationTests.Stack;

/// <summary>
/// FR-118, §6 <c>POST /api/stack/detect</c>: import detection over a Local Git repository under <c>Git:Local:ImportRoot</c>
/// (a Vue + Vite frontend in <c>frontend/</c> with Supabase, no backend), the ref it reads, and its errors.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class StackDetectTests(AideApiFactory factory) : IAsyncLifetime
{
    private readonly string _importRoot = Path.Combine(Path.GetTempPath(), "aide-tests-detect", Guid.NewGuid().ToString("N"));
    private string _first = string.Empty;
    private string _head = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // The import root is itself inside a repository, with a plain (non-repository) folder holding a manifest.
        Directory.CreateDirectory(Path.Combine(_importRoot, "plain"));
        await TestGit.RunAsync(_importRoot, Ct, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(_importRoot, "plain", "package.json"), """{ "dependencies": { "next": "^16.1.0" } }""", Ct);
        await TestGit.RunAsync(_importRoot, Ct, "add", "plain");
        await TestGit.RunAsync(_importRoot, Ct, "commit", "-q", "-m", "Parent");

        var repo = Path.Combine(_importRoot, "vue-supabase");
        Directory.CreateDirectory(Path.Combine(repo, "frontend", "src"));
        await TestGit.RunAsync(repo, Ct, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# Vue shop\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "frontend", "index.html"), "<div id=\"app\"></div>\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "frontend", "src", "main.ts"), "import { createApp } from 'vue';\n", Ct);
        await File.WriteAllTextAsync(
            Path.Combine(repo, "frontend", "package.json"),
            """{ "name": "shop", "dependencies": { "vue": "^3.5.13" }, "devDependencies": { "vite": "^6.2.0" } }""",
            Ct);
        await TestGit.RunAsync(repo, Ct, "add", ".");
        await TestGit.RunAsync(repo, Ct, "commit", "-q", "-m", "Frontend");
        await TestGit.RunAsync(repo, Ct, "tag", "v1.0.0");
        _first = await TestGit.RunAsync(repo, Ct, "rev-parse", "HEAD");

        Directory.CreateDirectory(Path.Combine(repo, "supabase", "migrations"));
        await File.WriteAllTextAsync(Path.Combine(repo, "supabase", "config.toml"), "project_id = \"shop\"\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "supabase", "migrations", "20260101000000_init.sql"), "create table orders (id bigint);\n", Ct);
        await TestGit.RunAsync(repo, Ct, "add", ".");
        await TestGit.RunAsync(repo, Ct, "commit", "-q", "-m", "Supabase");
        _head = await TestGit.RunAsync(repo, Ct, "rev-parse", "HEAD");
    }

    public ValueTask DisposeAsync()
    {
        TestGit.DeleteTree(_importRoot);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> App() => factory.WithWebHostBuilder(b => b.UseSetting("Git:Local:ImportRoot", _importRoot));

    private static Task<HttpResponseMessage> DetectAsync(HttpClient client, object git) =>
        client.PostAsJsonAsync("/api/stack/detect", new { git }, Ct);

    [Fact]
    public async Task FR118_Given_a_vue_frontend_with_supabase_and_no_backend_then_fe_vue_at_frontend_backend_none_and_supabase_baas()
    {
        await using var app = App();
        using var user = await ApiSession.RegisterAsync(app);

        var detection = await ProjectApi.ReadAsync<StackDetectionDto>(await DetectAsync(user.Client, new { provider = "local", repo = "vue-supabase" }));

        Assert.Equal(("main", _head, false), (detection.Ref, detection.Commit, detection.Truncated));
        StackDetectedChoiceDto Suggested(string choice) => Assert.Single(detection.Suggestions, s => s.Choice == choice);
        Assert.Equal(("fe-vue", "frontend"), (Suggested(StackChoices.Frontend).Option, Suggested(StackChoices.Frontend).Path));
        Assert.Contains("`vue` 3.5 in `frontend/package.json`", Suggested(StackChoices.Frontend).Evidence);
        Assert.Equal(StackIds.None, Suggested(StackChoices.Backend).Option);
        Assert.Equal(("data-supabase", "baas"), (Suggested(StackChoices.Data).Option, Suggested(StackChoices.Data).Mode));
        Assert.Equal("vcs-github", Suggested(StackChoices.Vcs).Option);
        Assert.Empty(detection.Conflicts);

        // At an earlier ref (the v1.0.0 tag) there is no Supabase yet.
        var tagged = await ProjectApi.ReadAsync<StackDetectionDto>(await DetectAsync(user.Client, new { provider = "local", repo = "vue-supabase", @ref = "v1.0.0" }));
        Assert.Equal(("v1.0.0", _first), (tagged.Ref, tagged.Commit));
        Assert.DoesNotContain(tagged.Suggestions, s => s.Choice == StackChoices.Data);
    }

    [Fact]
    public async Task FR118_Unknown_repositories_and_refs_are_NOT_FOUND_and_malformed_requests_VALIDATION()
    {
        await using var app = App();
        using var user = await ApiSession.RegisterAsync(app);

        await Problems.AssertAsync(await DetectAsync(user.Client, new { provider = "local", repo = "missing" }), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await Problems.AssertAsync(await DetectAsync(user.Client, new { provider = "local", repo = "vue-supabase", @ref = "no-such-branch" }), HttpStatusCode.NotFound, ErrorCodes.NotFound);

        var outside = await Problems.AssertAsync(await DetectAsync(user.Client, new { provider = "local", repo = "../elsewhere" }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(outside.GetProperty("errors").TryGetProperty("git.repo", out _), outside.ToString());
        var option = await Problems.AssertAsync(await DetectAsync(user.Client, new { provider = "local", repo = "vue-supabase", @ref = "--output=/tmp/x" }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(option.GetProperty("errors").TryGetProperty("git.ref", out _), option.ToString());
        await Problems.AssertAsync(await DetectAsync(user.Client, new { provider = "local", repo = "vue-supabase", instanceId = Guid.NewGuid() }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await Problems.AssertAsync(await DetectAsync(user.Client, new { provider = "github_pat", repo = "not a repo" }), HttpStatusCode.BadRequest, ErrorCodes.Validation);

        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await DetectAsync(anonymous, new { provider = "local", repo = "vue-supabase" })).StatusCode);
    }

    [Fact]
    public async Task FR118_A_folder_that_isnt_itself_a_repository_is_NOT_FOUND_even_inside_a_parent_repository()
    {
        await using var app = App();
        using var user = await ApiSession.RegisterAsync(app);

        await Problems.AssertAsync(await DetectAsync(user.Client, new { provider = "local", repo = "plain" }), HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task FR118_Local_detection_needs_the_import_root_and_github_app_needs_a_GitHub_connection()
    {
        using var user = await ApiSession.RegisterAsync(factory);

        // Without Git:Local:ImportRoot, local repositories can't be read.
        var disabled = await Problems.AssertAsync(await DetectAsync(user.Client, new { provider = "local", repo = "vue-supabase" }), HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(disabled.GetProperty("errors").TryGetProperty("git.repo", out _), disabled.ToString());

        // §6: no (valid) GitHub connection is GIT_NOT_CONNECTED.
        await Problems.AssertAsync(await DetectAsync(user.Client, new { provider = "github_app", repo = "acme/shop" }), HttpStatusCode.Conflict, ErrorCodes.GitNotConnected);
    }
}
