using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aide.Domain.Entities;
using Aide.Domain.GitHub;
using Aide.Domain.Jobs;
using Aide.Domain.Projects;
using Aide.Infrastructure.GitHub;

namespace Aide.UnitTests.GitHub;

/// <summary>FR-80–FR-83, FR-86, §4.14: the GitHub App rules — manifest, tokens and their caching, rate limits, the bot
/// identity, repository emptiness, owners and tags.</summary>
public class GitHubAppRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- FR-80 manifest

    [Fact]
    public void S4_14_The_manifest_asks_for_the_spec_permissions_and_email_read()
    {
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["contents"] = "write",
                ["pull_requests"] = "write",
                ["workflows"] = "write",
                ["administration"] = "write",
                ["metadata"] = "read",
                ["checks"] = "read",
                ["statuses"] = "read",
                ["emails"] = "read",
            },
            GitHubRules.ManifestPermissions);
    }

    [Theory]
    [InlineData("localhost", "AIDE (localhost)")]
    [InlineData("aide.internal.example.com", "AIDE (aide.internal.example.com)")]
    [InlineData("a-very-long-host-name.example.internal", "AIDE (a-very-long-host-name.examp)")]
    public void FR80_The_App_is_named_after_the_public_host_within_GitHubs_34_characters(string host, string expected)
    {
        var name = GitHubRules.ManifestAppName(host);

        Assert.Equal(expected, name);
        Assert.True(name.Length <= GitHubRules.MaxAppNameLength);
    }

    [Fact]
    public void FR80_The_install_and_manage_links_are_on_github_com()
    {
        Assert.Equal("https://github.com/apps/aide-local/installations/new", GitHubRules.InstallUrl("aide-local"));
        Assert.Equal("https://github.com/settings/installations/7", GitHubRules.ManageInstallationUrl(7, "octo", GitHubOwnerType.User));
        Assert.Equal("https://github.com/organizations/acme/settings/installations/8", GitHubRules.ManageInstallationUrl(8, "acme", GitHubOwnerType.Organization));
    }

    [Theory]
    [InlineData("write", true)]
    [InlineData("read", false)]
    [InlineData(null, false)]
    public void S4_14_Create_repository_needs_Administration_write(string? administration, bool expected)
    {
        var permissions = new Dictionary<string, string> { ["contents"] = "write" };
        if (administration is not null)
        {
            permissions["administration"] = administration;
        }

        Assert.Equal(expected, GitHubRules.CanCreateRepositories(permissions));
    }

    // ---------------------------------------------------------------- FR-82, §4.14 tokens

    [Fact]
    public void S4_14_The_App_JWT_is_RS256_issued_a_minute_back_expiring_within_10_minutes_with_the_App_id_as_issuer()
    {
        using var key = RSA.Create(2048);

        var jwt = GitHubAppJwt.Create(424242, key.ExportRSAPrivateKeyPem(), Now);

        var parts = jwt.Split('.');
        using var header = JsonDocument.Parse(Decode(parts[0]));
        using var payload = JsonDocument.Parse(Decode(parts[1]));
        Assert.Equal(("RS256", "JWT"), (header.RootElement.GetProperty("alg").GetString(), header.RootElement.GetProperty("typ").GetString()));
        Assert.Equal(Now.AddSeconds(-60).ToUnixTimeSeconds(), payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(Now.AddMinutes(9).ToUnixTimeSeconds(), payload.RootElement.GetProperty("exp").GetInt64());
        Assert.True(payload.RootElement.GetProperty("exp").GetInt64() - Now.ToUnixTimeSeconds() <= 600);
        Assert.Equal("424242", payload.RootElement.GetProperty("iss").GetString());
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void S4_14_A_PKCS8_key_from_GitHub_and_a_one_line_env_key_both_work()
    {
        using var key = RSA.Create(2048);
        var options = new GitHubAppOptions { PrivateKey = key.ExportPkcs8PrivateKeyPem().ReplaceLineEndings("\\n") };

        Assert.True(GitHubAppJwt.IsReadablePrivateKey(options.PrivateKeyPem!));
        Assert.False(GitHubAppJwt.IsReadablePrivateKey("not a key"));
    }

    [Fact]
    public void S4_14_Installation_tokens_are_used_for_at_most_55_minutes_and_never_in_their_last_minute()
    {
        Assert.Equal(Now.AddMinutes(55), GitHubRules.InstallationTokenUsableUntil(Now, Now.AddHours(1)));
        Assert.Equal(Now.AddMinutes(55), GitHubRules.InstallationTokenUsableUntil(Now, null));
        Assert.Equal(Now.AddMinutes(29), GitHubRules.InstallationTokenUsableUntil(Now, Now.AddMinutes(30)));
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(6, false)]
    [InlineData(5, true)]
    [InlineData(-1, true)]
    public void FR81_A_user_token_is_refreshed_within_5_minutes_of_its_8_hour_expiry(int minutesLeft, bool refresh) =>
        Assert.Equal(refresh, GitHubRules.NeedsRefresh(Now.AddMinutes(minutesLeft), Now));

    [Fact]
    public void FR81_A_user_token_without_an_expiry_is_never_refreshed() => Assert.False(GitHubRules.NeedsRefresh(null, Now));

    [Fact]
    public void FR81_The_token_response_gives_both_tokens_and_their_expiries()
    {
        using var json = JsonDocument.Parse("""{"access_token":"ghu_a","expires_in":28800,"refresh_token":"ghr_b","refresh_token_expires_in":15811200,"token_type":"bearer"}""");

        var tokens = GitHubUserTokens.FromTokenResponse(json.RootElement, Now)!;

        Assert.Equal(("ghu_a", Now.AddHours(8), "ghr_b", Now.AddSeconds(15811200)), (tokens.AccessToken, tokens.AccessExpiresAt, tokens.RefreshToken, tokens.RefreshExpiresAt));
        Assert.DoesNotContain("ghu_a", tokens.ToString(), StringComparison.Ordinal);
        using var error = JsonDocument.Parse("""{"error":"bad_refresh_token"}""");
        Assert.Null(GitHubUserTokens.FromTokenResponse(error.RootElement, Now));
    }

    [Fact]
    public void FR82_Commits_are_attributed_to_the_App_bot_with_its_noreply_email()
    {
        Assert.Equal("aide-local[bot]", GitHubRules.BotLogin("aide-local"));
        Assert.Equal("9001+aide-local[bot]@users.noreply.github.com", GitHubRules.BotEmail(9001, "aide-local"));
    }

    // ---------------------------------------------------------------- §4.14 rate limits

    [Fact]
    public void S4_14_A_403_with_no_requests_remaining_waits_until_the_reset()
    {
        var reset = Now.AddMinutes(12);

        Assert.Equal(reset, GitHubRules.RateLimitResetAt(403, "0", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), null, Now));
        Assert.Equal(reset, GitHubRules.RateLimitResetAt(429, "0", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), null, Now));
    }

    [Fact]
    public void S4_14_A_secondary_limit_waits_retry_after_seconds()
    {
        Assert.Equal(Now.AddSeconds(90), GitHubRules.RateLimitResetAt(403, "12", null, "90", Now));
        Assert.Equal(Now.AddMinutes(1), GitHubRules.RateLimitResetAt(429, null, null, null, Now));
    }

    [Theory]
    [InlineData(403, "5", null)]
    [InlineData(404, "0", null)]
    [InlineData(500, null, "30")]
    public void S4_14_Other_responses_are_not_rate_limits(int status, string? remaining, string? retryAfter) =>
        Assert.Null(GitHubRules.RateLimitResetAt(status, remaining, "1700000000", retryAfter, Now));

    [Fact]
    public void S4_14_Retry_After_is_whole_seconds_until_the_reset_at_least_one()
    {
        Assert.Equal(121, GitHubRules.RetryAfterSeconds(Now.AddSeconds(120.2), Now));
        Assert.Equal(1, GitHubRules.RetryAfterSeconds(Now.AddSeconds(-5), Now));
    }

    [Fact]
    public void S4_14_NFR_REL_A_rate_limited_job_waits_for_the_reset_between_a_second_and_an_hour()
    {
        Assert.Equal(Now.AddMinutes(7), JobPolicy.DeferUntil(Now, Now.AddMinutes(7)));
        Assert.Equal(Now.AddSeconds(1), JobPolicy.DeferUntil(Now, Now.AddMinutes(-3)));
        Assert.Equal(Now.AddHours(1), JobPolicy.DeferUntil(Now, Now.AddHours(6)));
    }

    // ---------------------------------------------------------------- FR-81, FR-86 repositories

    [Fact]
    public void FR86_A_repository_without_commits_or_with_only_GitHubs_initial_files_has_no_code()
    {
        Assert.True(GitHubRules.IsEmptyRepository(null));
        Assert.True(GitHubRules.IsEmptyRepository([]));
        Assert.True(GitHubRules.IsEmptyRepository(["README.md"]));
        Assert.True(GitHubRules.IsEmptyRepository(["readme.md", "LICENSE", ".gitignore"]));
        Assert.False(GitHubRules.IsEmptyRepository(["README.md", "src"]));
        Assert.False(GitHubRules.IsEmptyRepository(["package.json"]));
    }

    [Theory]
    [InlineData("v2.3.0", "v", "2.3.0")]
    [InlineData("2.3.0", "v", "2.3.0")]
    [InlineData("release-1.0.0-rc.1", "release-", "1.0.0-rc.1")]
    [InlineData("nightly", "v", null)]
    [InlineData("v01.2.0", "v", null)]
    public void FR73_Tags_carry_their_semantic_version_without_the_prefix(string tag, string prefix, string? expected) =>
        Assert.Equal(expected, GitHubRules.TagSemver(tag, prefix));

    [Fact]
    public void FR81_Owners_are_the_user_then_organisations_and_creating_needs_the_App_installed_with_Administration()
    {
        IReadOnlyCollection<GitHubInstallationRef> installations =
            [new(1, "octo", GitHubOwnerType.User), new(2, "Acme", GitHubOwnerType.Organization)];

        var owners = GitHubRules.Owners("octo", installations, ["acme", "globex"], appCanCreateRepositories: true);
        var readOnly = GitHubRules.Owners("octo", installations, [], appCanCreateRepositories: false);

        Assert.Equal(
            [
                new GitHubOwnerOption("octo", GitHubOwnerType.User, 1, true, true),
                new GitHubOwnerOption("Acme", GitHubOwnerType.Organization, 2, true, true),
                new GitHubOwnerOption("globex", GitHubOwnerType.Organization, null, false, false),
            ],
            owners);
        Assert.All(readOnly, o => Assert.False(o.CanCreate));
    }

    [Fact]
    public void FR82_A_github_app_repository_web_url_comes_from_its_full_name()
    {
        Assert.Equal("https://github.com/acme/todo-app", ProjectSettingsRules.RepoWebUrl(GitProvider.GithubApp, null, "acme/todo-app"));
        Assert.Equal("https://github.com/acme/x", ProjectSettingsRules.RepoWebUrl(GitProvider.GithubPat, "https://github.com/acme/x", null));
        Assert.Null(ProjectSettingsRules.RepoWebUrl(GitProvider.Local, null, null));
        Assert.True(ProjectSettingsRules.IsGitHub(GitProvider.GithubApp) && ProjectSettingsRules.IsGitHub(GitProvider.GithubPat));
        Assert.False(ProjectSettingsRules.IsGitHub(GitProvider.Local));
    }

    [Fact]
    public void FR82_PATCH_git_settings_refuses_a_token_for_a_github_app_project()
    {
        var result = ProjectSettingsRules.ValidateGit(GitProvider.GithubApp, null, "main", tokenSent: "ghp_x");

        Assert.True(result.IsFailure);
        Assert.Contains(result.Error!.Details, d => d.StartsWith("token:", StringComparison.Ordinal));
    }

    private static byte[] Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }
}
