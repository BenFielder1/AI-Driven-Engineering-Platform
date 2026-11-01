using Aide.Domain.Common;
using Aide.Domain.GitHosts;

namespace Aide.UnitTests.GitHosts;

/// <summary>FR-130–FR-132: GitLab versions, PKCE, scopes, project-creation levels, the create-project error map and validation.</summary>
public class GitLabRulesTests
{
    [Theory]
    [InlineData("17.11.0", GitLabVersionVerdict.Supported)]
    [InlineData("17.11.2-ee", GitLabVersionVerdict.Supported)]
    [InlineData("18.0.1", GitLabVersionVerdict.Supported)]
    [InlineData("17.10.9", GitLabVersionVerdict.Old)]
    [InlineData("16.11.0-ee", GitLabVersionVerdict.Old)]
    [InlineData(null, GitLabVersionVerdict.Unknown)]
    [InlineData("", GitLabVersionVerdict.Unknown)]
    [InlineData("nightly", GitLabVersionVerdict.Unknown)]
    public void FR130_Versions_from_17_11_are_supported_and_older_ones_warn(string? version, GitLabVersionVerdict expected)
    {
        Assert.Equal(expected, GitLabRules.CheckVersion(version));
        Assert.Equal(expected == GitLabVersionVerdict.Old, GitLabRules.VersionWarning(version) is not null);
    }

    [Fact]
    public void FR130_The_version_warning_names_the_version_and_the_minimum()
    {
        var warning = GitLabRules.VersionWarning("17.4.1");

        Assert.NotNull(warning);
        Assert.Contains("17.4.1", warning, StringComparison.Ordinal);
        Assert.Contains("17.11", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void FR131_PKCE_S256_challenge_matches_the_RFC_7636_example()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        Assert.True(GitLabRules.IsValidPkceVerifier(verifier));
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", GitLabRules.PkceChallenge(verifier));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has spaces in it that are not allowed in a verifier string at all")]
    [InlineData(null)]
    public void FR131_Malformed_PKCE_verifiers_are_rejected(string? verifier) => Assert.False(GitLabRules.IsValidPkceVerifier(verifier));

    [Fact]
    public void FR131_The_OAuth_scope_parameter_is_api_write_repository_openid_profile_email()
    {
        Assert.Equal("api write_repository openid profile email", GitLabRules.OauthScopeParameter);
        Assert.Equal(["api", "write_repository", "openid"], GitLabRules.ParseScopes(" api  write_repository openid api "));
        Assert.Empty(GitLabRules.ParseScopes(null));
    }

    [Fact]
    public void FR131_A_token_missing_write_repository_is_CONNECTION_SCOPE_INSUFFICIENT_listing_it()
    {
        var result = GitLabRules.CheckTokenScopes(["api", "read_user"]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ConnectionScopeInsufficient, result.Error!.Code);
        Assert.Equal(["write_repository"], result.Error.Details);
        Assert.Contains("write_repository", result.Error.Message, StringComparison.Ordinal);
        Assert.True(GitLabRules.CheckTokenScopes(["write_repository", "api", "read_api"]).IsSuccess);
        Assert.Equal(["api", "write_repository"], GitLabRules.CheckTokenScopes([]).Error!.Details);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("glpat abc")]
    public void FR131_Blank_or_spaced_tokens_are_VALIDATION(string? token)
    {
        var result = GitLabRules.ValidateToken(token);

        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
        Assert.StartsWith("token: ", Assert.Single(result.Error!.Details), StringComparison.Ordinal);
    }

    [Fact]
    public void FR131_A_token_over_1000_characters_is_VALIDATION() =>
        Assert.True(GitLabRules.ValidateToken(new string('a', 1001)).IsFailure);

    [Theory]
    [InlineData("developer", 30, true)]
    [InlineData("developer", 40, true)]
    [InlineData("maintainer", 30, false)]
    [InlineData("maintainer", 40, true)]
    [InlineData(null, 30, false)]
    [InlineData(null, 40, true)]
    [InlineData("owner", 40, false)]
    [InlineData("owner", 50, true)]
    [InlineData("noone", 50, false)]
    [InlineData("administrator", 50, false)]
    public void FR132_Project_creation_follows_the_groups_project_creation_level(string? level, int access, bool allowed)
    {
        Assert.Equal(allowed, GitLabRules.CanCreateProjectIn(level, access));
        Assert.Equal(allowed, GitLabRules.CannotCreateReason(level, access) is null);
    }

    [Theory]
    [InlineData(400, "name has already been taken; path has already been taken", ErrorCodes.RepoNameTaken)]
    [InlineData(400, "path has already been taken", ErrorCodes.RepoNameTaken)]
    [InlineData(403, "403 Forbidden", ErrorCodes.Forbidden)]
    [InlineData(429, "Retry later", ErrorCodes.UpstreamRateLimited)]
    [InlineData(400, "This endpoint has been requested too many times. Try again later.", ErrorCodes.UpstreamRateLimited)]
    [InlineData(400, "name can contain only letters, digits", ErrorCodes.Validation)]
    [InlineData(500, null, ErrorCodes.UpstreamError)]
    public void FR132_Create_project_failures_map_to_the_spec_codes(int status, string? message, string expected) =>
        Assert.Equal(expected, GitLabRules.CreateProjectErrorCode(status, message));

