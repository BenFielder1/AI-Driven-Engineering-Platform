using System.Text;
using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Execution;

namespace Aide.Domain.RepoTools;

/// <summary>
/// Path rules of the repo tools (FR-71): the v1 §4.6 rules (<see cref="PathRules.IsUnsafe"/>: no <c>..</c>, absolute or
/// drive paths, <c>.git</c> under any alias, control characters, reserved device names, …) refuse a path with
/// <see cref="RepoToolErrors.UnsafePath"/> before anything is read. <c>.git/</c> and <c>.aide/</c> are hidden: never listed,
/// searched or read (a <c>.aide/</c> path looks missing). Symbolic links are refused by the infrastructure when it resolves
/// the path on disk.
/// </summary>
public static class RepoToolPaths
{
    /// <summary>The normalised path (empty for the root when <paramref name="allowRoot"/>), or <c>UNSAFE_PATH</c>.</summary>
    public static Result<string> Check(string raw, bool allowRoot)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (PathRules.HasUnsafeCharacters(raw))
        {
            return Result.Failure<string>(Unsafe(raw));
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\'))
        {
            return Result.Failure<string>(Unsafe(raw));
        }

        var normalized = PathRules.Normalize(trimmed);
        if (normalized is "" or ".")
        {
            return allowRoot ? Result.Success(string.Empty) : Result.Failure<string>(Unsafe(raw));
        }

        return PathRules.IsUnsafe(normalized) ? Result.Failure<string>(Unsafe(raw)) : Result.Success(normalized);
    }

    /// <summary>The <c>UNSAFE_PATH</c> error; the message never repeats more than a short, single-line prefix of the path.</summary>
    public static DomainError Unsafe(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var shown = new string([.. raw.Take(120).Select(c => char.IsControl(c) ? ' ' : c)]);
        return new DomainError(RepoToolErrors.UnsafePath, $"'{shown}' is not a safe repository path (no absolute paths, '..', drive letters, '.git' or Windows aliases). Nothing was read.");
    }

    /// <summary>
    /// A repository-relative path the tools never show: anything with a <c>.git</c> segment (the repository's own and
    /// submodules') or under <c>.aide/</c> (any case), or whose name the §4.6 rules refuse (it could not be read anyway).
    /// </summary>
    public static bool IsHidden(string normalisedPath)
    {
        ArgumentNullException.ThrowIfNull(normalisedPath);
        return normalisedPath.Length > 0
            && (PathRules.IsAgentForbidden(normalisedPath)
                || normalisedPath.Split('/').Any(PathRules.IsGitDirectoryName)
                || PathRules.IsUnsafe(normalisedPath));
    }
}

/// <summary>
/// The <c>pathGlob</c> of <c>search_code</c>: <c>*</c> (within a segment), <c>**</c> (any depth), <c>?</c> (one character),
/// matched case-insensitively against the repository-relative path; a pattern without <c>/</c> matches the file name at
/// any depth (<c>*.cs</c>). Globs follow the §4.6 path rules once the wildcards are set aside.
/// </summary>
public static class RepoGlob
{
    public static string Normalize(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        return PathRules.Normalize(glob);
    }

    public static bool IsSafe(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        if (PathRules.HasUnsafeCharacters(glob) || glob.TrimStart().StartsWith('/') || glob.TrimStart().StartsWith('\\'))
        {
            return false;
        }

        var normalized = Normalize(glob);
        if (normalized.Length == 0)
        {
            return false;
        }

        // Each wildcard stands in for an ordinary name character, then the path rules apply.
        var probe = normalized.Replace("**", "x", StringComparison.Ordinal).Replace('*', 'x').Replace('?', 'x');
        return !PathRules.IsUnsafe(probe);
    }

    public static bool IsMatch(string glob, string normalisedPath)
    {
        ArgumentNullException.ThrowIfNull(glob);
        ArgumentNullException.ThrowIfNull(normalisedPath);
        var pattern = Normalize(glob);
        var subject = pattern.Contains('/', StringComparison.Ordinal) ? normalisedPath : normalisedPath[(normalisedPath.LastIndexOf('/') + 1)..];
        return ToRegex(pattern).IsMatch(subject);
    }

    public static Regex ToRegex(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        var regex = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var slash = i + 2 < glob.Length && glob[i + 2] == '/';
                regex.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*')
            {
                regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        regex.Append('$');
        return new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}

/// <summary>The lines <c>read_file</c> returns: the requested range, at most 400 lines and 64 KB (FR-71).</summary>
public sealed record RepoFileSlice(string Text, int FirstLine, int LastLine, int TotalLines, bool Truncated)
{
    /// <summary>
    /// Lines <paramref name="startLine"/>..<paramref name="endLine"/> (1-based, inclusive; to the end of the file when
    /// <c>null</c>) of <paramref name="content"/>, capped at <paramref name="maxLines"/> lines and <paramref name="maxBytes"/>
    /// UTF-8 bytes (whole lines only, except a single line longer than the cap, which is cut). <see cref="Truncated"/> is
    /// set when a cap cut the requested range short. A start past the end gives no lines.
    /// </summary>
    public static RepoFileSlice Take(string content, int startLine, int? endLine, int maxLines = RepoToolLimits.MaxReadLines, int maxBytes = RepoToolLimits.MaxReadBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var total = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var first = Math.Max(1, startLine);
        if (first > total)
        {
            return new RepoFileSlice(string.Empty, first, first - 1, total, false);
        }

        var requestedLast = Math.Min(total, endLine ?? total);
        var last = Math.Min(requestedLast, first + maxLines - 1);
        var text = new StringBuilder();
        var bytes = 0;
        var taken = first - 1;
        for (var n = first; n <= last; n++)
        {
            var line = lines[n - 1];
            var size = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + size > maxBytes)
            {
                if (n == first)
                {
                    // A single line longer than the cap: return its start.
                    text.Append(CutToBytes(line, maxBytes - 1)).Append('\n');
                    taken = n;
                }

                return new RepoFileSlice(text.ToString(), first, taken, total, true);
            }

            text.Append(line).Append('\n');
            bytes += size;
            taken = n;
        }

        return new RepoFileSlice(text.ToString(), first, taken, total, taken < requestedLast);
    }

    private static string CutToBytes(string line, int maxBytes)
    {
        var count = 0;
        var end = 0;
        while (end < line.Length)
        {
            var width = char.IsHighSurrogate(line[end]) && end + 1 < line.Length ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(line.AsSpan(end, width));
            if (count + size > maxBytes)
            {
                break;
            }

            count += size;
            end += width;
        }

        return line[..end];
    }
}
