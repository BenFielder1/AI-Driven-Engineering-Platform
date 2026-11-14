using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aide.Domain.Execution;

namespace Aide.Domain.Review;

/// <summary>§4.44: what a path segment is.</summary>
public enum RouteSegmentKind
{
    Literal,
    Param,
    CatchAll,
    OptionalCatchAll,
}

/// <summary>A path segment and its kind (§4.44).</summary>
public readonly record struct RouteSegment(RouteSegmentKind Kind, string Text)
{
    public bool IsCatchAll => Kind is RouteSegmentKind.CatchAll or RouteSegmentKind.OptionalCatchAll;

    public override string ToString() => Kind switch
    {
        RouteSegmentKind.Param => "{param}",
        RouteSegmentKind.CatchAll => "{catch-all}",
        RouteSegmentKind.OptionalCatchAll => "{catch-all?}",
        _ => Text,
    };
}

/// <summary>
/// FR-251, §4.44: endpoint paths as segments. A segment is literal, a <b>param</b> (<c>:x</c>, <c>{x}</c>, <c>{x:…}</c>,
/// <c>{x?}</c>, <c>&lt;x&gt;</c>, <c>[x]</c>) or a <b>catch-all</b> (<c>*</c>, <c>**</c>, <c>{*x}</c>, <c>{**x}</c>,
/// <c>{x*}</c>, <c>{x+}</c>, <c>:x*</c>, <c>:x+</c>, <c>[...x]</c>, <c>[[...x]]</c>, <c>(.*)</c>). Pure.
/// </summary>
public static partial class RoutePaths
{
    /// <summary>The segments of <paramref name="path"/>: split on <c>/</c> outside braces, without a trailing <c>/</c> or a query string.</summary>
    public static IReadOnlyList<RouteSegment> Segments(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return [.. Split(WithoutQuery(path.Trim())).Select(Classify)];
    }

    /// <summary>Whether <paramref name="text"/> (one segment) is a param, a catch-all or a literal.</summary>
    public static RouteSegment Classify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (OptionalCatchAllSegment().IsMatch(text))
        {
            return new(RouteSegmentKind.OptionalCatchAll, text);
        }

        if (CatchAllSegment().IsMatch(text))
        {
            return new(RouteSegmentKind.CatchAll, text);
        }

