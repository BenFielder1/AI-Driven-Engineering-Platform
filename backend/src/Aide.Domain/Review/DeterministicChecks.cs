using System.Text.RegularExpressions;
using Aide.Domain.Execution;

namespace Aide.Domain.Review;

/// <summary>A source file as the review checks see it.</summary>
public sealed record SourceFile(string Path, string Content);

/// <summary>
/// §4.7 / D8: stack-agnostic deterministic review checks. Each returns findings that
/// <see cref="ComplianceScoring"/> turns into penalties.
/// </summary>
public static partial class DeterministicChecks
{
    /// <summary>Security: every secret-pattern hit is a critical finding (FR-246: id <c>finding:secret:&lt;path&gt;-&lt;line&gt;</c>).</summary>
    public static IReadOnlyList<ReviewFinding> SecretScan(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return
        [
            .. files.SelectMany(f => SecretScanner.Scan(f.Content, PathRules.Normalize(f.Path)))
                .Select(s => new ReviewFinding(ReviewCategory.Security, Severity.Critical, $"Possible {s.Pattern} at line {s.Line}.", s.Path)
                {
                    FindingId = FindingIds.ForSecret(s.Path ?? string.Empty, s.Line),
                }),
        ];
    }

    /// <summary>
    /// Architecture: a dependency the code uses that isn't on the spec's allowlist is a major finding.
    /// Names compare case-insensitively. FR-246: id <c>finding:dependency:&lt;package&gt;</c>.
    /// </summary>
    public static IReadOnlyList<ReviewFinding> DependencyAllowlist(IEnumerable<string> allowed, IEnumerable<string> used)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(used);
        var allow = allowed.Select(a => a.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. used.Select(u => u.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(u => u.Length > 0 && !allow.Contains(u))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(u => new ReviewFinding(ReviewCategory.Architecture, Severity.Major, $"Dependency '{u}' is not in the specification's architecture.")
                {
                    FindingId = FindingIds.ForDependency(u),
                }),
        ];
    }

    /// <summary>
    /// API contracts: every endpoint path from the spec must be served by the code (major finding otherwise, FR-246 id
    /// <c>finding:endpoint:&lt;path&gt;</c>). A path is covered when it appears in some file's text (parameter segments
    /// <c>:id</c>, <c>{id}</c>, <c>&lt;id&gt;</c> match any parameter syntax), or (FR-252, §4.45) when a file-based route or a
    /// static file in <paramref name="files"/> or <paramref name="repositoryPaths"/> serves it. FR-251, §4.44: a catch-all
    /// path with prefix P is covered by a route whose text is P followed by a catch-all marker, or a file route with prefix
    /// P and a catch-all segment; a root catch-all also by any fallback (<see cref="RouteFallbacks"/>).
    /// </summary>
    /// <param name="specEndpointPaths">The spec's endpoint paths (<see cref="SpecReading.EndpointPaths"/>).</param>
    /// <param name="files">The files read for review (path and content).</param>
    /// <param name="repositoryPaths">Every path in the reviewed tree, also those not read (file routes only need the path).</param>
    public static IReadOnlyList<ReviewFinding> EndpointCoverage(
        IEnumerable<string> specEndpointPaths, IEnumerable<SourceFile> files, IEnumerable<string>? repositoryPaths = null)
    {
        ArgumentNullException.ThrowIfNull(specEndpointPaths);
        ArgumentNullException.ThrowIfNull(files);
        var fileList = files.ToList();
        var contents = fileList.Select(f => f.Content).ToList();
        List<string> paths = [.. fileList.Select(f => f.Path), .. repositoryPaths ?? []];
        var routes = FileRoutes.From(paths);
        var routeFiles = new Lazy<List<string>>(() => [.. fileList.Where(f => !RouteFallbacks.IsPlatformConfig(f.Path)).Select(f => f.Content)]);
        bool? fallback = null;
        return
        [
            .. specEndpointPaths
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Where(p =>
                {
                    var segments = RoutePaths.Segments(p);
                    var catchAll = RoutePaths.CatchAllIndex(segments);
                    if (catchAll < 0)
                    {
                        var pattern = EndpointPattern(p);
                        return !contents.Any(pattern.IsMatch) && !routes.Any(r => r.Matches(segments));
                    }

                    var prefix = segments.Take(catchAll).ToList();
                    var marker = CatchAllPattern(prefix);
                    // Platform config files (a vercel.json headers source "/(.*)") count only through their fallback rules.
                    if (routeFiles.Value.Any(marker.IsMatch) || routes.Any(r => r.IsCatchAllUnder(prefix)))
                    {
                        return false;
                    }

                    return prefix.Count > 0 || !(fallback ??= RouteFallbacks.Any(fileList, paths, routes));
                })
                .Select(p => new ReviewFinding(ReviewCategory.ApiContracts, Severity.Major, $"Endpoint '{p}' from the specification was not found in the code.")
                {
                    FindingId = FindingIds.ForEndpoint(p),
                }),
        ];
    }

    internal static Regex EndpointPattern(string path)
    {
        var segments = RoutePaths.Segments(path);
        var parts = segments.Select(s => s.Kind == RouteSegmentKind.Literal ? Regex.Escape(s.Text) : ParameterText);
        var prefix = path.TrimStart().StartsWith('/') ? "/" : string.Empty;
        return new Regex(Regex.Escape(prefix) + string.Join("/", parts), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }

    /// <summary>
    /// §4.44: <paramref name="prefix"/> followed by a catch-all marker in a route's text. A root catch-all must be a whole
    /// quoted string in a route position (<see cref="RouteFallbacks.RouteContext"/>: <c>MapGet("/{*path}")</c>,
    /// <c>app.get('/*')</c>), so a <c>/*</c> comment, a <c>source: '/(.*)'</c> header rule or <c>addMapping("/**")</c> never counts.
    /// </summary>
    internal static Regex CatchAllPattern(IReadOnlyList<RouteSegment> prefix)
    {
        var parts = string.Concat(prefix.Select(s => "/" + (s.Kind == RouteSegmentKind.Literal ? Regex.Escape(s.Text) : ParameterText)));
        var pattern = prefix.Count == 0
            ? $@"{RouteFallbacks.RouteContext}(['""`])/{CatchAllMarker}\1"
            : $@"{parts}/{CatchAllMarker}(?![\w}}\]])";
        return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }

    private const string ParameterText = @"[^/\s""'`]+";

    private const string CatchAllMarker = @"(?:\*{1,2}|\{\*{1,2}[^}/]*\}|\{[^}/*+]+[*+]\}|:\w+[*+]|\[\[?\.\.\.[^\]/]+\]\]?|\(\.\*\))";
}
