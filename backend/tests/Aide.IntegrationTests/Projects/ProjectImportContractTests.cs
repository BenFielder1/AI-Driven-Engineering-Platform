using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aide.Domain.Common;
using Aide.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Aide.IntegrationTests.Projects;

/// <summary>
/// FR-73 request validation: <c>POST /api/projects</c> with <c>source: import</c> checks the body shape and the versions
/// before touching any repository; <c>source</c> defaults to <c>scratch</c>. The import itself is in
/// <see cref="ProjectImportTests"/>.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ProjectImportContractTests(AideApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> CreateAsync(ApiSession session, object body) =>
        session.Client.PostAsJsonAsync("/api/projects", body, Ct);

    private static object Import(object? import, object? git = null, string? initialVersion = null) => new
    {
        name = "Imported",
        idea = "Add CSV export.",
        source = "import",
        git = git ?? new { mode = "local" },
        import,
        initialVersion,
    };

    [Fact]
    public async Task FR73_A_local_import_on_a_server_without_Git_Local_ImportRoot_is_VALIDATION_and_creates_nothing()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var response = await CreateAsync(owner, Import(new { releasedTag = "v2.3.0", developmentVersion = "2.4.0" }, new { mode = "local", localSourcePath = "demo" }));

        var body = await Problems.AssertAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        Assert.True(body.GetProperty("errors").TryGetProperty("git.localSourcePath", out _));
        await ProjectApi.WithDbAsync(factory, async db =>
            Assert.False(await db.ProjectMembers.AnyAsync(m => m.UserId == owner.User!.Id, Ct)));
    }

    [Fact]
    public async Task FR73_Never_released_is_a_null_or_omitted_releasedTag_whose_versions_pass_validation()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        // The versions are accepted; only the (disabled) Local source is refused.
        foreach (var import in new object[] { new { releasedTag = (string?)null, developmentVersion = "0.1.0" }, new { developmentVersion = "0.1.0" } })
        {
            var body = await Problems.AssertAsync(await CreateAsync(owner, Import(import)), HttpStatusCode.BadRequest, ErrorCodes.Validation);
            var errors = body.GetProperty("errors");
            Assert.True(errors.TryGetProperty("git.localSourcePath", out _));
            Assert.False(errors.TryGetProperty("import.releasedTag", out _));
        }
    }

    [Theory]
    [InlineData("v2.3.0", "2.4", HttpStatusCode.BadRequest, ErrorCodes.VersionInvalid)]
    [InlineData("nightly", "2.4.0", HttpStatusCode.BadRequest, ErrorCodes.VersionInvalid)]
    [InlineData("v2.3.0", "2.3.0", HttpStatusCode.Conflict, ErrorCodes.VersionNotGreater)]
    public async Task FR73_Bad_versions_are_refused_before_anything_else(string releasedTag, string developmentVersion, HttpStatusCode status, string code)
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        await Problems.AssertAsync(await CreateAsync(owner, Import(new { releasedTag, developmentVersion })), status, code);
    }

    [Fact]
    public async Task FR73_Import_needs_its_import_object_and_no_initialVersion()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var body = await Problems.AssertAsync(await CreateAsync(owner, Import(null, initialVersion: "1.0.0")), HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("import", out _));
        Assert.True(errors.TryGetProperty("initialVersion", out _));
    }

    [Fact]
    public async Task FR73_A_missing_developmentVersion_is_VALIDATION()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        await Problems.AssertAsync(await CreateAsync(owner, Import(new { releasedTag = "v1.0.0" })), HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task FR73_Scratch_projects_refuse_import_fields()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var body = await Problems.AssertAsync(
            await CreateAsync(owner, new
            {
                name = "Scratch",
                idea = "A todo app.",
                git = new { mode = "local", localSourcePath = "demo" },
                import = new { developmentVersion = "1.0.0" },
            }),
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation);

        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("import", out _));
        Assert.True(errors.TryGetProperty("git.localSourcePath", out _));
    }

    [Fact]
    public async Task FR73_Source_defaults_to_scratch_and_an_explicit_scratch_creates_the_project()
    {
        using var owner = await ApiSession.RegisterAsync(factory);

        var response = await CreateAsync(owner, new { name = "Scratch", idea = "A todo app.", source = "scratch", git = new { mode = "local" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var unknown = await CreateAsync(owner, new { name = "X", idea = "Y", source = "clone", git = new { mode = "local" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var problem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ErrorCodes.Validation, problem.RootElement.GetProperty("errorCode").GetString());
    }
}
