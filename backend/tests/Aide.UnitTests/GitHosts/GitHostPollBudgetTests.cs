using System.Globalization;
using Aide.Domain.GitHosts;

namespace Aide.UnitTests.GitHosts;

/// <summary>§4.19: GitLab poll cadence and back-off, and the Bitbucket per-connection token bucket.</summary>
public class GitHostPollBudgetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private static string Epoch(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(0, 2)]
    [InlineData(59, 2)]
    [InlineData(60, 10)]
    public void S4_19_GitLab_merge_polls_every_2_minutes_for_an_hour_then_every_10(int minutesWaited, int interval)
    {
        var now = Now.AddMinutes(minutesWaited);

        Assert.Equal(now.AddMinutes(interval), GitHostPollBudget.NextGitLabMergePollAt(Now, now));
    }

    [Fact]
    public void S4_19_GitLab_status_polls_every_30_seconds()
    {
        Assert.Equal(Now.AddSeconds(30), GitHostPollBudget.NextGitLabStatusPollAt(Now, 200, RateLimitHeaders.None));
        Assert.Equal(Now.AddSeconds(30), GitHostPollBudget.NextGitLabStatusPollAt(Now, 200, new RateLimitHeaders("100", "1000", Epoch(Now.AddMinutes(1)))));
    }

    [Fact]
    public void S4_19_GitLab_backs_off_until_the_reset_below_10_percent_remaining()
    {
        var reset = Now.AddMinutes(3);
        var low = new RateLimitHeaders("99", "1000", Epoch(reset));

        Assert.True(GitHostPollBudget.IsLow(low));
        Assert.Equal(reset, GitHostPollBudget.NextGitLabStatusPollAt(Now, 200, low));
        Assert.Equal(Now.AddMinutes(2), GitHostPollBudget.NextGitLabStatusPollAt(Now, 200, low with { Reset = null }));
        Assert.Equal(Now.AddSeconds(30), GitHostPollBudget.NextGitLabStatusPollAt(Now, 200, low with { Reset = Epoch(Now.AddSeconds(5)) }));
    }

    [Fact]
    public void S4_19_GitLab_429_waits_for_Retry_After()
    {
        Assert.Equal(Now.AddSeconds(90), GitHostPollBudget.NextGitLabStatusPollAt(Now, 429, new RateLimitHeaders(RetryAfter: "90")));
        Assert.Equal(Now.AddMinutes(4), GitHostPollBudget.NextGitLabStatusPollAt(Now, 429, new RateLimitHeaders(Reset: Epoch(Now.AddMinutes(4)))));
        Assert.Equal(Now.AddMinutes(1), GitHostPollBudget.NextGitLabStatusPollAt(Now, 429, RateLimitHeaders.None));
        Assert.Equal(Now.AddMinutes(10), GitHostPollBudget.ResetAt(Now.AddMinutes(10).ToString("r", CultureInfo.InvariantCulture), null, Now));
        Assert.Equal(Now.AddDays(1), GitHostPollBudget.ResetAt("999999999", null, Now));
    }

    [Fact]
    public void S4_19_Bitbucket_bucket_allows_900_background_requests_per_hour()
    {
        var state = GitHostPollBudget.FullBucket(Now);
        for (var i = 0; i < GitHostPollBudget.BitbucketBackgroundPerHour; i++)
        {
            var take = GitHostPollBudget.TryTake(state, Now);
            Assert.True(take.Allowed);
            state = take.State;
        }

        var refused = GitHostPollBudget.TryTake(state, Now);
        Assert.False(refused.Allowed);
        Assert.Equal(Now.AddSeconds(4), refused.RetryAt);

        var refilled = GitHostPollBudget.TryTake(state, Now.AddSeconds(5));
        Assert.True(refilled.Allowed);

        var afterAnHour = GitHostPollBudget.TryTake(state, Now.AddHours(2));
        Assert.True(afterAnHour.Allowed);
        Assert.Equal(GitHostPollBudget.BitbucketBackgroundPerHour - 1, afterAnHour.State.Tokens, precision: 6);
    }

    [Fact]
    public void S4_19_Bitbucket_429_drains_the_bucket()
    {
        var decision = GitHostPollBudget.OnBitbucketRateLimited(Now, null);

        Assert.False(decision.Allowed);
        Assert.Equal(0, decision.State.Tokens);
        Assert.Equal(Now.AddMinutes(5), decision.RetryAt);
        Assert.Equal(Now.AddSeconds(30), GitHostPollBudget.OnBitbucketRateLimited(Now, "30").RetryAt);
    }

    [Fact]
    public void S4_19_Bitbucket_PR_is_polled_at_most_once_a_minute()
    {
        Assert.True(GitHostPollBudget.CanPollBitbucketPullRequest(null, Now));
        Assert.False(GitHostPollBudget.CanPollBitbucketPullRequest(Now.AddSeconds(-59), Now));
        Assert.True(GitHostPollBudget.CanPollBitbucketPullRequest(Now.AddMinutes(-1), Now));
        Assert.Equal(Now.AddSeconds(30), GitHostPollBudget.NextBitbucketPullRequestPollAt(Now.AddSeconds(-30), Now));
    }
}
