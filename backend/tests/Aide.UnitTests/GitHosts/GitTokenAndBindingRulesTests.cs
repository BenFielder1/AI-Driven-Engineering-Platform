using Aide.Domain.Access;
using Aide.Domain.Common;
using Aide.Domain.GitHosts;
using Aide.Domain.Releases;

namespace Aide.UnitTests.GitHosts;

/// <summary>FR-131, FR-134: GitLab and Bitbucket token rules.</summary>
public class GitTokenRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FR131_PAT_expiry_warning_starts_14_days_before_expiry()
    {
        Assert.False(GitTokenRules.ShouldWarnExpiry(Now.AddDays(15), Now));
        Assert.True(GitTokenRules.ShouldWarnExpiry(Now.AddDays(14), Now));
        Assert.True(GitTokenRules.ShouldWarnExpiry(Now.AddHours(1), Now));
        Assert.False(GitTokenRules.ShouldWarnExpiry(Now, Now));
        Assert.False(GitTokenRules.ShouldWarnExpiry(null, Now));
        Assert.True(GitTokenRules.IsExpired(Now, Now));
        Assert.False(GitTokenRules.IsExpired(null, Now));
    }

    [Fact]
    public void FR131_OAuth_access_tokens_are_refreshed_before_their_2_hour_expiry()
    {
        var expires = GitTokenRules.AccessExpiresAt(Now, null);
        Assert.Equal(Now.AddHours(2), expires);
        Assert.Equal(Now.AddSeconds(7200), GitTokenRules.AccessExpiresAt(Now, 7200));

        Assert.False(GitTokenRules.NeedsRefresh(expires, Now));
        Assert.False(GitTokenRules.NeedsRefresh(expires, expires.AddMinutes(-6)));
        Assert.True(GitTokenRules.NeedsRefresh(expires, expires.AddMinutes(-5)));
        Assert.True(GitTokenRules.NeedsRefresh(expires, expires.AddMinutes(1)));
        Assert.False(GitTokenRules.NeedsRefresh(null, Now));
    }

    [Fact]
    public void FR131_A_401_after_one_refresh_marks_the_connection_invalid()
    {
        Assert.Equal(GitTokenFailureAction.Refresh, GitTokenRules.OnUnauthorized(canRefresh: true, refreshedThisJob: false));
        Assert.Equal(GitTokenFailureAction.MarkInvalid, GitTokenRules.OnUnauthorized(canRefresh: true, refreshedThisJob: true));
        Assert.Equal(GitTokenFailureAction.MarkInvalid, GitTokenRules.OnUnauthorized(canRefresh: false, refreshedThisJob: false));
    }

    [Theory]
    [InlineData(400, "invalid_grant", GitTokenFailureAction.MarkInvalid)]
    [InlineData(401, null, GitTokenFailureAction.MarkInvalid)]
    [InlineData(404, "invalid_grant", GitTokenFailureAction.MarkInvalid)]
    [InlineData(429, null, GitTokenFailureAction.Retry)]
    [InlineData(503, null, GitTokenFailureAction.Retry)]
    [InlineData(null, null, GitTokenFailureAction.Retry)]
    public void FR131_Refresh_failures_invalidate_only_on_a_rejected_grant(int? status, string? error, GitTokenFailureAction expected) =>
        Assert.Equal(expected, GitTokenRules.OnRefreshFailed(status, error));

    [Theory]
    [InlineData(GitCredentialKind.Gitlab, "oauth2")]
    [InlineData(GitCredentialKind.BitbucketOauth, "x-token-auth")]
    [InlineData(GitCredentialKind.BitbucketApiToken, "x-bitbucket-api-token-auth")]
    public void FR136_Git_extra_header_user_per_credential(GitCredentialKind kind, string user) =>
        Assert.Equal(user, GitTokenRules.GitBasicUser(kind));

    [Fact]
    public void FR131_FR134_Missing_token_scopes_are_listed()
    {
        Assert.Equal(["write_repository"], GitTokenRules.MissingGitLabScopes(["api", "read_user"]));
        Assert.Empty(GitTokenRules.MissingGitLabScopes(["api", "write_repository"]));

        var granted = GitTokenRules.BitbucketTokenScopes.Where(s => s != "write:pullrequest:bitbucket");
        Assert.Equal(["write:pullrequest:bitbucket"], GitTokenRules.MissingBitbucketScopes(granted));
    }
}

/// <summary>FR-136: Git binding cleared and re-bound.</summary>
public class GitBindingRulesTests
{
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid OtherOwner = Guid.CreateVersion7();
    private static readonly Guid Instance = Guid.CreateVersion7();