        return ParamSegment().IsMatch(text) ? new(RouteSegmentKind.Param, text) : new(RouteSegmentKind.Literal, text);
    }

    /// <summary>Whether <paramref name="path"/> has a <c>{</c>, <c>[</c> or <c>&lt;</c> that is never closed (§4.44: such a path is dropped).</summary>
    public static bool HasUnclosedBracket(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Unclosed(path, '{', '}') || Unclosed(path, '[', ']') || Unclosed(path, '<', '>');
    }

    /// <summary>The index of the first catch-all segment, or <c>-1</c>.</summary>
    public static int CatchAllIndex(IReadOnlyList<RouteSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].IsCatchAll)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Splits on <c>/</c> outside braces, dropping empty segments (leading, trailing and doubled <c>/</c>).</summary>
    internal static List<string> Split(string path)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        foreach (var c in path)
        {
            depth += c == '{' ? 1 : c == '}' && depth > 0 ? -1 : 0;
            if (c == '/' && depth == 0)
            {
                if (current.Length > 0)
                {
                    segments.Add(current.ToString());
                }

                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        return segments;
    }

    /// <summary>The path without a query string: cut at the first <c>?</c> outside braces that has text after it.</summary>
    internal static string WithoutQuery(string path)
    {
        var depth = 0;
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            depth += c == '{' ? 1 : c == '}' && depth > 0 ? -1 : 0;
            if (c == '?' && depth == 0 && i + 1 < path.Length && path[i + 1] != '/')
            {
                return path[..i];
            }
        }

        return path;
    }

    private static bool Unclosed(string path, char open, char close)
    {
        var depth = 0;
        foreach (var c in path)
        {
            if (c == open)
            {
                depth++;
            }
            else if (c == close && depth > 0)
            {
                depth--;
            }
        }

        return depth > 0;
    }

    [GeneratedRegex(@"^\[\[\.\.\.[^\]/]+\]\]$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex OptionalCatchAllSegment();

    [GeneratedRegex(@"^(?:\*{1,2}|\{\*{1,2}[^}]*\}|\{[^}*+]+[*+]\}|:\w+[*+]|\[\.\.\.[^\]/]+\]|\(\.\*\))$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex CatchAllSegment();

    [GeneratedRegex(@"^(?::\w+\??|\{[^}]+\}|<[^>]+>|\[\[?[^\]]+\]?\])$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex ParamSegment();
}

/// <summary>A route built from a file path (§4.45).</summary>
public sealed record FileRoute(string File, IReadOnlyList<RouteSegment> Segments)
{
    public override string ToString() => "/" + string.Join("/", Segments.Select(s => s.ToString()));

    /// <summary>
    /// §4.45: whether this route serves the non-catch-all spec path <paramref name="spec"/>: the same number of segments, a
    /// spec literal matching only the same literal and a spec param a file param or literal. A file catch-all never serves a
    /// non-catch-all spec path (a <c>[...path]</c> proxy would cover every endpoint); it covers only a catch-all spec path
    /// with its prefix (<see cref="IsCatchAllUnder"/>).
    /// </summary>
    public bool Matches(IReadOnlyList<RouteSegment> spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (Segments.Count != spec.Count || Segments.Any(s => s.IsCatchAll))
        {
            return false;
        }

        for (var i = 0; i < spec.Count; i++)
        {
            if (!SegmentMatches(spec[i], Segments[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>§4.44: whether this route is <paramref name="prefix"/> followed by a catch-all segment.</summary>
    public bool IsCatchAllUnder(IReadOnlyList<RouteSegment> prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (Segments.Count != prefix.Count + 1 || !Segments[^1].IsCatchAll)
        {
            return false;
        }

        for (var i = 0; i < prefix.Count; i++)
        {
            if (!SegmentMatches(prefix[i], Segments[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SegmentMatches(RouteSegment spec, RouteSegment file) => spec.Kind == RouteSegmentKind.Param
        ? file.Kind is RouteSegmentKind.Param or RouteSegmentKind.Literal
        : file.Kind == RouteSegmentKind.Literal && string.Equals(spec.Text, file.Text, StringComparison.Ordinal);
}

/// <summary>
/// FR-252, §4.45: the routes a repository's file paths serve, at any folder depth (so <c>frontend/src/app/…</c> counts).
/// <list type="bullet">
/// <item>Next.js App Router (<c>app/</c>): <c>page.{tsx,ts,jsx,js,mdx}</c> and <c>route.{ts,js}</c> give the folder path,
/// without <c>(group)</c> and <c>@slot</c> folders; a path with a <c>_private</c> folder is skipped. Metadata files at any
/// level: robots, sitemap, manifest, favicon, icon, apple-icon, opengraph-image and twitter-image.</item>
/// <item>Pages Router (<c>pages/</c>): <c>x.tsx</c> and <c>x/index.tsx</c> → <c>/x</c> (also .ts, .jsx, .js), without
/// <c>_app</c>, <c>_document</c> and <c>_error</c>.</item>
/// <item>Static files: <c>public/&lt;p&gt;</c> → <c>/&lt;p&gt;</c>.</item>
/// <item>SvelteKit (<c>src/routes/</c>): <c>+page.svelte</c> and <c>+server.{ts,js}</c> give the folder path.</item>
/// </list>
/// Pure.
/// </summary>
public static class FileRoutes
{
    private static readonly string[] PageExtensions = [".tsx", ".ts", ".jsx", ".js", ".mdx"];
    private static readonly string[] RouteExtensions = [".ts", ".js"];
    private static readonly string[] PagesRouterExtensions = [".tsx", ".ts", ".jsx", ".js"];
    private static readonly string[] SkippedPages = ["_app", "_document", "_error"];
    private static readonly string[] ImageMetadata = ["icon", "apple-icon", "opengraph-image", "twitter-image"];

    /// <summary>The routes of every path in <paramref name="paths"/> that serves one (distinct paths, in order).</summary>
    public static IReadOnlyList<FileRoute> From(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return [.. paths.Select(PathRules.Normalize).Distinct(StringComparer.Ordinal).Select(Of).OfType<FileRoute>()];
    }

    /// <summary>The route <paramref name="path"/> serves, or <c>null</c>.</summary>
    public static FileRoute? Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = PathRules.Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "node_modules" or ".next" or ".svelte-kit"))
        {
            return null;
        }

        var segments = AppRouter(parts) ?? PagesRouter(parts) ?? Public(parts) ?? SvelteKit(parts);
        return segments is null ? null : new FileRoute(path, segments);
    }

    private static List<RouteSegment>? AppRouter(string[] parts)
    {
        var app = Array.IndexOf(parts, "app");
        if (app < 0 || app == parts.Length - 1)
        {
            return null;
        }

        var folders = parts[(app + 1)..^1];
        var file = parts[^1];
        if (folders.Any(f => f.StartsWith('_')))
        {
            return null;
        }

        var route = Folders(folders, dropSlots: true);
        var (name, extension) = SplitName(file);
        if ((name == "page" && PageExtensions.Contains(extension, StringComparer.Ordinal))
            || (name == "route" && RouteExtensions.Contains(extension, StringComparer.Ordinal)))
        {
            return route;
        }

        var metadata = (name, extension) switch
        {
            ("robots", ".ts" or ".js" or ".txt") => "robots.txt",
            ("sitemap", ".ts" or ".js" or ".xml") => "sitemap.xml",
            ("manifest", ".ts" or ".js" or ".webmanifest") => "manifest.webmanifest",
            ("manifest", ".json") => "manifest.json",
            ("favicon", ".ico") => "favicon.ico",
            _ when extension.Length > 0 && ImageMetadata.Contains(name, StringComparer.Ordinal) => name,
            _ => null,
        };
        if (metadata is null)
        {
            return null;
        }

        route.Add(new RouteSegment(RouteSegmentKind.Literal, metadata));
        return route;
    }

    private static List<RouteSegment>? PagesRouter(string[] parts)
    {
        var pages = Array.IndexOf(parts, "pages");
        if (pages < 0 || pages == parts.Length - 1)
        {
            return null;
        }

        var (name, extension) = SplitName(parts[^1]);
        if (!PagesRouterExtensions.Contains(extension, StringComparer.Ordinal) || SkippedPages.Contains(name, StringComparer.Ordinal))
        {
            return null;
        }

        var route = Folders(parts[(pages + 1)..^1], dropSlots: false);
        if (name != "index")
        {
            route.Add(RoutePaths.Classify(name));
        }

        return route;
    }

    private static List<RouteSegment>? Public(string[] parts)
    {
        var index = Array.IndexOf(parts, "public");
        return index < 0 || index == parts.Length - 1
            ? null
            : [.. parts[(index + 1)..].Select(p => new RouteSegment(RouteSegmentKind.Literal, p))];
    }

    private static List<RouteSegment>? SvelteKit(string[] parts)
    {
        var routes = -1;
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i] == "src" && parts[i + 1] == "routes")
            {
                routes = i + 1;
                break;
            }
        }

        if (routes < 0 || routes == parts.Length - 1)
        {
            return null;
        }

        var file = parts[^1];
        return file is "+page.svelte" or "+server.ts" or "+server.js" ? Folders(parts[(routes + 1)..^1], dropSlots: false) : null;
    }

    private static List<RouteSegment> Folders(IEnumerable<string> folders, bool dropSlots) =>
    [
        .. folders
            .Where(f => !(f.StartsWith('(') && f.EndsWith(')')) && !(dropSlots && f.StartsWith('@')))
            .Select(RoutePaths.Classify),
    ];

    private static (string Name, string Extension) SplitName(string file)
    {
        var dot = file.LastIndexOf('.');
        return dot <= 0 ? (file, string.Empty) : (file[..dot], file[dot..]);
    }
}

/// <summary>
/// FR-251, §4.44: whether the code has a <b>fallback</b> that covers a root catch-all spec path: <c>MapFallback</c> or
/// <c>MapFallbackToFile</c>; a <c>'*'</c>, <c>'/*'</c> or <c>'/(.*)'</c> route; Next <c>app/[...x]</c>,
/// <c>app/[[...x]]</c>, <c>app/not-found.*</c> or <c>pages/404.*</c>; a SPA rewrite in <c>vercel.json</c> (source
/// <c>/(.*)</c> or <c>/:path*</c>); a <c>/*</c> redirect in <c>netlify.toml</c> or <c>_redirects</c>; or
/// <c>not_found_handling = "single-page-application"</c> in a wrangler file. Pure.
/// </summary>
public static partial class RouteFallbacks
{
    public static bool Any(IReadOnlyList<SourceFile> files, IReadOnlyList<string> paths, IReadOnlyList<FileRoute> fileRoutes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileRoutes);
        return fileRoutes.Any(r => r.Segments.Count == 1 && r.Segments[0].IsCatchAll)
            || paths.Any(IsFallbackPage)
            || files.Any(IsFallbackFile);
    }

    /// <summary>Whether the file's content declares a fallback (§4.44).</summary>
    public static bool IsFallbackFile(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var path = PathRules.Normalize(file.Path);
        var name = path.Split('/')[^1];
        return name switch
        {
            "vercel.json" => VercelRewrite(file.Content),
            "netlify.toml" => NetlifyRedirect().IsMatch(file.Content),
            "_redirects" => file.Content.ReplaceLineEndings("\n").Split('\n')
                .Any(l => l.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries) is [var from, ..] && from == "/*"),
            "wrangler.toml" or "wrangler.json" or "wrangler.jsonc" => WranglerSpa().IsMatch(file.Content),
            _ => FallbackCall().IsMatch(file.Content) || FallbackRoute().IsMatch(file.Content),
        };
    }

    /// <summary>Whether <paramref name="path"/> is a platform config file whose fallback rules <see cref="IsFallbackFile"/> reads.</summary>
    public static bool IsPlatformConfig(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return PathRules.Normalize(path).Split('/')[^1] is "vercel.json" or "netlify.toml" or "_redirects" or "wrangler.toml" or "wrangler.json" or "wrangler.jsonc";
    }

    private static bool IsFallbackPage(string path)
    {
        var parts = PathRules.Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var folder = parts[^2];
        var name = parts[^1];
        return (folder == "app" && name.StartsWith("not-found.", StringComparison.Ordinal))
            || (folder == "pages" && name.StartsWith("404.", StringComparison.Ordinal));
    }

    private static bool VercelRewrite(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("rewrites", out var rewrites)
                && rewrites.ValueKind == JsonValueKind.Array
                && rewrites.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.Object
                    && r.TryGetProperty("source", out var source)
                    && source.ValueKind == JsonValueKind.String
                    && source.GetString() is "/(.*)" or "/:path*");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"\bMapFallback(?:ToFile)?\s*[(<]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex FallbackCall();

    /// <summary>
    /// The text before a route string: a <c>path</c> value, or the first argument of a routing call. Middleware
    /// (<c>.use</c>), <c>addMapping</c> and a header rule's <c>source:</c> are not routes.
    /// </summary>
    public const string RouteContext = @"(?:\bpath\s*[:=]\s*\{?\s*|\.(?:get|post|put|delete|patch|all|route|on|any)\s*\(\s*(?:['""`][A-Z]+['""`]\s*,\s*)?|\bMap\w*\s*\(\s*)";

    /// <summary>A <c>'*'</c>, <c>'/*'</c> or <c>'/(.*)'</c> string used as a route (<see cref="RouteContext"/>).</summary>
    [GeneratedRegex(RouteContext + @"(['""`])(?:/?\*|/\(\.\*\))\1", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex FallbackRoute();

    [GeneratedRegex(@"\bfrom\s*=\s*['""]/\*['""]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex NetlifyRedirect();

    [GeneratedRegex(@"not_found_handling['""]?\s*[=:]\s*['""]single-page-application['""]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex WranglerSpa();
}
