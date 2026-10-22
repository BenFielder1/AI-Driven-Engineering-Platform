using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.Projects;

namespace Aide.UnitTests.Projects;

/// <summary>FR-73: import versions, repository tag checks, request shape and the Local import root (§8 Testing).</summary>
public class ImportRulesTests
{
    private static readonly string[] RepoTags = ["v2.2.0", "v2.3.0", "nightly"];

    private static readonly string[] Links = ["root/escape", "root/alias", "root-link"];

    [Fact]
    public void FR73_Released_v2_3_0_and_developing_2_4_0_is_accepted()
    {
        var result = ImportRules.ParseVersions("v2.3.0", "v2.4.0");

        Assert.Equal(("v2.3.0", "2.3.0", "2.4.0"), (result.Value.ReleasedTag, result.Value.ReleasedVersion!.ToString(), result.Value.DevelopmentVersion.ToString()));
        Assert.True(ImportRules.CheckAgainstRepository(result.Value, RepoTags).IsSuccess);
    }

    [Fact]
    public void FR73_Never_released_accepts_any_valid_development_version()
    {
        var result = ImportRules.ParseVersions(null, "0.1.0");

        Assert.Equal((null, null), (result.Value.ReleasedTag, result.Value.ReleasedVersion));
        Assert.True(ImportRules.CheckAgainstRepository(result.Value, []).IsSuccess);
    }

    [Theory]
    [InlineData("v2.3.0", "2.3.0", ErrorCodes.VersionNotGreater)]
    [InlineData("v2.3.0", "2.2.9", ErrorCodes.VersionNotGreater)]
    [InlineData("v2.3.0", "2.4", ErrorCodes.VersionInvalid)]
    [InlineData("v2.3.0", "02.4.0", ErrorCodes.VersionInvalid)]
    [InlineData("nightly", "2.4.0", ErrorCodes.VersionInvalid)]
    [InlineData("  ", "2.4.0", ErrorCodes.VersionInvalid)]
    public void FR73_Bad_semver_and_a_version_not_greater_are_refused(string releasedTag, string development, string code)
    {
        var result = ImportRules.ParseVersions(releasedTag, development);

        Assert.Equal(code, result.Error!.Code);
        Assert.Single(result.Error.Details);
    }

    [Fact]
    public void FR73_A_released_tag_after_a_custom_prefix_names_its_version()
    {
        Assert.Equal("1.0.0", ImportRules.ParseVersions("release-1.0.0", "1.1.0", tagPrefix: "release-").Value.ReleasedVersion!.ToString());
    }

    [Fact]
    public void FR73_A_tag_missing_from_the_repository_is_TAG_NOT_FOUND_and_an_existing_development_tag_is_TAG_EXISTS()
    {
        var missing = ImportRules.CheckAgainstRepository(ImportRules.ParseVersions("v2.1.0", "2.4.0").Value, RepoTags);
        Assert.Equal((ErrorCodes.TagNotFound, 400), (missing.Error!.Code, ErrorCodes.StatusCodeFor(missing.Error.Code)));

        var exists = ImportRules.CheckAgainstRepository(ImportRules.ParseVersions("v2.2.0", "2.3.0").Value, RepoTags);
        Assert.Equal(ErrorCodes.TagExists, exists.Error!.Code);
    }

    [Fact]
    public void FR73_The_request_shape_depends_on_the_source()
    {
        Assert.True(ImportRules.ValidateSource(ProjectSource.Scratch, false, true, false, GitProvider.Local).IsSuccess);
        Assert.True(ImportRules.ValidateSource(ProjectSource.Import, true, false, true, GitProvider.Local).IsSuccess);

        Assert.Equal(
            ["import: Only used with source import.", "git.localSourcePath: Only used with source import."],
            ImportRules.ValidateSource(ProjectSource.Scratch, true, false, true, GitProvider.Local).Error!.Details);
        var import = ImportRules.ValidateSource(ProjectSource.Import, false, true, true, GitProvider.GithubApp);
        Assert.Equal(ErrorCodes.Validation, import.Error!.Code);
        Assert.Equal(["import", "initialVersion", "git.localSourcePath"], import.Error.Details.Select(d => d.Split(':')[0]));
    }

    [Fact]
    public void FR73_A_local_import_source_must_resolve_under_the_configured_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "aide-import-root");

        Assert.Equal(Path.Combine(root, "demo"), ImportRules.ResolveLocalSource(root, "demo").Value);
        Assert.Equal(Path.Combine(root, "demo"), ImportRules.ResolveLocalSource(root + Path.DirectorySeparatorChar, Path.Combine(root, "demo") + Path.DirectorySeparatorChar).Value);
        Assert.Equal(ErrorCodes.Validation, ImportRules.ResolveLocalSource(root, "../elsewhere").Error!.Code);
        Assert.Equal(ErrorCodes.Validation, ImportRules.ResolveLocalSource(root, root + "-sibling").Error!.Code);
        Assert.Equal(ErrorCodes.Validation, ImportRules.ResolveLocalSource(root, "  ").Error!.Code);
        Assert.StartsWith("git.localSourcePath:", ImportRules.ResolveLocalSource(null, "demo").Error!.Details[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR73_A_link_or_junction_under_the_import_root_that_points_outside_it_is_refused()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "aide-import-links", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(baseDir, "root");
        var outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(Path.Combine(root, "inner"));
        Directory.CreateDirectory(outside);
        try
        {
            var escape = Path.Combine(root, "escape");
            var inside = Path.Combine(root, "alias");
            if (!await LinkAsync(escape, outside) || !await LinkAsync(inside, Path.Combine(root, "inner")))
            {
                Assert.Skip("Creating links or junctions isn't possible here.");
            }

            Assert.Equal(ErrorCodes.Validation, ImportRules.ResolveLocalSource(root, "escape").Error!.Code);
            Assert.Equal(ErrorCodes.Validation, ImportRules.ResolveLocalSource(root, "escape/sub").Error!.Code);
            Assert.Equal(Path.Combine(root, "inner"), ImportRules.ResolveLocalSource(root, "alias").Value); // the real path

            // A root reached through a link is compared by its real path too.
            var rootLink = Path.Combine(baseDir, "root-link");
            if (await LinkAsync(rootLink, root))
            {
                Assert.Equal(Path.Combine(root, "inner"), ImportRules.ResolveLocalSource(rootLink, "inner").Value);
            }
        }
        finally
        {
            foreach (var link in Links.Select(l => Path.Combine(baseDir, l)))
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
            }

            Directory.Delete(baseDir, recursive: true);
        }
    }

    /// <summary>A directory symbolic link, or on Windows without the privilege a junction (<c>mklink /J</c>).</summary>
    private static async Task<bool> LinkAsync(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start);
        if (process is null)
        {
            return false;
        }

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
