using System.Text;
using Aide.Domain.Common;
using Aide.Domain.Entities;
using Aide.Domain.GitHosts;
using Aide.Domain.Notifications;
using Aide.Domain.Projects;
using Aide.Infrastructure.GitLab;

namespace Aide.UnitTests.GitHosts;

/// <summary>FR-132 (binding), FR-136, FR-137, FR-141: the pure rules of a project on a GitLab repository.</summary>
public class GitLabProjectRulesTests
{
    private static readonly Guid Instance = Guid.CreateVersion7();
    private static readonly Guid Connection = Guid.CreateVersion7();

    [Fact]
    public void FR132_Create_with_gitlab_needs_instance_connection_repo_id_and_path()
    {
        Assert.True(GitLabProjectRules.ValidateCreate(Instance, Connection, "42", "acme/web/todo-app", null, null, null, "git.").IsSuccess);

        var missing = GitLabProjectRules.ValidateCreate(null, null, null, null, null, null, null, "git.");
        Assert.Equal(ErrorCodes.Validation, missing.Error!.Code);
        Assert.Equal(
            ["git.instanceId", "git.connectionId", "git.repoId", "git.repoFullName"],
            missing.Error.Details.Select(d => d[..d.IndexOf(':', StringComparison.Ordinal)]));
    }

    [Fact]
    public void FR132_Create_with_gitlab_refuses_the_GitHub_fields()
    {
        var result = GitLabProjectRules.ValidateCreate(Instance, Connection, "42", "acme/todo", "https://gitlab.com/acme/todo", "glpat-x", 7, "git.");
        Assert.Equal(["git.repoUrl", "git.token", "git.installationId"], result.Error!.Details.Select(d => d[..d.IndexOf(':', StringComparison.Ordinal)]));
    }

    [Fact]
    public void FR132_GitLab_fields_are_refused_for_other_modes()
    {
        Assert.True(GitLabProjectRules.ValidateNotSent(null, null, null).IsSuccess);
        Assert.Equal(3, GitLabProjectRules.ValidateNotSent(Instance, Connection, "42", "git.").Error!.Details.Count);
    }

