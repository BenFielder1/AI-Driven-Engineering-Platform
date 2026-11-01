using System.Collections.Concurrent;
using Aide.Domain.GitHosts;

namespace Aide.Infrastructure.Bitbucket;

/// <summary>
/// §4.19, §8 Reliability: the one shared Bitbucket limiter, keyed by connection. Every request sent with a connection's
/// token (background jobs and request paths alike) takes a token from that connection's bucket (900 per hour,
/// <see cref="GitHostPollBudget.TryTake"/>), so AIDE stays under Bitbucket's 1,000 requests per hour per user with room for
/// the user's own tools. Requests made while saving an API token, before a connection exists, take from a bucket per AIDE
/// user (<see cref="UserKey"/>, same budget), so repeated saves are throttled. A Bitbucket 429 empties the bucket until
/// <c>Retry-After</c> (else five minutes, <see cref="GitHostPollBudget.BucketAfterBitbucketRateLimited"/>). The buckets are
/// in memory: AIDE runs one API replica (the M27 OAuth-state precedent); a restart starts every bucket full.
/// </summary>
public sealed class BitbucketRateLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, TokenBucketState> _buckets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The bucket of a connection.</summary>
    public static string ConnectionKey(Guid connectionId) => $"connection:{connectionId:N}";

    /// <summary>The bucket of an AIDE user's token checks (no connection yet).</summary>
    public static string UserKey(Guid userId) => $"user:{userId:N}";

    /// <summary>Takes one request from the bucket: <c>null</c> when allowed, else how long until one is.</summary>
    public TimeSpan? TryAcquire(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var state = _buckets.TryGetValue(key, out var current) ? current : GitHostPollBudget.FullBucket(now);
            var decision = GitHostPollBudget.TryTake(state, now);
            _buckets[key] = decision.State;
            if (decision.Allowed)
            {
                return null;
            }

            var wait = decision.RetryAt!.Value - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
        }
    }

    /// <summary>Bitbucket answered 429: no request is sent from the bucket until the wait ends.</summary>
    public void OnRateLimited(string key, string? retryAfter)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var now = time.GetUtcNow();
        lock (_gate)
        {
            _buckets[key] = GitHostPollBudget.BucketAfterBitbucketRateLimited(now, retryAfter);
        }
    }

    /// <summary>Forgets a removed connection's bucket.</summary>
    public void Forget(Guid connectionId) => _buckets.TryRemove(ConnectionKey(connectionId), out _);
}

/// <summary>
/// FR-136, §4.19: Bitbucket (or AIDE's per-connection budget) held back a background job until <see cref="ResetAt"/>. The job
/// worker re-queues the job for then without counting the attempt (<c>BITBUCKET_RATE_LIMITED</c>); request paths answer 429
/// <c>UPSTREAM_RATE_LIMITED</c>.
/// </summary>
public sealed class BitbucketRateLimitedException : Exception
{
    public BitbucketRateLimitedException()
        : this(DateTimeOffset.UtcNow + GitHostPollBudget.BitbucketRateLimitWait)
    {
    }

    public BitbucketRateLimitedException(string message)
        : base(message)
    {
        ResetAt = DateTimeOffset.UtcNow + GitHostPollBudget.BitbucketRateLimitWait;
    }

    public BitbucketRateLimitedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ResetAt = DateTimeOffset.UtcNow + GitHostPollBudget.BitbucketRateLimitWait;
    }

    public BitbucketRateLimitedException(DateTimeOffset resetAt)
        : base($"Bitbucket is rate limiting requests until {resetAt:u}.")
    {
        ResetAt = resetAt;
    }

    public DateTimeOffset ResetAt { get; }
}