    [Theory]
    [InlineData("todo-app", null, null, true)]
    [InlineData("Todo App+", "todo-app", "A todo app", true)]
    [InlineData("", null, null, false)]
    [InlineData("-bad", null, null, false)]
    [InlineData("todo", "todo.git", null, false)]
    [InlineData("todo", "-todo", null, false)]
    [InlineData("todo", "to do", null, false)]
    public void FR132_New_project_names_and_paths_follow_GitLab_rules(string name, string? path, string? description, bool valid)
    {
        var result = GitLabRules.ValidateNewProject(name, path, description);

        Assert.Equal(valid, result.IsSuccess);
        if (!valid)
        {
            Assert.Equal(ErrorCodes.Validation, result.Error!.Code);
        }
    }

    [Fact]
    public void FR130_Instance_names_are_1_to_200_characters()
    {
        Assert.True(GitLabRules.ValidateInstanceName(" Acme GitLab ").IsSuccess);
        Assert.True(GitLabRules.ValidateInstanceName("  ").IsFailure);
        Assert.True(GitLabRules.ValidateInstanceName(new string('x', 201)).IsFailure);
    }
}

/// <summary>FR-131, D-V4-18: the GitLab callback's account rules: match by identity, never link silently by email.</summary>
public class GitSignInRulesTests
{
    private static readonly Guid Ana = Guid.CreateVersion7();
    private static readonly Guid Ben = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FR131_A_first_sign_in_with_an_unknown_verified_email_creates_an_account()
    {
        var decision = GitSignInRules.Decide(GitOAuthMode.Signin, null, null, null, "new@example.com", null);

        Assert.Equal(new GitSignInDecision(GitSignInAction.CreateAccount, null, null), decision);
    }

    [Fact]
    public void FR131_A_linked_identity_signs_in_as_its_account()
    {
        Assert.Equal(GitSignInAction.SignInLinked, GitSignInRules.Decide(GitOAuthMode.Signin, null, null, Ana, "ana@example.com", null).Action);
        Assert.Equal(Ana, GitSignInRules.Decide(GitOAuthMode.Signin, null, Ben, Ana, null, null).UserId);
    }

    [Fact]
    public void FR131_D_V4_18_An_email_match_is_never_linked_silently_it_needs_confirmation()
    {
        var decision = GitSignInRules.Decide(GitOAuthMode.Signin, null, null, null, "ana@example.com", Ana);

        Assert.Equal(GitSignInAction.ConfirmEmailLink, decision.Action);
        Assert.Equal(Ana, decision.UserId);
    }

    [Fact]
    public void FR131_Without_a_verified_email_a_new_identity_is_refused()
    {
        Assert.Equal(GitSignInRules.EmailRequired, GitSignInRules.Decide(GitOAuthMode.Signin, null, null, null, null, null).Reason);
        Assert.Equal(GitSignInRules.EmailRequired, GitSignInRules.Decide(GitOAuthMode.Signin, null, null, null, " ", null).Reason);
    }

    [Fact]
    public void FR131_Signing_in_while_signed_in_links_the_new_identity_to_the_current_user()
    {
        var decision = GitSignInRules.Decide(GitOAuthMode.Signin, null, Ben, null, "other@example.com", Ana);

        Assert.Equal(new GitSignInDecision(GitSignInAction.LinkToCurrentUser, Ben, null), decision);
    }

    [Fact]
    public void FR131_Connect_links_to_the_user_who_started_it_and_refuses_another_session_or_owner()
    {
        Assert.Equal(GitSignInAction.LinkToCurrentUser, GitSignInRules.Decide(GitOAuthMode.Connect, Ana, Ana, null, null, null).Action);
        Assert.Equal(GitSignInAction.LinkToCurrentUser, GitSignInRules.Decide(GitOAuthMode.Connect, Ana, Ana, Ana, null, null).Action);
        Assert.Equal(GitSignInRules.LinkSession, GitSignInRules.Decide(GitOAuthMode.Connect, Ana, Ben, null, null, null).Reason);
        Assert.Equal(GitSignInRules.LinkSession, GitSignInRules.Decide(GitOAuthMode.Connect, Ana, null, null, null, null).Reason);
        Assert.Equal(GitSignInRules.LinkSession, GitSignInRules.Decide(GitOAuthMode.Connect, null, Ana, null, null, null).Reason);
        Assert.Equal(GitSignInRules.AlreadyLinked, GitSignInRules.Decide(GitOAuthMode.Connect, Ana, Ana, Ben, null, null).Reason);
    }

    [Fact]
    public void FR131_Only_the_offered_account_confirms_an_unexpired_link_to_a_still_unlinked_identity()
    {
        var expires = Now.AddMinutes(5);

        Assert.True(GitSignInRules.CanConfirmEmailLink(Ana, Ana, expires, Now, null));
        Assert.True(GitSignInRules.CanConfirmEmailLink(Ana, Ana, expires, Now, Ana));
        Assert.False(GitSignInRules.CanConfirmEmailLink(Ana, Ben, expires, Now, null));
        Assert.False(GitSignInRules.CanConfirmEmailLink(Ana, null, expires, Now, null));
        Assert.False(GitSignInRules.CanConfirmEmailLink(Ana, Ana, Now, Now, null));
        Assert.False(GitSignInRules.CanConfirmEmailLink(Ana, Ana, expires, Now, Ben));
    }
}
