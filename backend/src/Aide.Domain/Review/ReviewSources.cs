using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aide.Domain.Common;
using Aide.Domain.Execution;

namespace Aide.Domain.Review;

/// <summary>FR-251, §4.44: the spec's endpoint paths, and those dropped because a <c>{</c>, <c>[</c> or <c>&lt;</c> is never closed.</summary>
public sealed record SpecEndpointPaths(IReadOnlyList<string> Paths, IReadOnlyList<string> Dropped);

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
    /// starts with <c>/</c> (not inside a URL) and contains a letter or a <c>*</c>; trailing <c>.</c>, <c>/</c>, <c>,</c>,
    /// <c>;</c>, <c>:</c>, a sentence's <c>?</c> and Markdown emphasis <c>*</c> are dropped (FR-251, §4.44: never inside
    /// braces, so <c>{id:int}</c> and <c>{*any}</c> stay whole). A path left with an unclosed <c>{</c>, <c>[</c> or
    /// <c>&lt;</c> is dropped (<see cref="ReadEndpointPaths"/> returns it so the caller can warn).
    /// </summary>
    public static IReadOnlyList<string> EndpointPaths(string specification) => ReadEndpointPaths(specification).Paths;

    /// <summary><see cref="EndpointPaths"/>, plus the paths dropped for an unclosed bracket (§4.44).</summary>
    public static SpecEndpointPaths ReadEndpointPaths(string specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var section = Section(specification, "api contracts");
        if (section is null)
        {
            return new([], []);
        }

        var paths = new List<string>();
        var dropped = new List<string>();
        foreach (var line in section)
        {
            foreach (Match match in PathPattern().Matches(line))
            {
                var path = TrimPath(match.Value);
                if (path.Length <= 1 || !path.Any(c => char.IsAsciiLetter(c) || c == '*'))
                {
                    continue;
                }

                if (RoutePaths.HasUnclosedBracket(path))
                {
                    if (!dropped.Contains(path, StringComparer.Ordinal))
                    {
                        dropped.Add(path);
                    }

                    continue;
                }

                if (!paths.Contains(path, StringComparer.Ordinal))
                {
                    paths.Add(path);
                }
            }

            if (paths.Count >= MaxEntries)
            {
                break;
            }
        }

        return new(paths, dropped);
    }

    /// <summary>
    /// §4.44: drops trailing punctuation (<c>.</c>, <c>/</c>, <c>,</c>, <c>;</c>, <c>:</c>), a trailing <c>?</c> unless the last
    /// segment is an optional <c>:x?</c> param, and a trailing run of <c>*</c> that is Markdown emphasis (not a whole
    /// <c>*</c>/<c>**</c> segment or a <c>:x*</c> param). A <c>{…}</c> segment ends with <c>}</c>, so nothing inside braces is cut.
    /// </summary>
    private static string TrimPath(string value)
    {
        var path = value;
        while (true)
        {
            var trimmed = path.TrimEnd('.', '/', ',', ';', ':');
            var last = trimmed[(trimmed.LastIndexOf('/') + 1)..];
            if (trimmed.EndsWith(')') && trimmed.Count(c => c == ')') > trimmed.Count(c => c == '('))
            {
                // "(see /api/todos)": the sentence's closing parenthesis, not part of a (.*) segment.
                trimmed = trimmed[..^1];
            }
            else if (trimmed.EndsWith('?') && !last.StartsWith(':'))
            {
                trimmed = trimmed[..^1];
            }
            else if (trimmed.EndsWith('*') && last.TrimEnd('*').Length > 0 && !last.StartsWith(':'))
            {
                trimmed = trimmed.TrimEnd('*');
            }

            if (trimmed == path)
            {
                return path;
            }

            path = trimmed;
        }
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

    /// <summary>
    /// A path: <c>/</c> then segments of letters, digits and <c>_ - . ~ : &lt; &gt; [ ] * + ? =</c>, where a <c>{…}</c> is read
    /// up to its closing brace (FR-251, §4.44). A lone <c>{</c> is read too, so an unclosed one is detected and dropped.
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_.:/\-\\])/(?:\{[^{}\s]*\}|[A-Za-z0-9_\-.~:<>\[\]*+?=(){])+(?:/(?:\{[^{}\s]*\}|[A-Za-z0-9_\-.~:<>\[\]*+?=(){])+)*/?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+(?<text>.+)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ListItemPattern();
}

/// <summary>
/// §4.7: the dependencies the code declares, read from the package manifests found in the repository (npm
/// <c>package.json</c>, pip <c>requirements*.txt</c> and <c>pyproject.toml</c>, NuGet <c>*.csproj</c>, Go <c>go.mod</c>, Cargo
/// <c>Cargo.toml</c>, Bundler <c>Gemfile</c>, Maven <c>pom.xml</c>). Vendored folders (<c>node_modules/</c>, <c>vendor/</c>)
/// are ignored, and an unreadable manifest contributes nothing. FR-253, §4.46: runtime dependencies only; dev, test and
/// build-only dependencies are skipped.
/// </summary>
public static partial class DependencyManifests
{
    private static readonly string[] NpmRuntimeSections = ["dependencies", "peerDependencies", "optionalDependencies"];
    private static readonly string[] RequirementsDevWords = ["dev", "test", "tests", "lint", "doc", "docs"];
    private static readonly string[] PythonDevGroups = ["dev", "test", "tests", "lint", "docs"];

    public static IReadOnlyList<string> Used(IEnumerable<SourceFile> files) => [.. UsedIn(files).Keys];

    /// <summary>
    /// §4.43: each used dependency with the first manifest declaring it (its path, normalised), in the order of
    /// <see cref="Used"/>; names compare case-insensitively.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Manifests(IEnumerable<SourceFile> files) => UsedIn(files);

    private static OrderedDictionary<string, string> UsedIn(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var names = new OrderedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                "pyproject.toml" => PyProject(file.Content),
                "go.mod" => GoModules(file.Content),
                "Cargo.toml" => Cargo(file.Content),
                "Gemfile" => Gems(file.Content),
                "pom.xml" => Maven(file.Content),
                _ when name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    => NuGet(path, file.Content),
                _ when name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    => name.Split(['-', '_', '.']).Any(w => RequirementsDevWords.Contains(w, StringComparer.OrdinalIgnoreCase)) ? [] : Pip(file.Content),
                _ => [],
            };
            foreach (var dependency in found)
            {
                if (dependency.Length > 0)
                {
                    names.TryAdd(dependency, path);
                }
            }
        }

        return names;
    }

    /// <summary>§4.46: <c>dependencies</c>, <c>peerDependencies</c> and <c>optionalDependencies</c>; never <c>devDependencies</c>.</summary>
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
            foreach (var section in NpmRuntimeSections)
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
            .Select(RequirementName)
            .Where(n => n.Length > 0);

    private static string RequirementName(string requirement) =>
        requirement.Split(['[', '<', '>', '=', '!', '~', ';', '@', ' ', '('], 2)[0].Trim();

    /// <summary>
    /// §4.46: <c>[project].dependencies</c>, <c>[project.optional-dependencies]</c> groups other than dev, test, tests, lint
    /// and docs, and <c>[tool.poetry.dependencies]</c> (without <c>python</c>). <c>[dependency-groups]</c> and
    /// <c>[tool.poetry.group.*]</c> are skipped.
    /// </summary>
    private static List<string> PyProject(string content)
    {
        var result = new List<string>();
        string? table = null;
        var collecting = false;
        var continuation = 0;
        foreach (var raw in content.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (collecting)
            {
                collecting = CollectStrings(line, result);
                continue;
            }

            if (continuation > 0)
            {
                // The rest of a multi-line array or inline table (e.g. poetry's {version = "1", python = "<3.9"} constraints).
                continuation += BracketDepth(line);
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                table = line.Split('#', 2)[0].Trim().Trim('[', ']').Trim();
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim().Trim('"', '\'');
            var value = line[(eq + 1)..].Trim();
            var checkedArray = (table == "project" && key == "dependencies")
                || (table == "project.optional-dependencies" && !PythonDevGroups.Contains(key, StringComparer.OrdinalIgnoreCase));
            if (checkedArray && value.StartsWith('['))
            {
                collecting = CollectStrings(value[1..], result);
                continue;
            }

            continuation = Math.Max(0, BracketDepth(value));
            if (table == "tool.poetry.dependencies"
                && PoetryKeyPattern().IsMatch(line)
                && !string.Equals(key, "python", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(key);
            }
        }

        return result;
    }

    /// <summary>Opening minus closing <c>[</c>/<c>{</c> brackets outside strings and comments.</summary>
    private static int BracketDepth(string text)
    {
        var depth = 0;
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is { } q)
            {
                quote = c == q ? null : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '#')
            {
                break;
            }
            else
            {
                depth += c is '[' or '{' ? 1 : c is ']' or '}' ? -1 : 0;
            }
        }

        return depth;
    }

    /// <summary>Adds the requirement names of the TOML array strings in <paramref name="chunk"/>; <c>true</c> while the array is still open.</summary>
    private static bool CollectStrings(string chunk, List<string> into)
    {
        char? quote = null;
        var start = 0;
        for (var i = 0; i < chunk.Length; i++)
        {
            var c = chunk[i];
            if (quote is { } q)
            {
                if (c == q)
                {
                    var name = RequirementName(chunk[start..i]);
                    if (name.Length > 0)
                    {
                        into.Add(name);
                    }

                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                start = i + 1;
            }
            else if (c == '#')
            {
                return true;
            }
            else if (c == ']')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// §4.46: <c>PackageReference</c> items, except those with <c>PrivateAssets="all"</c>; nothing from a test project
    /// (<c>&lt;IsTestProject&gt;true</c>, under a <c>tests/</c> folder, or named <c>*Tests</c>).
    /// </summary>
    private static IEnumerable<string> NuGet(string path, string content)
    {
        var segments = path.Split('/');
        var file = segments[^1];
        var project = file[..file.LastIndexOf('.')];
        if (segments[..^1].Any(s => s.Equals("tests", StringComparison.OrdinalIgnoreCase))
            || project.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || TestProjectPattern().IsMatch(content))
        {
            return [];
        }

        return NuGetPattern().Matches(content)
            .Where(m => !PrivateAssetsAllPattern().IsMatch(m.Value))
            .Select(m => IncludePattern().Match(m.Groups["attrs"].Value))
            .Where(m => m.Success)
            .Select(m => m.Groups["name"].Value.Trim());
    }

    /// <summary>§4.46: every <c>&lt;dependency&gt;</c> except <c>&lt;scope&gt;test&lt;/scope&gt;</c>.</summary>
    private static IEnumerable<string> Maven(string content) =>
        MavenPattern().Matches(content)
            .Select(m => m.Groups["body"].Value)
            .Where(body => !MavenTestScopePattern().IsMatch(body))
            .Select(body => MavenArtifactPattern().Match(body))
            .Where(m => m.Success)
            .Select(m => m.Groups["name"].Value.Trim());

    /// <summary>§4.46: top-level gems; gems inside a <c>group</c> block naming <c>:development</c> or <c>:test</c> are skipped.</summary>
    private static List<string> Gems(string content)
    {
        var result = new List<string>();
        var depth = 0;
        int? skippedDepth = null;
        foreach (var raw in content.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Split('#', 2)[0].Trim();
            if (GemGroupPattern().Match(line) is { Success: true } group)
            {
                depth++;
                if (skippedDepth is null && GemDevGroupPattern().IsMatch(group.Groups["groups"].Value))
                {
                    skippedDepth = depth;
                }

                continue;
            }

            if (GemBlockPattern().IsMatch(line))
            {
                depth++;
                continue;
            }

            if (line == "end")
            {
                if (skippedDepth == depth)
                {
                    skippedDepth = null;
                }

                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (skippedDepth is null && GemPattern().Match(line) is { Success: true } gem)
            {
                result.Add(gem.Groups["name"].Value);
            }
        }

        return result;
    }

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
                inDependencies = line == "[dependencies]";
                continue;
            }

            if (inDependencies && line.IndexOf('=', StringComparison.Ordinal) is > 0 and var eq)
            {
                result.Add(line[..eq].Trim().Trim('"'));
            }
        }

        return result;
    }

    [GeneratedRegex(@"^\s*gem\s+['""](?<name>[^'""]+)['""]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GemPattern();

    [GeneratedRegex(@"^group\s*\(?(?<groups>.*?)\)?\s+do\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GemGroupPattern();

    [GeneratedRegex(@"[:'""](?:development|test)\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GemDevGroupPattern();

    [GeneratedRegex(@"\bdo(?:\s*\|[^|]*\|)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GemBlockPattern();

    [GeneratedRegex(@"<PackageReference\b(?<attrs>[^>]*?)(?:/>|>(?<body>(?:(?!</PackageReference>).)*?)</PackageReference>)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NuGetPattern();

    [GeneratedRegex(@"\bInclude\s*=\s*""(?<name>[^""]+)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IncludePattern();

    [GeneratedRegex(@"\bPrivateAssets\s*=\s*""\s*all\s*""|<PrivateAssets>\s*all\s*</PrivateAssets>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PrivateAssetsAllPattern();

    [GeneratedRegex(@"<IsTestProject>\s*true\s*</IsTestProject>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TestProjectPattern();

    [GeneratedRegex(@"<dependency>(?<body>(?:(?!</dependency>).)*?)</dependency>", RegexOptions.CultureInvariant | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MavenPattern();

    [GeneratedRegex(@"<artifactId>(?<name>[^<]+)</artifactId>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MavenArtifactPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\s*=", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PoetryKeyPattern();

    [GeneratedRegex(@"<scope>\s*test\s*</scope>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MavenTestScopePattern();
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

    /// <summary>
    /// The <c>{{DETERMINISTIC_FINDINGS?}}</c> input: one line per finding, or a note that there are none. FR-237: a failure
    /// finding (<see cref="ReviewFinding.FailureId"/>) also shows its failure id and its evidence excerpt in a fenced block;
    /// FR-240: a failure in <paramref name="repeats"/> is marked <c>repeated (n cycles)</c> with the previous cycle's fix step
    /// text. FR-246: another finding with a <see cref="ReviewFinding.FindingId"/> starts with it
    /// (<c>- finding:dependency:lodash [architecture/major] …</c>); one without renders as in 0.6.0. FR-247: a finding an
    /// earlier waiver covers (<paramref name="marks"/>) ends with <c>(waived in cycle n: &lt;reason&gt;)</c>, and one the
    /// previous cycle left unaddressed with <c>— carried (n cycles)</c>.
    /// </summary>
    public static string RenderFindings(
        IReadOnlyList<ReviewFinding> findings,
        bool allowlistChecked,
        int endpointCount,
        IReadOnlyDictionary<string, FailureRepeat>? repeats = null,
        FindingMarks? marks = null)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Checks run: secret scan; {(allowlistChecked ? "dependency allowlist" : "dependency allowlist skipped (the specification lists no dependencies)")}; {endpointCount} specification endpoint path(s).\n");
        if (findings.Count == 0)
        {
            sb.Append("No findings.");
        }

        // Failure findings first, so the 100-finding cap never drops one (other findings keep their order).
        foreach (var f in findings.Where(f => f.FailureId is not null).Concat(findings.Where(f => f.FailureId is null)).Take(100))
        {
            if (f.FailureId is { } id)
            {
                RenderFailure(sb, f, id, repeats is not null && repeats.TryGetValue(id, out var repeat) ? repeat : null);
                continue;
            }

            sb.Append(CultureInfo.InvariantCulture, $"- {(f.FindingId is { } findingId ? findingId + " " : string.Empty)}[{f.Category.ToWire()}/{f.Severity.ToWire()}] {f.Message}{(f.Path is null ? string.Empty : $" ({f.Path})")}");
            if (f.FindingId is { } marked && marks is not null)
            {
                if (marks.Waived.TryGetValue(marked, out var waiver))
                {
                    sb.Append(CultureInfo.InvariantCulture, $" (waived in cycle {waiver.Cycle}: {waiver.Reason.ReplaceLineEndings(" ")})");
                }
                else if (marks.Carried.TryGetValue(marked, out var cycles))
                {
                    sb.Append(CultureInfo.InvariantCulture, $" — carried ({cycles} {(cycles == 1 ? "cycle" : "cycles")})");
                }
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static void RenderFailure(StringBuilder sb, ReviewFinding f, string id, FailureRepeat? repeat)
    {
        sb.Append(CultureInfo.InvariantCulture, $"- [{f.Category.ToWire()}/{f.Severity.ToWire()}] failure id {id} ({FailureIds.SourceLabel(id)}): {f.Message}");
        if (f.Path is not null)
        {
            sb.Append(" (").Append(f.Path).Append(')');
        }

        if (repeat is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $" — repeated ({repeat.Cycles} cycles)\n");
            sb.Append("  The same error failed the previous cycle too, so its fix did not work: plan a different approach and say in the step what was tried.\n");
            if (repeat.PreviousStepText is { } previous)
            {
                var previousFence = Fence(previous);
                sb.Append("  Previous fix step:\n").Append(previousFence).Append('\n').Append(previous.TrimEnd('\n')).Append('\n').Append(previousFence).Append('\n');
            }
        }
        else
        {
            sb.Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(f.Evidence))
        {
            var fence = Fence(f.Evidence);
            sb.Append("  Evidence excerpt:\n").Append(fence).Append('\n').Append(f.Evidence.TrimEnd('\n')).Append('\n').Append(fence).Append('\n');
        }
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
