using System.Collections.Concurrent;

namespace Aide.Domain.Stack;

/// <summary>
/// A set of expanded composition globs with exclusions (the v2 <see cref="GlobSet"/> semantics: a path matches when it matches
/// a positive glob and no <c>!</c>-prefixed one), built from a process-wide cache of compiled <see cref="PathGlob"/>s. Each
/// resolution expands a few hundred pack globs, and compiling a non-backtracking pattern is the expensive part, so each
/// distinct pattern is compiled once. Invalid patterns are left out (the catalog loader already rejects them).
/// </summary>
public sealed class StackGlobSet
{
    private const int MaxCached = 20_000;

    private static readonly ConcurrentDictionary<string, PathGlob?> Cache = new(StringComparer.Ordinal);

    private readonly PathGlob[] _include;
    private readonly PathGlob[] _exclude;

    private StackGlobSet(IReadOnlyList<string> patterns, PathGlob[] include, PathGlob[] exclude)
    {
        Patterns = patterns;
        _include = include;
        _exclude = exclude;
    }

    /// <summary>The patterns as given (exclusions with their <c>!</c>), invalid ones left out.</summary>
    public IReadOnlyList<string> Patterns { get; }

    public static StackGlobSet Empty { get; } = new([], [], []);

    /// <summary>The compiled glob for <paramref name="pattern"/> (no <c>!</c>), or <c>null</c> when it is invalid. Cached.</summary>
    public static PathGlob? Glob(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (Cache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        if (Cache.Count >= MaxCached)
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd(pattern, p => PathGlob.TryCreate(p, out _));
    }

    /// <summary>Whether <paramref name="pattern"/> (optionally <c>!</c>-negated) is a valid glob.</summary>
    public static bool IsValid(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return Glob(pattern.StartsWith('!') ? pattern[1..] : pattern) is not null;
    }

    /// <summary>The set of <paramref name="patterns"/>; without a positive pattern it matches nothing.</summary>
    public static StackGlobSet Of(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var written = new List<string>();
        var include = new List<PathGlob>();
        var exclude = new List<PathGlob>();
        foreach (var pattern in patterns)
        {
            var negated = pattern.StartsWith('!');
            if (Glob(negated ? pattern[1..] : pattern) is not { } glob || written.Contains(pattern, StringComparer.Ordinal))
            {
                continue;
            }

            written.Add(pattern);
            (negated ? exclude : include).Add(glob);
        }

        return include.Count == 0 ? Empty : new StackGlobSet(written, [.. include], [.. exclude]);
    }

    public bool IsMatch(string normalisedPath) =>
        PathGlob.AnyMatch(_include, normalisedPath) && !PathGlob.AnyMatch(_exclude, normalisedPath);
}
