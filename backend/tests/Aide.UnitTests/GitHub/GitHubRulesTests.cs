using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Domain.Projects;

namespace Aide.UnitTests.GitHub;

/// <summary>FR-80–FR-83, FR-86, D-V2-7: GitHub sign-in mode, git mode resolution and App repository rules.</summary>
public class GitHubRulesTests
{
    // ---------------------------------------------------------------- D-V2-7 sign-in mode

    [Theory]
    [InlineData(true, true, GitHubSignInMode.App)]
    [InlineData(true, false, GitHubSignInMode.App)]
    [InlineData(false, true, GitHubSignInMode.Oauth)]
    [InlineData(false, false, GitHubSignInMode.None)]
    public void FR80_Sign_in_uses_the_App_once_registered_and_the_v1_OAuth_App_only_without_one(bool app, bool oauth, GitHubSignInMode expected) =>
        Assert.Equal(expected, GitHubRules.SignInMode(app, oauth));

    [Fact]
    public void FR80_Wire_values_match_the_contract()
    {
        Assert.Equal(["not_configured", "configured"], EnumStrings.Values<GitHubAppStatus>());
        Assert.Equal(["database", "configuration"], EnumStrings.Values<GitHubAppSource>());
        Assert.Equal(["none", "oauth", "app"], EnumStrings.Values<GitHubSignInMode>());
        Assert.Equal(["all", "selected"], EnumStrings.Values<GitHubRepositorySelection>());
        Assert.Equal(["github_pat", "local", "github_app", "gitlab", "bitbucket"], EnumStrings.Values<GitProvider>()); // FR-136
    }

    // ---------------------------------------------------------------- FR-81 names

    [Theory]
    [InlineData("todo-app", true)]
    [InlineData("my.repo_x", true)]
    [InlineData(".github", true)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("bad name", false)]
    [InlineData("a/b", false)]
    public void FR81_Repository_names_follow_GitHub_rules(string name, bool valid) =>
        Assert.Equal(valid, Regex.IsMatch(name, GitHubRules.RepoNamePattern));

    [Theory]
    [InlineData("acme", true)]
    [InlineData("a-b-c", true)]
    [InlineData("-acme", false)]
    [InlineData("acme-", false)]
    [InlineData("ac--me", false)]
    [InlineData("ac_me", false)]
    public void FR81_Owner_logins_follow_GitHub_rules(string login, bool valid) =>
        Assert.Equal(valid, Regex.IsMatch(login, GitHubRules.LoginPattern));

    // ---------------------------------------------------------------- FR-86 git mode

    [Theory]
    [InlineData(GitProvider.GithubApp, null, GitProvider.GithubApp)]
    [InlineData(null, GitProvider.GithubPat, GitProvider.GithubPat)]
    [InlineData(GitProvider.Local, GitProvider.Local, GitProvider.Local)]
    public void FR86_Mode_is_resolved_from_mode_or_the_v1_provider_alias(GitProvider? mode, GitProvider? provider, GitProvider expected) =>
        Assert.Equal(expected, ProjectSettingsRules.ResolveGitMode(mode, provider).Value);

    [Theory]
    [InlineData(null, null)]
    [InlineData(GitProvider.Local, GitProvider.GithubPat)]
    public void FR86_Mode_is_required_and_must_agree_with_provider(GitProvider? mode, GitProvider? provider)
    {
        var result = ProjectSettingsRules.ResolveGitMode(mode, provider, "git.");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        Assert.StartsWith("git.mode: ", Assert.Single(result.Error.Details), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- FR-82 App repository

    [Fact]
    public void FR82_An_App_repository_needs_installation_and_owner_slash_name() =>
        Assert.True(ProjectSettingsRules.ValidateAppRepo(42, "acme/todo-app", null, null).IsSuccess);

    [Theory]
    [InlineData(null, "acme/todo", null, null, "installationId")]
    [InlineData(0L, "acme/todo", null, null, "installationId")]
    [InlineData(1L, null, null, null, "repoFullName")]
    [InlineData(1L, "acme", null, null, "repoFullName")]
    [InlineData(1L, "acme/todo/x", null, null, "repoFullName")]
    [InlineData(1L, "-acme/todo", null, null, "repoFullName")]
    [InlineData(1L, "acme/todo", "https://github.com/acme/todo", null, "repoUrl")]
    [InlineData(1L, "acme/todo", null, "ghp_x", "token")]
    public void FR82_Invalid_App_repository_fields_are_reported(long? installationId, string? fullName, string? repoUrl, string? token, string field)
    {
        var result = ProjectSettingsRules.ValidateAppRepo(installationId, fullName, repoUrl, token, "git.");

        Assert.True(result.IsFailure);
        Assert.Contains(result.Error!.Details, d => d.StartsWith($"git.{field}: ", StringComparison.Ordinal));
    }

    [Fact]
    public void FR82_Repo_full_name_is_parsed()
    {
        Assert.True(ProjectSettingsRules.TryParseRepoFullName("acme/todo-app", out var repo));
        Assert.Equal(new GitHubRepo("acme", "todo-app"), repo);
    }

    // ---------------------------------------------------------------- FR-83 switching

    [Theory]
    [InlineData(GitProvider.GithubPat, GitProvider.GithubApp, false)]
    [InlineData(GitProvider.Local, GitProvider.GithubApp, false)]
    [InlineData(GitProvider.GithubApp, GitProvider.GithubApp, true)]
    [InlineData(GitProvider.GithubApp, GitProvider.GithubPat, true)]
    [InlineData(GitProvider.Local, GitProvider.GithubPat, true)]
    [InlineData(GitProvider.GithubPat, GitProvider.Local, true)]
    public void FR83_Only_switch_to_app_moves_a_project_onto_the_App(GitProvider previous, GitProvider next, bool allowed) =>
        Assert.Equal(allowed, ProjectSettingsRules.ValidateProviderChange(previous, next).IsSuccess);
}
