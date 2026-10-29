using System.Collections.Concurrent;

namespace Aide.Api.Features.Bitbucket;

/// <summary>
/// FR-135, §4.19: the workspace slugs of a connection, kept for five minutes so paging through the repo picker doesn't list
/// the workspaces again on every page (each Bitbucket call spends the connection's budget). In memory (one API replica).
/// </summary>
public sealed class BitbucketWorkspaceCache(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>The most connections held; beyond it expired entries are swept and, if still full, nothing new is kept.</summary>
    public const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<Guid, (IReadOnlyList<string> Slugs, DateTimeOffset ExpiresAt)> _entries = new();

    /// <summary>The connection's workspace slugs: the cached list while fresh, else <paramref name="load"/>'s (then cached).</summary>
    public async Task<IReadOnlyList<string>> GetAsync(Guid connectionId, Func<Task<IReadOnlyList<string>>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        var now = time.GetUtcNow();
        if (_entries.TryGetValue(connectionId, out var entry) && entry.ExpiresAt > now)
        {
            return entry.Slugs;
        }

        var slugs = await load();
        if (_entries.Count >= MaxEntries)
        {
            foreach (var (key, value) in _entries)
            {
                if (value.ExpiresAt <= now)
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }

        if (_entries.Count < MaxEntries || _entries.ContainsKey(connectionId))
        {
            _entries[connectionId] = (slugs, now + Lifetime);
        }

        return slugs;
    }
}
