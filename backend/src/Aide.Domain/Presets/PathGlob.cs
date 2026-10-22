using System.Text;
using System.Text.RegularExpressions;

namespace Aide.Domain.Presets;

/// <summary>
/// §4.11: a path glob of a preset (owned paths, plan rules, protected paths), matched against normalised repository paths
/// (forward slashes, no leading <c>./</c>, case-insensitive like v1 <c>PathRules</c>, so a case-insensitive checkout
/// can't sneak past a protected path). A pattern with a <c>/</c> is anchored at the repository root;
/// one without (<c>.env*</c>, <c>package.json</c>) matches that file name in any folder, as in <c>.gitignore</c>.
/// <list type="bullet">
/// <item><c>**</c> as a whole segment matches zero or more segments (<c>app/**</c>: everything under <c>app/</c>;
/// <c>**/*.test.ts</c>: a test file anywhere, the root included).</item>
/// <item><c>*</c> matches any characters except <c>/</c>; <c>?</c> one character except <c>/</c>.</item>
/// <item><c>{a,b}</c> matches either alternative (not nested): <c>**/*.test.{ts,tsx}</c>.</item>
/// <item>Everything else is literal (<c>[</c> included).</item>
/// </list>
/// A pure, dependency-free matcher (no file system) so plan validation stays in the domain; patterns compile to
/// non-backtracking regular expressions, so no pattern can make matching slow.
/// </summary>
public sealed class PathGlob
{
    /// <summary>The longest pattern accepted.</summary>
    public const int MaxLength = 300;

    private readonly Regex _regex;

    private PathGlob(string pattern, Regex regex)
    {
        Pattern = pattern;
        _regex = regex;
    }

    public string Pattern { get; }

    /// <summary>Whether <paramref name="normalisedPath"/> matches the pattern.</summary>
    public bool IsMatch(string normalisedPath)
    {
        ArgumentNullException.ThrowIfNull(normalisedPath);
        return _regex.IsMatch(normalisedPath);
    }

    /// <summary>Compiles a pattern; <c>null</c> with a reason when it is empty, too long, absolute, escapes the root or has unbalanced braces.</summary>
    public static PathGlob? TryCreate(string? pattern, out string? reason)
    {
        reason = Check(pattern);
        if (reason is not null)
        {
            return null;
        }

        var anchored = pattern!.Contains('/', StringComparison.Ordinal) ? pattern : "**/" + pattern;
        var regex = new Regex(ToRegex(anchored), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
        return new PathGlob(pattern!, regex);
    }

    /// <summary>Compiles a pattern that is known to be valid (throws otherwise).</summary>
    public static PathGlob Create(string pattern) =>
        TryCreate(pattern, out var reason) ?? throw new ArgumentException(reason, nameof(pattern));

    /// <summary>Whether <paramref name="path"/> matches any of <paramref name="globs"/>.</summary>
    public static bool AnyMatch(IEnumerable<PathGlob> globs, string path) => globs.Any(g => g.IsMatch(path));

    public override string ToString() => Pattern;

    private static string? Check(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "A glob is empty.";
        }

        if (pattern.Length > MaxLength)
        {
            return $"The glob '{pattern[..40]}…' is longer than {MaxLength} characters.";
        }

        if (pattern != pattern.Trim() || pattern.Any(char.IsControl))
        {
            return $"The glob '{pattern}' has surrounding whitespace or control characters.";
        }

        if (pattern.StartsWith('/') || pattern.Contains('\\', StringComparison.Ordinal) || pattern.Split('/').Any(s => s is ".." or "." or ""))
        {
            return $"The glob '{pattern}' must be a relative path with forward slashes (no leading '/', '.', '..' or empty segments).";
        }

        var depth = 0;
        foreach (var c in pattern)
        {
            depth += c switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth is < 0 or > 1)
            {
                return $"The glob '{pattern}' has unbalanced or nested braces.";
            }
        }

        return depth == 0 ? null : $"The glob '{pattern}' has unbalanced or nested braces.";
    }

    private static string ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        var inBraces = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            var segmentStart = i == 0 || pattern[i - 1] == '/';
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*' && segmentStart
                && (i + 2 == pattern.Length || pattern[i + 2] == '/'))
            {
                if (i + 2 == pattern.Length)
                {
                    sb.Append(".*");
                    i += 1;
                }
                else
                {
                    sb.Append("(?:[^/]+/)*");
                    i += 2; // the following '/' belongs to the group
                }

                continue;
            }

            switch (c)
            {
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '{':
                    inBraces = true;
                    sb.Append("(?:");
                    break;
                case '}':
                    inBraces = false;
                    sb.Append(')');
                    break;
                case ',' when inBraces:
                    sb.Append('|');
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return sb.Append('$').ToString();
    }
}

/// <summary>
/// A set of globs with exclusions: a path matches when it matches a positive glob and no <c>!</c>-prefixed one
/// (e.g. <c>["app/**", "!app/api/**"]</c>: everything under <c>app/</c> except the API routes).
/// </summary>
public sealed class GlobSet
{
    private readonly PathGlob[] _include;
    private readonly PathGlob[] _exclude;

    private GlobSet(IReadOnlyList<string> patterns, PathGlob[] include, PathGlob[] exclude)
    {
        Patterns = patterns;
        _include = include;
        _exclude = exclude;
    }

    /// <summary>The patterns as written (exclusions with their <c>!</c>).</summary>
    public IReadOnlyList<string> Patterns { get; }

    public static GlobSet Empty { get; } = new([], [], []);

    public bool IsMatch(string normalisedPath) =>
        PathGlob.AnyMatch(_include, normalisedPath) && !PathGlob.AnyMatch(_exclude, normalisedPath);

    /// <summary>Compiles <paramref name="patterns"/>; invalid ones are added to <paramref name="reasons"/> (prefixed with <paramref name="where"/>).</summary>
    public static GlobSet Compile(IEnumerable<string>? patterns, string where, ICollection<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        var written = new List<string>();
        var include = new List<PathGlob>();
        var exclude = new List<PathGlob>();
        foreach (var pattern in patterns ?? [])
        {
            var negated = pattern is not null && pattern.StartsWith('!');
            var glob = PathGlob.TryCreate(negated ? pattern![1..] : pattern, out var reason);
            if (glob is null)
            {
                reasons.Add($"{where}: {reason}");
                continue;
            }

            written.Add(pattern!);
            (negated ? exclude : include).Add(glob);
        }

        if (include.Count == 0 && exclude.Count > 0)
        {
            reasons.Add($"{where}: only exclusions ('!…'); add the globs they exclude from.");
        }

        return new GlobSet(written, [.. include], [.. exclude]);
    }

    /// <summary>Compiles patterns that are known to be valid (throws otherwise).</summary>
    public static GlobSet Create(params string[] patterns)
    {
        var reasons = new List<string>();
        var set = Compile(patterns, "globs", reasons);
        return reasons.Count == 0 ? set : throw new ArgumentException(string.Join(" ", reasons), nameof(patterns));
    }
}
