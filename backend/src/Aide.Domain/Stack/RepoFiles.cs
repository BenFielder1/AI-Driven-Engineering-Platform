using Aide.Domain.Execution;

namespace Aide.Domain.Stack;

/// <summary>
/// FR-118: a repository's file list as stack detection reads it: paths normalised (<see cref="PathRules.Normalize"/>),
/// unsafe ones (absolute, <c>..</c>, control characters; <see cref="PathRules.IsUnsafe"/>) left out, and duplicates after
/// normalisation collapsed (the first wins), in listing order.
/// </summary>
public static class RepoFiles
{
    /// <summary>The files of <paramref name="entries"/> by normalised path, at most <paramref name="maxFiles"/>; <c>Truncated</c> when more were listed.</summary>
    public static (IReadOnlyDictionary<string, T> Files, bool Truncated) ByNormalisedPath<T>(IEnumerable<(string Path, T Value)> entries, int maxFiles)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFiles);
        var files = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (path, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var normalised = PathRules.Normalize(path);
            if (PathRules.IsUnsafe(normalised) || files.ContainsKey(normalised))
            {
                continue;
            }

            if (files.Count >= maxFiles)
            {
                return (files, true);
            }

            files[normalised] = value;
        }

        return (files, false);
    }
}