    [Theory]
    [InlineData("42", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("4a", false)]
    [InlineData("", false)]
    [InlineData("123456789012345678901", false)]
    public void FR132_Repo_id_is_GitLabs_numeric_project_id(string id, bool valid) =>
        Assert.Equal(valid, GitLabProjectRules.IsValidRepoId(id));

    [Theory]
    [InlineData("acme/todo-app", true)]
    [InlineData("acme/web/sub/todo.app", true)]
    [InlineData("todo", false)]
    [InlineData("acme//todo", false)]
    [InlineData("acme/todo.git", false)]
    [InlineData("acme/-todo", false)]
    [InlineData("acme/to do", false)]
    public void FR136_Full_path_is_group_sub_project(string path, bool valid) =>
        Assert.Equal(valid, GitLabProjectRules.IsValidFullPath(path));

    [Theory]
    [InlineData(30, null, true)]
    [InlineData(null, 40, true)]
    [InlineData(20, 30, true)]
    [InlineData(20, 10, false)]
    [InlineData(null, null, false)]
    public void FR136_Binding_needs_Developer_access_from_project_or_group_membership(int? project, int? group, bool allowed) =>
        Assert.Equal(allowed, GitLabProjectRules.HasDeveloperAccess(project, group));

    [Fact]
    public void FR137_Merge_request_opens_as_draft_and_is_ready_without_the_prefix()
    {
        Assert.Equal("Draft: Todo app: 3 steps", GitLabProjectRules.DraftTitle("Todo app: 3 steps"));
        Assert.Equal("Draft: Todo", GitLabProjectRules.DraftTitle("Draft: Todo"));
        Assert.Equal("Draft: Todo", GitLabProjectRules.DraftTitle("[Draft] Todo"));
        Assert.Equal("Todo", GitLabProjectRules.ReadyTitle("Draft: Todo"));
        Assert.Equal("Todo", GitLabProjectRules.ReadyTitle("draft: (Draft) Todo"));
        Assert.Equal("Todo", GitLabProjectRules.ReadyTitle("Todo"));
        Assert.True(GitLabProjectRules.IsDraftTitle("DRAFT: x"));
        Assert.False(GitLabProjectRules.IsDraftTitle("Drafting the API"));
    }

    [Fact]
    public void FR137_A_reused_merge_request_keeps_its_draft_state()
    {
        Assert.Equal("Draft: New title", GitLabProjectRules.ReusedTitle("Draft: Old", "New title"));
        Assert.Equal("New title", GitLabProjectRules.ReusedTitle("Old", "New title"));
    }

    [Fact]
    public void FR141_Given_tags_v1_0_0_and_v1_1_0_then_v1_1_0_is_preselected_and_1_2_0_suggested()
    {
        var suggestion = GitLabProjectRules.SuggestRelease([new("v1.0.0", false, false), new("v1.1.0", false, false), new("nightly", false, false)]);

        Assert.Equal("v1.1.0", suggestion.PreselectedTag);
        Assert.Equal("1.2.0", suggestion.DevelopmentVersion.ToString());
    }

    [Fact]
    public void FR141_The_latest_release_wins_over_a_higher_tag_then_the_highest_release()
    {
        Assert.Equal("v1.0.0", GitLabProjectRules.SuggestRelease([new("v1.0.0", true, true), new("v0.9.0", true, false), new("v2.0.0", false, false)]).PreselectedTag);
        Assert.Equal("v1.3.0", GitLabProjectRules.SuggestRelease([new("v1.3.0", true, false), new("v1.2.0", true, false), new("v2.0.0", false, false)]).PreselectedTag);
    }

    [Fact]
    public void FR141_No_semver_ref_means_never_released_and_0_1_0()
    {
        var suggestion = GitLabProjectRules.SuggestRelease([new("latest", false, false)]);
        Assert.Null(suggestion.PreselectedTag);
        Assert.Equal("0.1.0", suggestion.DevelopmentVersion.ToString());
    }

    [Fact]
    public void FR136_R5_A_GitLab_repo_pins_vcs_gitlab_with_its_instance_and_self_managed_flag()
    {
        var host = Aide.Domain.Stack.StackPinning.RepoHost(GitProvider.Gitlab, Instance, selfManaged: true);
        Assert.Equal(new Aide.Domain.Stack.StackVcsPick("vcs-gitlab", Instance, true), host);
        Assert.Equal(new Aide.Domain.Stack.StackVcsPick("vcs-github"), Aide.Domain.Stack.StackPinning.RepoHost(GitProvider.GithubApp, Instance, selfManaged: true));
        Assert.Null(Aide.Domain.Stack.StackPinning.RepoHost(GitProvider.Local, null, false));

        var omitted = Aide.Domain.Stack.StackPinning.FixVcs(new Aide.Domain.Stack.StackSelection(), host);
        Assert.Equal(host, omitted.Value.Vcs);
        var clientSent = Aide.Domain.Stack.StackPinning.FixVcs(new Aide.Domain.Stack.StackSelection { Vcs = new("vcs-gitlab", null, false) }, host);
        Assert.Equal(host, clientSent.Value.Vcs);
        Assert.Equal(ErrorCodes.VcsMismatch, Aide.Domain.Stack.StackPinning.FixVcs(new Aide.Domain.Stack.StackSelection { Vcs = new("vcs-github") }, host).Error!.Code);
    }

    [Fact]
    public void FR136_Web_url_is_the_instance_base_and_the_path() =>
        Assert.Equal("https://gitlab.acme.example/acme/web/todo", GitLabProjectRules.ProjectWebUrl(new Uri("https://gitlab.acme.example/"), "acme/web/todo"));

    [Fact]
    public void FR136_Settings_can_not_move_a_project_onto_gitlab_or_bitbucket()
    {
        Assert.True(ProjectSettingsRules.ValidateProviderChange(GitProvider.Gitlab, GitProvider.Gitlab).IsSuccess);
        Assert.True(ProjectSettingsRules.ValidateProviderChange(GitProvider.Gitlab, GitProvider.Local).IsSuccess);
        foreach (var next in new[] { GitProvider.Gitlab, GitProvider.Bitbucket })
        {
            var refused = ProjectSettingsRules.ValidateProviderChange(GitProvider.Local, next);
            Assert.Equal(ErrorCodes.Validation, refused.Error!.Code);
            Assert.StartsWith("provider:", refused.Error.Details.Single(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FR136_A_token_is_never_saved_on_a_GitLab_project_and_its_web_url_is_the_stored_one()
    {
        Assert.Contains(ProjectSettingsRules.ValidateGit(GitProvider.Gitlab, null, "main", "glpat-x").Error!.Details, d => d.StartsWith("token:", StringComparison.Ordinal));
        Assert.Equal("https://gitlab.com/acme/todo", ProjectSettingsRules.RepoWebUrl(GitProvider.Gitlab, "https://gitlab.com/acme/todo", "acme/todo"));
    }

    [Fact]
    public void FR136_Git_carries_the_token_only_in_the_Basic_oauth2_extraHeader()
    {
        const string token = "glpat-secret-123";
        var remote = GitLabRemotes.Create(new Uri("https://gitlab.acme.example"), false, "acme/web/todo", token, new GitLabOptions(), [System.Net.IPAddress.Parse("20.0.0.10")]);

        Assert.Equal("https://gitlab.acme.example/acme/web/todo.git", remote.Url);
        Assert.DoesNotContain(token, remote.Url, StringComparison.Ordinal);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"oauth2:{token}"));
        Assert.Contains(remote.ConfigEnvironment, kv => kv.Value == $"Authorization: Basic {basic}");
        Assert.Contains(remote.ConfigEnvironment, kv => kv.Value == "http.https://gitlab.acme.example/.extraHeader");
        Assert.DoesNotContain(remote.ConfigEnvironment.Values, v => v == "protocol.http.allow");
        Assert.Equal("push *** to ***", remote.Redact($"push {token} to {basic}"));
    }

    [Fact]
    public void FR136_Git_is_pinned_to_the_vetted_addresses_without_redirects_or_proxy()
    {
        var addresses = new[] { System.Net.IPAddress.Parse("20.0.0.10"), System.Net.IPAddress.Parse("2001:db8::7") };
        var remote = GitLabRemotes.Create(new Uri("https://gitlab.acme.example"), false, "acme/todo", "glpat-x", new GitLabOptions(), addresses);
        var config = Config(remote);

        Assert.Equal("gitlab.acme.example:443:20.0.0.10,[2001:db8::7]", config["http.curloptResolve"]);
        Assert.Equal("false", config["http.followRedirects"]);
        Assert.Equal(string.Empty, config["http.proxy"]);
        Assert.Equal("never", config["protocol.allow"]);
        Assert.Equal("gitlab.lab.example:8080:10.0.4.7", GitLabRemotes.CurlResolve(new Uri("http://gitlab.lab.example:8080"), [System.Net.IPAddress.Parse("10.0.4.7")]));

        // Without the guard's addresses there is no remote (only the tests-only override skips the pin).
        Assert.Throws<ArgumentException>(() => GitLabRemotes.Create(new Uri("https://gitlab.acme.example"), false, "acme/todo", "glpat-x", new GitLabOptions()));
        var overridden = Config(GitLabRemotes.Create(
            new Uri("https://gitlab.acme.example"), false, "acme/todo", "glpat-x", new GitLabOptions { GitBaseUrlOverride = "file:///tmp/remotes/", AllowInsecureGitBaseUrl = true }));
        Assert.False(overridden.ContainsKey("http.curloptResolve"));
        Assert.Equal("false", overridden["http.followRedirects"]);
    }

    [Theory]
    [InlineData(GitConnectionStatus.Valid, GitConnectionStatus.Invalid, true)]
    [InlineData(GitConnectionStatus.Valid, GitConnectionStatus.InsufficientScope, true)]
    [InlineData(GitConnectionStatus.Valid, GitConnectionStatus.Valid, false)]
    [InlineData(GitConnectionStatus.Invalid, GitConnectionStatus.Invalid, false)]
    [InlineData(GitConnectionStatus.InsufficientScope, GitConnectionStatus.Valid, false)]
    public void FR136_A_connection_that_stops_being_valid_clears_its_bindings(GitConnectionStatus previous, GitConnectionStatus next, bool clears) =>
        Assert.Equal(clears, GitBindingRules.StatusChangeClearsBinding(previous, next));

    /// <summary>The per-command git configuration (<c>GIT_CONFIG_KEY_n</c> → <c>GIT_CONFIG_VALUE_n</c>).</summary>
    private static Dictionary<string, string> Config(Aide.Infrastructure.Git.GitRemote remote)
    {
        var count = int.Parse(remote.ConfigEnvironment["GIT_CONFIG_COUNT"], System.Globalization.CultureInfo.InvariantCulture);
        return Enumerable.Range(0, count).ToDictionary(
            i => remote.ConfigEnvironment[$"GIT_CONFIG_KEY_{i}"],
            i => remote.ConfigEnvironment[$"GIT_CONFIG_VALUE_{i}"],
            StringComparer.Ordinal);
    }

    [Fact]
    public void FR136_Git_auth_and_rate_limit_failures_are_recognised()
    {
        Assert.True(GitLabGitErrors.IsUnauthorized("fatal: Authentication failed for 'https://gitlab.com/acme/todo.git/'"));
        Assert.True(GitLabGitErrors.IsUnauthorized("The requested URL returned error: 401"));
        Assert.False(GitLabGitErrors.IsUnauthorized("The requested URL returned error: 404"));
        Assert.True(GitLabGitErrors.IsRateLimited("The requested URL returned error: 429"));
    }

    [Fact]
    public void FR136_git_binding_cleared_notifies_Owners_and_links_to_Git_settings()
    {
        var project = Guid.CreateVersion7();
        Assert.Equal(NotificationType.GitBindingCleared, NotificationRules.ForEvent(EventTypes.GitBindingCleared));
        Assert.Equal(RecipientScope.Owners, NotificationRules.Recipients(NotificationType.GitBindingCleared));
        Assert.Equal("git_binding_cleared", NotificationType.GitBindingCleared.ToWire());
        Assert.Contains(NotificationType.GitBindingCleared, NotificationRules.ChannelTypes);
        Assert.Equal($"/projects/{project}/settings/git", NotificationRules.LinkPath(NotificationType.GitBindingCleared, project));

        var text = NotificationMessages.Compose(new NotificationFacts(NotificationType.GitBindingCleared, "Todo") { Provider = "gitlab", Reason = "member_removed" });
        Assert.Equal("Todo: GitLab connection unbound", text.Title);
        Assert.Contains("the Owner who bound it left the project", text.Body, StringComparison.Ordinal);
        Assert.Equal(["owner_role_lost", "member_removed", "connection_deleted", "connection_invalid"], EnumStrings.Values<GitBindingClearedReason>());
    }

    [Fact]
    public void FR136_GITLAB_RATE_LIMITED_is_a_job_code_only() =>
        Assert.Null(ErrorCodes.StatusCodeFor(ErrorCodes.GitLabRateLimited));

    [Fact]
    public void FR136_Unbound_project_git_jobs_fail_GIT_NOT_CONNECTED() =>
        Assert.Equal(ErrorCodes.GitNotConnected, GitBindingRules.CanRunGitJob(null, true, "GitLab").Error!.Code);

    [Fact]
    public void FR136_Connection_settings_carry_the_binding_only_for_GitLab()
    {
        var settings = new ProjectGitSettings
        {
            ProjectId = Guid.CreateVersion7(),
            Provider = GitProvider.Gitlab,
            InstanceId = Instance,
            GitConnectionId = Connection,
            ExternalRepoId = "42",
            RepoFullName = "acme/todo",
            RepoUrl = "https://gitlab.com/acme/todo",
        };
        var connection = Aide.Infrastructure.Git.GitConnectionSettings.From(settings);
        Assert.Equal((Instance, Connection, "42", "acme/todo", "https://gitlab.com/acme/todo"), (connection.InstanceId!.Value, connection.GitConnectionId!.Value, connection.ExternalRepoId, connection.RepoFullName, connection.RepoUrl));

        settings.Provider = GitProvider.Local;
        Assert.Null(Aide.Infrastructure.Git.GitConnectionSettings.From(settings).GitConnectionId);
    }
}
