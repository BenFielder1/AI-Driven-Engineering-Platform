using Aide.Domain.GitHosts;

namespace Aide.UnitTests.GitHosts;

/// <summary>FR-140: the host named in release activity text.</summary>
public class GitHostNamesTests
{
    [Theory]
    [InlineData("gitlab", "GitLab")]
    [InlineData("bitbucket", "Bitbucket")]
    [InlineData("github_app", "GitHub")]
    [InlineData("github_pat", "GitHub")]
    [InlineData("local", "the local repository")]
    [InlineData(null, "GitHub")]
    public void FR140_The_release_activity_names_the_projects_Git_host(string? provider, string expected) =>
        Assert.Equal(expected, GitHostNames.Display(provider));
}
