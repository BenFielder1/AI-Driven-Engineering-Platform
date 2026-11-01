using System.Globalization;
using Aide.Domain.Releases;

namespace Aide.Domain.GitHosts;

/// <summary>A host response's rate-limit headers, verbatim (absent headers are <c>null</c>).</summary>
/// <param name="Remaining">GitLab <c>RateLimit-Remaining</c>.</param>
/// <param name="Limit">GitLab <c>RateLimit-Limit</c>.</param>
/// <param name="Reset">GitLab <c>RateLimit-Reset</c> (Unix epoch seconds).</param>
/// <param name="RetryAfter"><c>Retry-After</c> (seconds, or an HTTP date).</param>
public sealed record RateLimitHeaders(string? Remaining = null, string? Limit = null, string? Reset = null, string? RetryAfter = null)
{
    public static RateLimitHeaders None { get; } = new();
}

/// <summary>A Bitbucket connection's token bucket (§4.19): the tokens left at <see cref="UpdatedAt"/>.</summary>
public sealed record TokenBucketState(double Tokens, DateTimeOffset UpdatedAt);

/// <summary>Whether a background Bitbucket request may run now; when not, when it may.</summary>
public sealed record TokenBucketDecision(bool Allowed, TokenBucketState State, DateTimeOffset? RetryAt);

/// <summary>
/// §4.19 polling budget (no webhooks, v2 D-V2-8), as pure functions over (now, last poll, headers, bucket state).
/// <list type="bullet">
/// <item><b>GitLab:</b> merge polls every 2 minutes for an hour, then every 10 (v2 <see cref="MergePollRules"/>); status
/// polls every 30 seconds, backing off until <c>RateLimit-Reset</c> when <c>RateLimit-Remaining</c> is below 10 % of
/// <c>RateLimit-Limit</c>, and on 429 until <c>Retry-After</c>.</item>
/// <item><b>Bitbucket:</b> 1,000 requests per hour per user and no remaining-count headers, so each connection has a token
/// bucket of 900 per hour for background work (refilled continuously), and a PR is polled at most once a minute.</item>
/// </list>
/// Rate-limited jobs are re-queued without counting an attempt (<see cref="Jobs.JobPolicy"/>).
/// </summary>
public static class GitHostPollBudget
{
    /// <summary>§4.19: GitLab status polls while waiting (v2 <see cref="Review.ExternalCheckRules.RecheckInterval"/>).</summary>
    public static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>§4.19: back off when fewer than this share of the GitLab rate limit remains.</summary>
    public const double LowRemainingShare = 0.10;

    /// <summary>The back-off when the remaining share is low but GitLab sent no reset time.</summary>
    public static readonly TimeSpan LowRemainingBackoff = TimeSpan.FromMinutes(2);

    /// <summary>The wait after a 429 without <c>Retry-After</c> or <c>RateLimit-Reset</c>.</summary>
    public static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(1);

    /// <summary>§4.19: Bitbucket background requests per connection per hour (of the 1,000 Bitbucket allows).</summary>
    public const int BitbucketBackgroundPerHour = 900;

    /// <summary>§4.19: a Bitbucket PR is polled at most once a minute.</summary>
    public static readonly TimeSpan BitbucketPullRequestPollInterval = TimeSpan.FromMinutes(1);

    /// <summary>The wait after a Bitbucket 429 without <c>Retry-After</c> (Bitbucket's window is a rolling hour).</summary>
    public static readonly TimeSpan BitbucketRateLimitWait = TimeSpan.FromMinutes(5);

    /// <summary>The longest wait honoured from a header (a day).</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromDays(1);

    /// <summary>§4.19: the GitLab merge poll keeps v2's cadence (2 minutes for an hour, then 10, up to 14 days).</summary>
    public static DateTimeOffset NextGitLabMergePollAt(DateTimeOffset since, DateTimeOffset now) => MergePollRules.NextPollAt(since, now);

    /// <summary>
    /// §4.19: when to poll a GitLab status next after a response with <paramref name="status"/> and
    /// <paramref name="headers"/> at <paramref name="now"/>: on 429 at the reset (<c>Retry-After</c>, else
    /// <c>RateLimit-Reset</c>, else a minute); when under 10 % of the limit remains, at <c>RateLimit-Reset</c> (else two
    /// minutes), never sooner than 30 seconds; otherwise in 30 seconds.
    /// </summary>
    public static DateTimeOffset NextGitLabStatusPollAt(DateTimeOffset now, int status, RateLimitHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var normal = now + StatusPollInterval;
        if (status == 429)
        {
            return ResetAt(headers.RetryAfter, headers.Reset, now) ?? now + DefaultRateLimitWait;
        }

        if (IsLow(headers))
        {
            var reset = ResetAt(null, headers.Reset, now) ?? now + LowRemainingBackoff;
            return reset > normal ? reset : normal;
        }

        return normal;
    }

