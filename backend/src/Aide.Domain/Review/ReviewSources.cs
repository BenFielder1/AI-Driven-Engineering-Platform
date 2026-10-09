using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Execution;

namespace Aide.Domain.Review;

/// <summary>
/// §4.7 / D8: what the deterministic checks need from the project's own technical specification — the endpoint paths of
/// its API Contracts section and the dependency allowlist of its Technical Architecture section. Nothing here assumes a
/// stack: sections are found by heading, paths by shape.
/// </summary>
public static partial class SpecReading
{
    /// <summary>At most this many endpoint paths or dependencies are read from one specification.</summary>
    public const int MaxEntries = 200;

    /// <summary>
    /// Every distinct endpoint path (<c>/api/todos/{id}</c>) in the "API Contracts" section, in order of appearance. A path
    /// starts with <c>/</c> (not inside a URL) and contains a letter; a trailing <c>.</c> or <c>/</c> is dropped.
    /// </summary>
    public static IReadOnlyList<string> EndpointPaths(string specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var section = Section(specification, "api contracts");
        if (section is null)
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var line in section)
        {
            foreach (Match match in PathPattern().Matches(line))
            {
                var path = match.Value.TrimEnd('.', '/', ',', ';', ':');
                if (path.Length > 1 && path.Any(char.IsAsciiLetter) && !paths.Contains(path, StringComparer.Ordinal))
                {
                    paths.Add(path);
                }
            }

            if (paths.Count >= MaxEntries)
            {
                break;
            }
        }

        return paths;
    }

    /// <summary>
    /// The dependency allowlist: the list items under a "Dependencies" heading inside the "Technical Architecture" section
    /// (the first word of each item, without backticks or a version such as <c>@5</c> or <c>==2.1</c>). <c>null</c> when the
    /// specification has no such list, so the allowlist check is skipped rather than flagging every dependency.
    /// </summary>
    public static IReadOnlyList<string>? AllowedDependencies(string specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var architecture = Section(specification, "technical architecture");
        var list = architecture is null ? null : Section(string.Join("\n", architecture.Skip(1)), "dependencies");
        if (list is null)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var line in list)
        {
            var item = ListItemPattern().Match(line);
            if (!item.Success)
            {
                continue;
            }

            if (DependencyName(item.Groups["text"].Value) is { } name && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }

            if (names.Count >= MaxEntries)
            {
                break;
            }
        }

        return names;
    }

    /// <summary>The package name at the start of a list item: <c>`express@5` — web framework</c> → <c>express</c>.</summary>
    public static string? DependencyName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cleaned = text.Replace("`", string.Empty, StringComparison.Ordinal).Replace("*", string.Empty, StringComparison.Ordinal).Trim();
        var token = cleaned.Split([' ', '\t', ',', '(', ':', ';'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null)
        {
            return null;
        }

        var at = token.IndexOf('@', 1);
        if (at > 0)
        {
            token = token[..at];
        }

        var version = token.IndexOfAny(['=', '<', '>', '~', '!', '^']);
        if (version > 0)
        {
            token = token[..version];
        }

        token = token.Trim().TrimEnd('.');
        return token.Length == 0 || !token.Any(char.IsAsciiLetter) ? null : token;
    }

    /// <summary>
    /// The lines of the first section whose heading contains <paramref name="title"/> (case-insensitive, numbering and
    /// emphasis ignored), from its heading up to the next heading of the same or a higher level (subsections included);
    /// <c>null</c> if none.
    /// </summary>
    internal static IReadOnlyList<string>? Section(string markdown, string title)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var inFence = false;
        int? level = null;
        var body = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var fence = line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal);
            if (fence)
            {
                inFence = !inFence;
            }

            var heading = inFence || fence ? Match.Empty : HeadingPattern().Match(line.Trim());
            if (heading.Success)
            {
                var headingLevel = heading.Groups["hashes"].Value.Length;
                if (level is { } open && headingLevel <= open)
                {
                    break;
                }

                if (level is null && Normalise(heading.Groups["title"].Value).Contains(title, StringComparison.Ordinal))
                {
                    level = headingLevel;
                }
            }

            if (level is not null)
            {
                body.Add(line);
            }
        }

        return level is null ? null : body;
    }

    private static string Normalise(string title) =>
        title.Replace("*", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<title>.+?)\s*#*\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.:/\-\\])/[A-Za-z0-9_\-.~{}:<>\[\]]+(?:/[A-Za-z0-9_\-.~{}:<>\[\]]+)*/?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+(?<text>.+)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ListItemPattern();
}