    [Fact]
    public void FR136_Binding_is_cleared_when_the_binding_Owner_loses_the_role_or_leaves_or_the_connection_goes()
    {
        var connection = Guid.CreateVersion7();

        Assert.True(GitBindingRules.RoleChangeClearsBinding(Owner, Owner, ProjectRole.Contributor));
        Assert.False(GitBindingRules.RoleChangeClearsBinding(Owner, Owner, ProjectRole.Owner));
        Assert.False(GitBindingRules.RoleChangeClearsBinding(Owner, OtherOwner, ProjectRole.Viewer));
        Assert.True(GitBindingRules.RemovalClearsBinding(Owner, Owner));
        Assert.False(GitBindingRules.RemovalClearsBinding(Owner, OtherOwner));
        Assert.True(GitBindingRules.ConnectionChangeClearsBinding(connection, connection));
        Assert.False(GitBindingRules.ConnectionChangeClearsBinding(connection, Guid.CreateVersion7()));
    }

    [Fact]
    public void FR136_Git_job_without_a_usable_binding_fails_with_GIT_NOT_CONNECTED()
    {
        var valid = new GitConnectionRef(Guid.CreateVersion7(), Owner, Instance, Valid: true);

        Assert.True(GitBindingRules.CanRunGitJob(valid, instanceEnabled: true, "GitLab").IsSuccess);
        Assert.Equal(ErrorCodes.GitNotConnected, GitBindingRules.CanRunGitJob(null, true, "GitLab").Error?.Code);
        Assert.Equal(ErrorCodes.GitNotConnected, GitBindingRules.CanRunGitJob(valid with { Valid = false }, true, "GitLab").Error?.Code);
        Assert.Equal(ErrorCodes.GitNotConnected, GitBindingRules.CanRunGitJob(valid, instanceEnabled: false, "GitLab").Error?.Code);
    }

    [Fact]
    public void FR136_Another_Owner_rebinds_their_own_valid_connection_on_the_same_instance_with_write_access()
    {
        var mine = new GitConnectionRef(Guid.CreateVersion7(), OtherOwner, Instance, Valid: true);

        Assert.True(GitBindingRules.CanRebind(OtherOwner, ProjectRole.Owner, mine, Instance, hasWriteAccess: true, "GitLab").IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, GitBindingRules.CanRebind(OtherOwner, ProjectRole.Contributor, mine, Instance, true, "GitLab").Error?.Code);
        Assert.Equal(ErrorCodes.NotFound, GitBindingRules.CanRebind(OtherOwner, ProjectRole.Owner, null, Instance, true, "GitLab").Error?.Code);
        Assert.Equal(ErrorCodes.CredentialNotOwner, GitBindingRules.CanRebind(Owner, ProjectRole.Owner, mine, Instance, true, "GitLab").Error?.Code);
        Assert.Equal(ErrorCodes.Validation, GitBindingRules.CanRebind(OtherOwner, ProjectRole.Owner, mine, Guid.CreateVersion7(), true, "GitLab").Error?.Code);
        Assert.Equal(ErrorCodes.GitNotConnected, GitBindingRules.CanRebind(OtherOwner, ProjectRole.Owner, mine with { Valid = false }, Instance, true, "GitLab").Error?.Code);
        Assert.Equal(ErrorCodes.Forbidden, GitBindingRules.CanRebind(OtherOwner, ProjectRole.Owner, mine, Instance, false, "GitLab").Error?.Code);
        Assert.Equal(ErrorCodes.Forbidden, GitBindingRules.CanRebind(OtherOwner, ProjectRole.Owner, mine, Instance, null, "GitLab").Error?.Code);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(40, true)]
    [InlineData(20, false)]
    [InlineData(null, false)]
    public void FR136_GitLab_developer_access_is_level_30_or_more(int? level, bool expected) =>
        Assert.Equal(expected, GitBindingRules.GitLabHasDeveloperAccess(level));

    [Theory]
    [InlineData("write", true)]
    [InlineData("admin", true)]
    [InlineData("read", false)]
    [InlineData(null, false)]
    public void FR136_Bitbucket_write_access(string? permission, bool expected) =>
        Assert.Equal(expected, GitBindingRules.BitbucketHasWriteAccess(permission));

    [Fact]
    public void FR136_Branches_stay_aide_slug_version_on_every_host() =>
        Assert.Equal("aide/todo-app/v0.1.0", ReleaseVersionRules.BranchName("todo-app", Guid.CreateVersion7(), SemVer.Parse("0.1.0")));
}