    /// <summary>
    /// §4.19: the earliest the next GitLab merge poll may run after a response with <paramref name="headers"/>: when under
    /// 10 % of the rate limit remains, at <c>RateLimit-Reset</c> (else two minutes later); otherwise <c>null</c> (the merge
    /// poll's own cadence applies).
    /// </summary>
    public static DateTimeOffset? GitLabMergePollNotBefore(RateLimitHeaders headers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return IsLow(headers) ? ResetAt(null, headers.Reset, now) ?? now + LowRemainingBackoff : null;
    }

    /// <summary>The later of the merge poll's next time and the rate-limit back-off (<c>null</c>: none).</summary>
    public static DateTimeOffset Later(DateTimeOffset next, DateTimeOffset? notBefore) =>
        notBefore is { } backOff && backOff > next ? backOff : next;

    /// <summary>§4.19: <c>RateLimit-Remaining</c> is below 10 % of <c>RateLimit-Limit</c> (unknown values aren't low).</summary>
    public static bool IsLow(RateLimitHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return long.TryParse(headers.Remaining?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var remaining)
            && long.TryParse(headers.Limit?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            && limit > 0
            && remaining < limit * LowRemainingShare;
    }

    /// <summary>
    /// When a limit resets: <c>Retry-After</c> (seconds or an HTTP date), else <c>RateLimit-Reset</c> (epoch seconds), each
    /// capped at a day; <c>null</c> when neither gives a time after <paramref name="now"/>.
    /// </summary>
    public static DateTimeOffset? ResetAt(string? retryAfter, string? rateLimitReset, DateTimeOffset now)
    {
        var retry = retryAfter?.Trim();
        if (long.TryParse(retry, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return now + TimeSpan.FromSeconds(Math.Min(seconds, MaxWait.TotalSeconds));
        }

        if (!string.IsNullOrEmpty(retry)
            && DateTimeOffset.TryParseExact(retry, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            && date > now)
        {
            return date - now > MaxWait ? now + MaxWait : date;
        }

        if (long.TryParse(rateLimitReset?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            && epoch < 253_402_300_799
            && DateTimeOffset.FromUnixTimeSeconds(epoch) is var at
            && at > now)
        {
            return at - now > MaxWait ? now + MaxWait : at;
        }

        return null;
    }

    /// <summary>A full Bitbucket bucket (a new connection, or one not seen since this process started).</summary>
    public static TokenBucketState FullBucket(DateTimeOffset now) => new(BitbucketBackgroundPerHour, now);

    /// <summary>
    /// §4.19: takes <paramref name="cost"/> tokens from a connection's bucket after refilling it at 900 per hour (capped at
    /// 900). When there aren't enough, the request waits until there are (the state is only refilled).
    /// </summary>
    public static TokenBucketDecision TryTake(TokenBucketState state, DateTimeOffset now, int cost = 1)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfLessThan(cost, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cost, BitbucketBackgroundPerHour);
        var refilled = Refill(state, now);
        if (refilled.Tokens >= cost)
        {
            return new TokenBucketDecision(true, refilled with { Tokens = refilled.Tokens - cost }, null);
        }

        var missing = cost - refilled.Tokens;
        var wait = TimeSpan.FromTicks((long)Math.Ceiling(missing * TimeSpan.TicksPerHour / BitbucketBackgroundPerHour));
        return new TokenBucketDecision(false, refilled, refilled.UpdatedAt + wait);
    }

    /// <summary>
    /// §4.19: a Bitbucket 429 drains the bucket and waits for <c>Retry-After</c> when sent, else five minutes.
    /// </summary>
    public static TokenBucketDecision OnBitbucketRateLimited(DateTimeOffset now, string? retryAfter) =>
        new(false, new TokenBucketState(0, now), ResetAt(retryAfter, null, now) ?? now + BitbucketRateLimitWait);

    /// <summary>
    /// §4.19: the bucket to keep after a Bitbucket 429: empty until the wait of <see cref="OnBitbucketRateLimited"/> ends, so
    /// <see cref="TryTake"/> refuses every request (background and request paths alike) until then and refills from there.
    /// </summary>
    public static TokenBucketState BucketAfterBitbucketRateLimited(DateTimeOffset now, string? retryAfter) =>
        new(0, OnBitbucketRateLimited(now, retryAfter).RetryAt!.Value);

    /// <summary>§4.19: the earliest a Bitbucket PR may be polled again (at most once a minute).</summary>
    public static DateTimeOffset NextBitbucketPullRequestPollAt(DateTimeOffset? lastPollAt, DateTimeOffset now) =>
        lastPollAt is { } last && last + BitbucketPullRequestPollInterval > now ? last + BitbucketPullRequestPollInterval : now;

    /// <summary>§4.19: whether a Bitbucket PR may be polled now.</summary>
    public static bool CanPollBitbucketPullRequest(DateTimeOffset? lastPollAt, DateTimeOffset now) =>
        NextBitbucketPullRequestPollAt(lastPollAt, now) <= now;

    private static TokenBucketState Refill(TokenBucketState state, DateTimeOffset now)
    {
        if (now <= state.UpdatedAt)
        {
            return state;
        }

        var added = (now - state.UpdatedAt).TotalHours * BitbucketBackgroundPerHour;
        return new TokenBucketState(Math.Min(BitbucketBackgroundPerHour, state.Tokens + added), now);
    }
}