/// <summary>
/// §4.7: the dependencies the code declares, read from the package manifests found in the repository (npm
/// <c>package.json</c>, pip <c>requirements*.txt</c>, NuGet <c>*.csproj</c>, Go <c>go.mod</c>, Cargo <c>Cargo.toml</c>,
/// Bundler <c>Gemfile</c>, Maven <c>pom.xml</c>). Vendored folders (<c>node_modules/</c>, <c>vendor/</c>) are ignored, and
/// an unreadable manifest contributes nothing.
/// </summary>
public static partial class DependencyManifests
{
    public static IReadOnlyList<string> Used(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var names = new List<string>();
        foreach (var file in files)
        {
            var path = PathRules.Normalize(file.Path);
            if (path.Split('/').Any(s => s is "node_modules" or "vendor"))
            {
                continue;
            }

            var name = path.Split('/')[^1];
            IEnumerable<string> found = name switch
            {
                "package.json" => Npm(file.Content),
                "go.mod" => GoModules(file.Content),
                "Cargo.toml" => Cargo(file.Content),
                "Gemfile" => GemPattern().Matches(file.Content).Select(m => m.Groups["name"].Value),
                "pom.xml" => MavenPattern().Matches(file.Content).Select(m => m.Groups["name"].Value.Trim()),
                _ when name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    => NuGetPattern().Matches(file.Content).Select(m => m.Groups["name"].Value.Trim()),
                _ when name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    => Pip(file.Content),
                _ => [],
            };
            foreach (var dependency in found)
            {
                if (dependency.Length > 0 && !names.Contains(dependency, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(dependency);
                }
            }
        }

        return names;
    }

    private static List<string> Npm(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var result = new List<string>();
            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
            {
                if (doc.RootElement.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
                {
                    result.AddRange(deps.EnumerateObject().Select(p => p.Name));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<string> Pip(string content) =>
        content.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('-'))
            .Select(l => l.Split(['[', '<', '>', '=', '!', '~', ';', '@', ' '], 2)[0].Trim())
            .Where(n => n.Length > 0);

    private static List<string> GoModules(string content)
    {
        var result = new List<string>();
        var inBlock = false;
        foreach (var raw in content.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Split("//", 2)[0].Trim();
            if (line.StartsWith("require (", StringComparison.Ordinal) || line == "require(")
            {
                inBlock = true;
                continue;
            }

            if (inBlock && line == ")")
            {
                inBlock = false;
                continue;
            }

            var spec = inBlock ? line : line.StartsWith("require ", StringComparison.Ordinal) ? line["require ".Length..].Trim() : null;
            if (spec?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var module, ..])
            {
                result.Add(module);
            }
        }

        return result;
    }

    private static List<string> Cargo(string content)
    {
        var result = new List<string>();
        var inDependencies = false;
        foreach (var raw in content.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Split('#', 2)[0].Trim();
            if (line.StartsWith('['))
            {
                inDependencies = line is "[dependencies]" or "[dev-dependencies]" or "[build-dependencies]";
                continue;
            }

            if (inDependencies && line.IndexOf('=', StringComparison.Ordinal) is > 0 and var eq)
            {
                result.Add(line[..eq].Trim().Trim('"'));
            }
        }

        return result;
    }

    [GeneratedRegex(@"^\s*gem\s+['""](?<name>[^'""]+)['""]", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GemPattern();

    [GeneratedRegex(@"<PackageReference\s+[^>]*?Include\s*=\s*""(?<name>[^""]+)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NuGetPattern();

    [GeneratedRegex(@"<dependency>(?:(?!</dependency>).)*?<artifactId>(?<name>[^<]+)</artifactId>", RegexOptions.CultureInvariant | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MavenPattern();
}

/// <summary>A file in the reviewed tree (its repository path and size in bytes).</summary>
public sealed record RepositoryFile(string Path, long Size);

/// <summary>Bounds on what a review reads (NFR: a review never loads an unbounded repository).</summary>
public sealed record ReviewLimits(int MaxFiles, long MaxFileBytes, long MaxTotalBytes, int MaxPromptChars, int MaxListedPaths)
{
    public static ReviewLimits Default { get; } = new(MaxFiles: 400, MaxFileBytes: CodeGeneration.MaxExistingFileBytes, MaxTotalBytes: 2_000_000, MaxPromptChars: 200_000, MaxListedPaths: 2000);
}

/// <summary>
/// FR-38: which files of the project branch a review reads (changed files first, then the rest in path order, each within
/// the size limit, until the count or total budget is used), and how the code is presented to the review agent: the file
/// tree, the files changed against the default branch, and the contents that fit the prompt budget.
/// </summary>
public static class ReviewCode
{
    public static IReadOnlyList<string> SelectForReading(IReadOnlyList<RepositoryFile> tree, IReadOnlyCollection<string> changed, ReviewLimits limits)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(limits);
        var changedSet = changed.ToHashSet(StringComparer.Ordinal);
        var selected = new List<string>();
        long total = 0;
        foreach (var file in tree
            .Where(f => f.Size <= limits.MaxFileBytes)
            .OrderBy(f => changedSet.Contains(f.Path) ? 0 : 1)
            .ThenBy(f => f.Path, StringComparer.Ordinal))
        {
            if (selected.Count >= limits.MaxFiles)
            {
                break;
            }

            if (total + file.Size > limits.MaxTotalBytes)
            {
                continue;
            }

            selected.Add(file.Path);
            total += file.Size;
        }

        return selected;
    }

    /// <summary>The <c>{{CODE}}</c> input: tree, changed files and as many file contents (changed first) as fit the budget.</summary>
    /// <summary>
    /// The files as they may be shown to the review agent: every secret the secret scan (§4.7) would flag is replaced by a
    /// <c>[REDACTED:&lt;pattern&gt;]</c> marker, line for line, so the finding's line numbers still match (NFR-SEC).
    /// </summary>
    public static IReadOnlyList<SourceFile> RedactSecrets(IReadOnlyList<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return [.. files.Select(f => f with { Content = SecretScanner.Redact(f.Content) })];
    }

    public static string RenderForPrompt(
        IReadOnlyList<RepositoryFile> tree, IReadOnlyDictionary<string, string> changedStatus, IReadOnlyList<SourceFile> contents, ReviewLimits limits)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(changedStatus);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(limits);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"## Repository files ({tree.Count})\n");
        if (tree.Count == 0)
        {
            sb.Append("(the project branch has no files)\n");
        }

        foreach (var file in tree.Take(limits.MaxListedPaths))
        {
            sb.Append("- ").Append(file.Path);
            if (changedStatus.TryGetValue(file.Path, out var status))
            {
                sb.Append(" (").Append(status).Append(')');
            }

            sb.Append('\n');
        }

        if (tree.Count > limits.MaxListedPaths)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- … {tree.Count - limits.MaxListedPaths} more not listed\n");
        }

        var deleted = changedStatus.Where(kv => kv.Value == "deleted").Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        if (deleted.Count > 0)
        {
            sb.Append("\nDeleted on the project branch: ").Append(string.Join(", ", deleted.Take(100))).Append('\n');
        }

        sb.Append("\n## File contents\n");
        var omitted = new List<string>();
        foreach (var file in contents.OrderBy(f => changedStatus.ContainsKey(f.Path) ? 0 : 1).ThenBy(f => f.Path, StringComparer.Ordinal))
        {
            var fence = Fence(file.Content);
            var block = $"\n### {file.Path}\n{fence}\n{file.Content.TrimEnd('\n')}\n{fence}\n";
            if (sb.Length + block.Length > limits.MaxPromptChars)
            {
                omitted.Add(file.Path);
                continue;
            }

            sb.Append(block);
        }

        var unread = tree.Count - contents.Count;
        if (omitted.Count > 0 || unread > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n({omitted.Count + Math.Max(0, unread)} file(s) not shown: over the review's size budget, binary or too large.)\n");
        }

        return sb.ToString();
    }

    /// <summary>The <c>{{DETERMINISTIC_FINDINGS?}}</c> input: one line per finding, or a note that there are none.</summary>
    public static string RenderFindings(IReadOnlyList<ReviewFinding> findings, bool allowlistChecked, int endpointCount)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Checks run: secret scan; {(allowlistChecked ? "dependency allowlist" : "dependency allowlist skipped (the specification lists no dependencies)")}; {endpointCount} specification endpoint path(s).\n");
        if (findings.Count == 0)
        {
            sb.Append("No findings.");
        }

        foreach (var f in findings.Take(100))
        {
            sb.Append(CultureInfo.InvariantCulture, $"- [{f.Category.ToWire()}/{f.Severity.ToWire()}] {f.Message}{(f.Path is null ? string.Empty : $" ({f.Path})")}\n");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Fence(string content)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in content)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return new string('`', Math.Max(3, longest + 1));
    }
}
