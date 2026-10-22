using System.Net;
using System.Security.Cryptography;
using Aide.Api.Features.GitHub;
using Aide.Api.Features.Projects;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Jobs;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.GitHub;

/// <summary>
/// FR-73 and FR-86 against a fake GitHub (<see cref="GitHubAppHarness"/>, stub HTTP): importing a repository picked through
/// the App or given as URL + PAT, and a scratch project on a repository that already has code.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GitHubImportTests(AideApiFactory factory)
{
    private const long OrgInstallation = 2002;
    private const string V220 = "2222222222222222222222222222222222222222";
    private const string V230 = "3333333333333333333333333333333333333333";
    private const string Head = "4444444444444444444444444444444444444444";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri U(string path) => new(path, UriKind.Relative);

    private static long NewGitHubId() => RandomNumberGenerator.GetInt32(1, int.MaxValue) + 20_000_000_000L;

    /// <summary>
    /// The repository <c>acme/shop</c> with code and the given tags, answered by exact ref lookups (<c>git/ref/tags/…</c>):
    /// v2.3.0 as an annotated tag (a tag object), the others lightweight. Tag lists are not stubbed: an import never lists.
    /// </summary>
    private static void OnShop(GitHubAppHarness harness, params (string Name, string Sha)[] tags)
    {
        harness.OnRepoInstallation("acme", "shop", OrgInstallation);
        harness.Api.On(HttpMethod.Get, "/repos/acme/shop", HttpStatusCode.OK, GitHubAppHarness.RepoJson(55, "acme", "shop", size: 300));
        harness.OnBranchRoot("acme", "shop", "main", Head, "README.md", "src", "package.json");
        foreach (var (name, sha) in tags)
        {
            if (name == "v2.3.0")
            {
                harness.Api.On(HttpMethod.Get, $"/repos/acme/shop/git/ref/tags/{name}", HttpStatusCode.OK,
                    $$$"""{"ref":"refs/tags/{{{name}}}","object":{"type":"tag","sha":"7a90000000000000000000000000000000000000"}}""");
                harness.Api.On(HttpMethod.Get, "/repos/acme/shop/git/tags/7a90000000000000000000000000000000000000", HttpStatusCode.OK,
                    $$$"""{"tag":"{{{name}}}","object":{"type":"commit","sha":"{{{sha}}}"}}""");
            }
            else
            {
                harness.Api.On(HttpMethod.Get, $"/repos/acme/shop/git/ref/tags/{name}", HttpStatusCode.OK,
                    $$$"""{"ref":"refs/tags/{{{name}}}","object":{"type":"commit","sha":"{{{sha}}}"}}""");
            }
        }
    }

    private static Task<HttpResponseMessage> PostAsync(ApiSession owner, string bodyJson) =>
        owner.Client.PostAsync(U("/api/projects"), new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json"), Ct);

    private static string ImportBody(string gitJson, string? releasedTag, string developmentVersion)
    {
        var import = System.Text.Json.JsonSerializer.Serialize(new { releasedTag, developmentVersion });
        return $$"""{"name":"{{ProjectApi.UniqueName("shop")}}","idea":"Add CSV export.","source":"import","git":{{gitJson}},"import":{{import}} }""";
    }

    private const string AppGit = """{"mode":"github_app","installationId":2002,"repoFullName":"acme/shop"}""";

    [Fact]
    public async Task FR73_Given_releases_v2_2_0_and_v2_3_0_when_imported_through_the_App_then_Released_2_3_0_and_In_development_2_4_0()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var owner = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(owner.User!.Id, NewGitHubId(), "octo");
        OnShop(harness, ("v2.3.0", V230), ("v2.2.0", V220));

        var project = await ProjectApi.ReadAsync<ProjectDto>(await PostAsync(owner, ImportBody(AppGit, "v2.3.0", "2.4.0")), HttpStatusCode.Created);
        Assert.Equal(0, harness.Api.Count(HttpMethod.Get, "/repos/acme/shop/tags")); // exact lookups, whatever the number of tags

        Assert.Equal(("2.3.0", "2.4.0"), (project.ReleasedVersion, project.DevelopmentVersion));
        var versions = await ProjectApi.WithDbAsync(harness.App, db => db.ReleaseVersions.AsNoTracking().Where(v => v.ProjectId == project.Id).OrderBy(v => v.CreatedAt).ToListAsync(Ct));
        Assert.Equal(
            [(ReleaseVersionStatus.Imported, "2.3.0", "v2.3.0", (string?)V230, (string?)null), (ReleaseVersionStatus.InDevelopment, "2.4.0", null, null, V230)],
            versions.Select(v => (v.Status, v.Version, v.Tag, v.MergeCommitSha, v.BaseCommitSha)));
        Assert.Equal("v2.3.0", versions[1].BaseTag);
        var git = await ProjectApi.WithDbAsync(harness.App, db => db.ProjectGitSettings.AsNoTracking().SingleAsync(s => s.ProjectId == project.Id, Ct));
        Assert.Equal((GitProvider.GithubApp, (long?)OrgInstallation, (long?)55, "acme/shop"), (git.Provider, git.InstallationId, git.RepoId, git.RepoFullName));
        Assert.Contains(await LifecycleSeed.JobsAsync(harness.App, project.Id), j => j.Type == JobTypes.RepositoryAnalysis);
    }

    [Fact]
    public async Task FR73_A_released_tag_the_repository_doesnt_have_is_TAG_NOT_FOUND()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var owner = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(owner.User!.Id, NewGitHubId(), "octo");
        OnShop(harness, ("v2.2.0", V220));

        await Problems.AssertAsync(await PostAsync(owner, ImportBody(AppGit, "v2.3.0", "2.4.0")), HttpStatusCode.BadRequest, ErrorCodes.TagNotFound);
    }

    [Fact]
    public async Task FR73_Importing_a_repository_the_App_isnt_installed_on_is_GITHUB_APP_NOT_INSTALLED()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var owner = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(owner.User!.Id, NewGitHubId(), "octo");
        harness.OnRepoInstallation("acme", "shop", 999);

        await Problems.AssertAsync(await PostAsync(owner, ImportBody(AppGit, "v2.3.0", "2.4.0")), HttpStatusCode.Conflict, ErrorCodes.GitHubAppNotInstalled);
    }

    [Fact]
    public async Task FR73_FR83_A_PAT_import_reads_the_tags_with_the_token()
    {
        const string token = "ghp_importTheShop0123456789";
        await using var harness = GitHubAppHarness.Create(factory);
        using var owner = await ApiSession.RegisterAsync(harness.App);
        OnShop(harness, ("v2.3.0", V230));

        var project = await ProjectApi.ReadAsync<ProjectDto>(
            await PostAsync(owner, ImportBody($$"""{"mode":"github_pat","repoUrl":"https://github.com/acme/shop","token":"{{token}}"}""", "v2.3.0", "3.0.0")),
            HttpStatusCode.Created);

        Assert.Equal(("2.3.0", "3.0.0"), (project.ReleasedVersion, project.DevelopmentVersion));
        Assert.All(harness.Api.Requests.Where(r => r.Path.StartsWith("/repos/acme/shop", StringComparison.Ordinal)), r => Assert.Equal($"Bearer {token}", r.Authorization));
        var version = await ProjectApi.WithDbAsync(harness.App, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == project.Id && v.Status == ReleaseVersionStatus.InDevelopment, Ct));
        Assert.Equal(("v2.3.0", V230), (version.BaseTag, version.BaseCommitSha));
    }

    [Fact]
    public async Task FR73_An_existing_development_tag_is_TAG_EXISTS()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var owner = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(owner.User!.Id, NewGitHubId(), "octo");
        OnShop(harness, ("v2.3.0", V230), ("v2.4.0", Head));

        await Problems.AssertAsync(await PostAsync(owner, ImportBody(AppGit, "v2.3.0", "2.4.0")), HttpStatusCode.Conflict, ErrorCodes.TagExists);
    }

    [Fact]
    public async Task FR73_The_refs_picker_reads_every_tag_page_and_always_offers_the_release_tags()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var owner = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(owner.User!.Id, NewGitHubId(), "octo");
        OnShop(harness, ("v1.0.0", V220));
        harness.Api.On(HttpMethod.Get, "/repos/acme/shop/releases", r => (HttpStatusCode.OK, r.Query.Contains("page=1", StringComparison.Ordinal)
            ? """[{"tag_name":"v2.3.0","published_at":"2026-09-01T10:00:00Z","draft":false},{"tag_name":"v1.0.0","published_at":"2020-01-01T10:00:00Z","draft":false}]"""
            : "[]"));
        harness.Api.On(HttpMethod.Get, "/repos/acme/shop/releases/latest", HttpStatusCode.OK, """{"tag_name":"v2.3.0"}""");
        // 150 tags over two pages; v1.0.0 (a release) is in neither, v2.3.0 is on page 2.
        harness.Api.On(HttpMethod.Get, "/repos/acme/shop/tags", r =>
        {
            var page = r.Query.Contains("page=2", StringComparison.Ordinal) ? 2 : 1;
            var names = page == 1
                ? Enumerable.Range(0, 100).Select(i => $"build-{i}")
                : Enumerable.Range(100, 49).Select(i => $"build-{i}").Append("v2.3.0");
            return (HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(names.Select(n => new { name = n, commit = new { sha = n == "v2.3.0" ? V230 : Head } })));
        });

        var refs = await ProjectApi.ReadAsync<GitHubRepoRefsDto>(await owner.Client.GetAsync(U("/api/me/github/repos/acme/shop/refs"), Ct));

        Assert.Equal(151, refs.Tags.Count);
        Assert.Contains(new GitHubTagDto("v2.3.0", V230, "2.3.0"), refs.Tags);
        Assert.Contains(new GitHubTagDto("v1.0.0", V220, "1.0.0"), refs.Tags);
        Assert.Equal(2, harness.Api.Count(HttpMethod.Get, "/repos/acme/shop/tags"));
    }

    [Fact]
    public async Task FR86_Given_a_non_empty_repo_in_start_from_scratch_then_the_Repository_Analyst_is_queued()
    {
        await using var harness = GitHubAppHarness.Create(factory);
        using var owner = await ApiSession.RegisterAsync(harness.App);
        await harness.SeedConnectionAsync(owner.User!.Id, NewGitHubId(), "octo");
        OnShop(harness);

        var project = await ProjectApi.ReadAsync<ProjectDto>(
            await PostAsync(owner, $$"""{"name":"{{ProjectApi.UniqueName("shop")}}","idea":"Build on the shop.","git":{{AppGit}}}"""), HttpStatusCode.Created);

        var version = await ProjectApi.WithDbAsync(harness.App, db => db.ReleaseVersions.AsNoTracking().SingleAsync(v => v.ProjectId == project.Id, Ct));
        Assert.Equal((Head, (string?)null), (version.BaseCommitSha, version.BaseTag));
        var analysis = Assert.Single(await LifecycleSeed.JobsAsync(harness.App, project.Id), j => j.Type == JobTypes.RepositoryAnalysis);
        Assert.Equal(version.Id.ToString(), LifecycleSeed.Payload(analysis.Payload).GetProperty("releaseVersionId").GetString());
    }
}
