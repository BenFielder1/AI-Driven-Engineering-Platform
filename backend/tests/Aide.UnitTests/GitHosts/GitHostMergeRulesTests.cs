using Aide.Domain.GitHosts;
using Aide.Domain.Releases;

namespace Aide.UnitTests.GitHosts;

/// <summary>FR-140, §4.19: the merged commit, merge-error mapping and GitLab's tag creation limit.</summary>
public class GitHostMergeRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private const string Merge = "1111111111111111111111111111111111111111";
    private const string Squash = "2222222222222222222222222222222222222222";
    private const string Head = "3333333333333333333333333333333333333333";

    [Fact]
    public void FR140_Fast_forward_merge_uses_the_head_sha() =>
        Assert.Equal(Head, GitLabMergeResult.Sha(null, null, Head));

    [Fact]
    public void FR140_Merged_commit_is_merge_commit_then_squash_commit_then_head()
    {
        Assert.Equal(Merge, GitLabMergeResult.Sha(Merge, Squash, Head));
        Assert.Equal(Squash, GitLabMergeResult.Sha(null, Squash, Head));
        Assert.Equal(Squash, GitLabMergeResult.Sha("  ", Squash, Head));
        Assert.Null(GitLabMergeResult.Sha(null, null, null));
    }

    [Theory]
    [InlineData(405, HostMergeFailureKind.AwaitingMerge)]
    [InlineData(406, HostMergeFailureKind.AwaitingMerge)]
    [InlineData(403, HostMergeFailureKind.AwaitingMerge)]
    [InlineData(409, HostMergeFailureKind.HeadMoved)]
    [InlineData(422, HostMergeFailureKind.Failed)]
    [InlineData(401, HostMergeFailureKind.Unauthorized)]
    [InlineData(429, HostMergeFailureKind.RateLimited)]
    [InlineData(502, HostMergeFailureKind.Retry)]
    [InlineData(404, HostMergeFailureKind.Failed)]
    public void FR140_GitLab_merge_errors_map_to_release_outcomes(int status, HostMergeFailureKind expected) =>
        Assert.Equal(expected, GitLabMergeResult.ClassifyMergeError(status).Kind);

    [Fact]
    public void S4_19_GitLab_403_on_merge_is_no_permission()
    {
        Assert.Equal(MergeReadinessReasonCode.NoPermission, GitLabMergeResult.ClassifyMergeError(403).Reason);
        Assert.Equal(MergeReadinessReasonCode.NoPermission, BitbucketMergeResult.ClassifyMergeError(403).Reason);
    }

    [Theory]
    [InlineData(400, HostMergeFailureKind.AwaitingMerge)]
    [InlineData(409, HostMergeFailureKind.HeadMoved)]
    [InlineData(555, HostMergeFailureKind.Retry)]
    [InlineData(503, HostMergeFailureKind.Retry)]
    [InlineData(401, HostMergeFailureKind.Unauthorized)]
    [InlineData(429, HostMergeFailureKind.RateLimited)]
    [InlineData(404, HostMergeFailureKind.Failed)]
    public void FR140_Bitbucket_merge_errors_map_to_release_outcomes(int status, HostMergeFailureKind expected) =>
        Assert.Equal(expected, BitbucketMergeResult.ClassifyMergeError(status).Kind);

    [Fact]
    public void FR140_GitLab_tag_creation_limit_allows_100_per_30_minutes()
    {
        var ninetyNine = Enumerable.Range(0, 99).Select(i => Now.AddSeconds(-i)).ToList();
        Assert.Null(GitLabMergeResult.NextTagAllowedAt(ninetyNine, Now));

        var hundred = Enumerable.Range(0, 100).Select(i => Now.AddMinutes(-i * 0.25)).ToList();
        var oldest = hundred.Min();
        Assert.Equal(oldest + TimeSpan.FromMinutes(30), GitLabMergeResult.NextTagAllowedAt(hundred, Now));

        var outsideWindow = hundred.Select(t => t.AddMinutes(-31)).ToList();
        Assert.Null(GitLabMergeResult.NextTagAllowedAt(outsideWindow, Now));
    }

    [Fact]
    public void FR140_GitLab_tag_limit_response_is_retried_at_the_reset()
    {
        Assert.Equal(Now.AddSeconds(120), GitLabMergeResult.TagLimitRetryAt(429, "120", null, Now));
        var reset = Now.AddMinutes(7);
        Assert.Equal(reset, GitLabMergeResult.TagLimitRetryAt(429, null, reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), Now));
        Assert.Equal(Now.AddMinutes(30), GitLabMergeResult.TagLimitRetryAt(429, null, null, Now));
        Assert.Null(GitLabMergeResult.TagLimitRetryAt(400, "120", null, Now));
    }
}
