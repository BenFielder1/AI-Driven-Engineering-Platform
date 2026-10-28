using Aide.Domain.Stack;

namespace Aide.UnitTests.Stack;

/// <summary>FR-118: the file list stack detection reads (normalised, safe, de-duplicated, capped).</summary>
public sealed class RepoFilesTests
{
    [Fact]
    public void FR118_Paths_are_normalised_unsafe_ones_dropped_and_duplicates_collapsed_first_wins()
    {
        var (files, truncated) = RepoFiles.ByNormalisedPath(
            [("frontend/package.json", 1), ("./frontend/package.json", 2), ("frontend\\package.json", 3), ("../etc/passwd", 4), ("/abs/file", 5), ("", 6), ("backend/api.csproj", 7)],
            maxFiles: 10);

        Assert.False(truncated);
        Assert.Equal(["frontend/package.json", "backend/api.csproj"], files.Keys);
        Assert.Equal(1, files["frontend/package.json"]);
    }

    [Fact]
    public void FR118_The_list_is_capped_and_says_so()
    {
        var (files, truncated) = RepoFiles.ByNormalisedPath([("a", 1), ("a", 2), ("b", 3), ("c", 4)], maxFiles: 2);

        Assert.True(truncated);
        Assert.Equal(["a", "b"], files.Keys);
        Assert.False(RepoFiles.ByNormalisedPath([("a", 1), ("b", 2)], maxFiles: 2).Truncated);
    }
}
