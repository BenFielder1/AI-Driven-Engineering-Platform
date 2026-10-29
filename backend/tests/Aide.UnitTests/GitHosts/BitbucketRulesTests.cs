using Aide.Domain.Common;
using Aide.Domain.GitHosts;

namespace Aide.UnitTests.GitHosts;

/// <summary>FR-133–FR-135, §4.19: the pure Bitbucket Cloud rules.</summary>
public class BitbucketRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, BitbucketScopeProbe> AllProbes(BitbucketScopeProbe value) =>
        GitTokenRules.BitbucketTokenScopes.ToDictionary(s => s, _ => value, StringComparer.Ordinal);

    [Fact]
    public void FR133_The_consumer_permissions_to_tick_are_the_spec_list() =>
        Assert.Equal(["account", "email", "repository:write", "repository:admin", "pullrequest:write", "project", "webhook"], BitbucketRules.ConsumerPermissions);

    [Fact]
    public void FR134_Given_an_API_token_without_write_pullrequest_then_it_is_listed_as_missing()
    {
        var probes = AllProbes(BitbucketScopeProbe.Granted);
        probes["write:pullrequest:bitbucket"] = BitbucketScopeProbe.Missing;

        var verdict = BitbucketRules.EvaluateTokenScopes(null, probes);
        var check = BitbucketRules.CheckTokenScopes(verdict);

        Assert.Equal(["write:pullrequest:bitbucket"], verdict.Missing);
        Assert.Empty(verdict.Unverifiable);
        Assert.Equal(ErrorCodes.ConnectionScopeInsufficient, check.Error!.Code);
        Assert.Equal(["write:pullrequest:bitbucket"], check.Error.Details);
        Assert.Empty(check.Error.UnverifiableScopes);
        Assert.Contains("write:pullrequest:bitbucket", check.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR134_A_scope_list_from_Bitbucket_decides_for_every_scope_at_once()
    {
        var granted = GitTokenRules.BitbucketTokenScopes.Where(s => s is not "write:pullrequest:bitbucket" and not "read:pipeline:bitbucket").ToList();

        var verdict = BitbucketRules.EvaluateTokenScopes(granted, AllProbes(BitbucketScopeProbe.Unverified));

        Assert.Equal(["write:pullrequest:bitbucket", "read:pipeline:bitbucket"], verdict.Missing);
        Assert.Empty(verdict.Unverifiable);
    }

    [Fact]
    public void FR134_A_token_is_saved_only_when_every_scope_is_verified()
    {
        var probes = AllProbes(BitbucketScopeProbe.Granted);
        probes["admin:repository:bitbucket"] = BitbucketScopeProbe.Unverified;
        probes.Remove("read:pipeline:bitbucket");

        var verdict = BitbucketRules.EvaluateTokenScopes(null, probes);
        var check = BitbucketRules.CheckTokenScopes(verdict);

        Assert.Empty(verdict.Missing);
        Assert.Equal(["admin:repository:bitbucket", "read:pipeline:bitbucket"], verdict.Unverifiable);
        Assert.Equal(ErrorCodes.ConnectionScopeInsufficient, check.Error!.Code);
        Assert.Empty(check.Error.Details);
        Assert.Equal(["admin:repository:bitbucket", "read:pipeline:bitbucket"], check.Error.UnverifiableScopes);
        Assert.Contains("Connect with Bitbucket", check.Error.Message, StringComparison.Ordinal);
        Assert.Contains("create a repository in the workspace first", check.Error.Message, StringComparison.Ordinal);
        Assert.True(BitbucketRules.CheckTokenScopes(BitbucketRules.EvaluateTokenScopes(null, AllProbes(BitbucketScopeProbe.Granted))).IsSuccess);
    }

    [Fact]
    public void FR134_An_OAuth_style_scope_list_is_ignored_so_the_scopes_are_probed()
    {
        string[] oauthStyle = ["account", "repository:write", "pullrequest:write"];

        Assert.Null(BitbucketRules.ApiTokenScopes(oauthStyle));
        Assert.Null(BitbucketRules.ApiTokenScopes(["read:user:bitbucket", "repository:write"]));
        Assert.Null(BitbucketRules.ApiTokenScopes([]));
        Assert.Null(BitbucketRules.ApiTokenScopes(null));
        var verdict = BitbucketRules.EvaluateTokenScopes(oauthStyle, AllProbes(BitbucketScopeProbe.Granted));
        Assert.True(verdict.AllVerified);
    }

    [Fact]
    public void FR134_An_API_token_style_scope_list_is_used()
    {
        Assert.Equal(["read:user:bitbucket", "write:pullrequest:bitbucket"], BitbucketRules.ApiTokenScopes([" read:user:bitbucket", "write:pullrequest:bitbucket", "read:user:bitbucket"]));
        var verdict = BitbucketRules.EvaluateTokenScopes(["read:user:bitbucket"], AllProbes(BitbucketScopeProbe.Granted));
        Assert.Equal(GitTokenRules.BitbucketTokenScopes.Skip(1), verdict.Missing);
    }

    [Fact]
    public void FR134_The_write_repository_probe_can_t_create_a_repository()
    {
        Assert.False(BitbucketRules.IsValidSlug(BitbucketRules.ScopeProbeSlug));
        Assert.True(BitbucketRules.IsValidSlug("todo-app"));
        using var body = System.Text.Json.JsonDocument.Parse(BitbucketRules.ScopeProbeCreateBody);
        Assert.NotEqual("git", body.RootElement.GetProperty("scm").GetString());
        Assert.Equal("!", body.RootElement.GetProperty("project").GetProperty("key").GetString());
    }

    [Theory]
    [InlineData(200, false, BitbucketScopeProbe.Granted)]
    [InlineData(400, false, BitbucketScopeProbe.Granted)]
    [InlineData(404, false, BitbucketScopeProbe.Unverified)]
    [InlineData(409, false, BitbucketScopeProbe.Granted)]
    [InlineData(422, false, BitbucketScopeProbe.Granted)]
    [InlineData(403, true, BitbucketScopeProbe.Missing)]
    [InlineData(403, false, BitbucketScopeProbe.Unverified)]
    [InlineData(405, false, BitbucketScopeProbe.Unverified)]
    public void FR134_A_probe_answer_classifies_its_scope(int status, bool scopeError, BitbucketScopeProbe expected) =>
        Assert.Equal(expected, BitbucketRules.ClassifyProbe(status, scopeError));

    [Fact]
    public void FR134_The_token_fallback_needs_a_token_and_the_account_email()
    {
        Assert.True(BitbucketRules.ValidateToken("ATATT3xFfGF0token", "ana@example.com").IsSuccess);

        var none = BitbucketRules.ValidateToken(" ", null);
        Assert.Equal(ErrorCodes.Validation, none.Error!.Code);
        Assert.Contains(none.Error.Details, d => d.StartsWith("token:", StringComparison.Ordinal));
        Assert.Contains(none.Error.Details, d => d.StartsWith("email:", StringComparison.Ordinal));
        Assert.Contains(BitbucketRules.ValidateToken("has space", "ana@example.com").Error!.Details, d => d.StartsWith("token:", StringComparison.Ordinal));
        Assert.Contains(BitbucketRules.ValidateToken("tok", "not-an-email").Error!.Details, d => d.StartsWith("email:", StringComparison.Ordinal));
    }

    [Fact]
    public void FR134_Sign_in_trusts_only_a_confirmed_email_preferring_the_primary()
    {
        Assert.Equal("ana@example.com", BitbucketRules.VerifiedEmail([
            new BitbucketEmail("old@example.com", false, true),
            new BitbucketEmail("ana@example.com", true, true),
        ]));
        Assert.Equal("old@example.com", BitbucketRules.VerifiedEmail([
            new BitbucketEmail("ana@example.com", true, false),
            new BitbucketEmail("old@example.com", false, true),
        ]));
        Assert.Null(BitbucketRules.VerifiedEmail([new BitbucketEmail("ana@example.com", true, false)]));
        Assert.Null(BitbucketRules.VerifiedEmail([]));
    }

    [Fact]
    public void FR134_D_V4_18_Sign_in_matches_the_linked_account_uuid_and_never_silently_by_email()
    {
        var linked = Guid.NewGuid();
        var emailOwner = Guid.NewGuid();

        var known = GitSignInRules.Decide(GitOAuthMode.Signin, null, null, linked, "ana@example.com", null);
        var sameEmail = GitSignInRules.Decide(GitOAuthMode.Signin, null, null, null, "ana@example.com", emailOwner);
        var fresh = GitSignInRules.Decide(GitOAuthMode.Signin, null, null, null, "new@example.com", null);
        var unconfirmed = GitSignInRules.Decide(GitOAuthMode.Signin, null, null, null, null, null);

        Assert.Equal((GitSignInAction.SignInLinked, linked), (known.Action, known.UserId));
        Assert.Equal((GitSignInAction.ConfirmEmailLink, emailOwner), (sameEmail.Action, sameEmail.UserId));
        Assert.Equal(GitSignInAction.CreateAccount, fresh.Action);
        Assert.Equal((GitSignInAction.Fail, GitSignInRules.EmailRequired), (unconfirmed.Action, unconfirmed.Reason));
    }

    [Theory]
    [InlineData("todo-app", "todo-app")]
    [InlineData("Todo App", "todo-app")]
    [InlineData("  My  Great_App.v2!! ", "my-great_app.v2")]
    [InlineData("--.Ünïcode--", "n-code")]
    [InlineData("!!!", "")]
    public void FR135_A_repository_name_becomes_Bitbucket_s_slug(string name, string slug) =>
        Assert.Equal(slug, BitbucketRules.Slug(name));

    [Fact]
    public void FR135_Slugs_are_at_most_62_characters() =>
        Assert.Equal(BitbucketRules.MaxSlugLength, BitbucketRules.Slug(new string('a', 100)).Length);

    [Fact]
    public void FR135_Given_acme_todo_app_in_project_TODO_the_request_is_valid_and_project_is_required()
    {
        Assert.True(BitbucketRules.ValidateNewRepo("acme", "TODO", "todo-app", "A todo app").IsSuccess);

        var noProject = BitbucketRules.ValidateNewRepo("acme", " ", "todo-app", null);
        Assert.Equal(["project: Choose a project. Bitbucket puts every repository in a project."], noProject.Error!.Details);

        var bad = BitbucketRules.ValidateNewRepo("../x", "1BAD", "!!!", new string('d', BitbucketRules.MaxDescriptionLength + 1));
        Assert.Equal(["owner", "project", "name", "description"], bad.Error!.Details.Select(d => d.Split(':')[0]));
    }

    [Fact]
    public void FR135_The_picker_search_is_a_BBQL_name_filter_with_quotes_escaped()
    {
        Assert.Null(BitbucketRules.RepoQuery("  "));
        Assert.Equal("name~\"todo\"", BitbucketRules.RepoQuery(" todo "));
        Assert.Equal("name~\"a\\\"b\\\\c\"", BitbucketRules.RepoQuery("a\"b\\c"));
    }

    [Fact]
    public void FR135_The_picker_cursor_walks_pages_then_workspaces()
    {
        Assert.Equal(new BitbucketRepoCursor(null, 1), BitbucketRules.ParseCursor(null).Value);
        Assert.Equal(new BitbucketRepoCursor("acme-team", 5), BitbucketRules.ParseCursor("acme-team:5").Value);
        Assert.Equal(ErrorCodes.Validation, BitbucketRules.ParseCursor("acme:0").Error!.Code);
        Assert.Equal(ErrorCodes.Validation, BitbucketRules.ParseCursor("../x:1").Error!.Code);
        Assert.Equal(ErrorCodes.Validation, BitbucketRules.ParseCursor("https://api.bitbucket.org/2.0/x").Error!.Code);

        string[] workspaces = ["acme", "beta"];
        Assert.Equal("acme:2", BitbucketRules.NextCursor(workspaces, "acme", 1, hasNext: true));
        Assert.Equal("beta:1", BitbucketRules.NextCursor(workspaces, "acme", 3, hasNext: false));
        Assert.Null(BitbucketRules.NextCursor(workspaces, "beta", 1, hasNext: false));
        Assert.Null(BitbucketRules.NextCursor(workspaces, "gone", 1, hasNext: false));
    }

    [Theory]
    [InlineData(400, "Repository with this Slug and Owner already exists.", ErrorCodes.RepoNameTaken)]
    [InlineData(403, "Forbidden", ErrorCodes.Forbidden)]
    [InlineData(404, "No workspace", ErrorCodes.NotFound)]
    [InlineData(429, null, ErrorCodes.UpstreamRateLimited)]
    [InlineData(400, "project: invalid key", ErrorCodes.Validation)]
    [InlineData(500, null, ErrorCodes.UpstreamError)]
    public void FR135_Create_repository_errors_map_to_AIDE_codes(int status, string? message, string code) =>
        Assert.Equal(code, BitbucketRules.CreateRepoErrorCode(status, message));

    [Fact]
    public void S4_19_Bitbucket_429_keeps_the_bucket_empty_until_Retry_After()
    {
        var bucket = GitHostPollBudget.BucketAfterBitbucketRateLimited(Now, "120");

        var refused = GitHostPollBudget.TryTake(bucket, Now.AddSeconds(60));
        var after = GitHostPollBudget.TryTake(bucket, Now.AddSeconds(125));

        Assert.False(refused.Allowed);
        Assert.True(refused.RetryAt > Now.AddSeconds(120));
        Assert.True(after.Allowed);
        Assert.Equal(Now.AddMinutes(5), GitHostPollBudget.BucketAfterBitbucketRateLimited(Now, null).UpdatedAt);
    }

    [Fact]
    public void S4_19_Request_paths_and_background_work_share_one_900_an_hour_bucket()
    {
        // Every request with the connection takes from the same bucket: 600 picker calls leave 300 for background work.
        var state = GitHostPollBudget.FullBucket(Now);
        for (var i = 0; i < 600; i++)
        {
            state = GitHostPollBudget.TryTake(state, Now).State;
        }

        var background = 0;
        while (GitHostPollBudget.TryTake(state, Now) is { Allowed: true } take)
        {
            state = take.State;
            background++;
        }

        Assert.Equal(300, background);
        Assert.True(GitHostPollBudget.BitbucketBackgroundPerHour < 1_000);
    }
}
