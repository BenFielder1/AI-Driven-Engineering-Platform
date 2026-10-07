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
    /// <summary>Security: every secret-pattern hit is a critical finding.</summary>
    public static IReadOnlyList<ReviewFinding> SecretScan(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return
        [
            .. files.SelectMany(f => SecretScanner.Scan(f.Content, PathRules.Normalize(f.Path)))
                .Select(s => new ReviewFinding(ReviewCategory.Security, Severity.Critical, $"Possible {s.Pattern} at line {s.Line}.", s.Path)),
        ];
    }

    /// <summary>
    /// Architecture: a dependency the code uses that isn't on the spec's allowlist is a major finding.
    /// Names compare case-insensitively.
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
                .Select(u => new ReviewFinding(ReviewCategory.Architecture, Severity.Major, $"Dependency '{u}' is not in the specification's architecture.")),
        ];
    }

    /// <summary>
    /// API contracts: every endpoint path from the spec must appear somewhere in the code (major finding
    /// otherwise). Parameter segments (<c>:id</c>, <c>{id}</c>, <c>&lt;id&gt;</c>) match any parameter syntax.
    /// </summary>
    public static IReadOnlyList<ReviewFinding> EndpointCoverage(IEnumerable<string> specEndpointPaths, IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(specEndpointPaths);
        ArgumentNullException.ThrowIfNull(files);
        var contents = files.Select(f => f.Content).ToList();
        return
        [
            .. specEndpointPaths
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Where(p =>
                {
                    var pattern = EndpointPattern(p);
                    return !contents.Any(pattern.IsMatch);
                })
                .Select(p => new ReviewFinding(ReviewCategory.ApiContracts, Severity.Major, $"Endpoint '{p}' from the specification was not found in the code.")),
        ];
    }

    internal static Regex EndpointPattern(string path)
    {
        var segments = path.Trim('/').Split('/');
        var parts = segments.Select(s => ParameterSegment().IsMatch(s) ? @"[^/\s""'`]+" : Regex.Escape(s));
        var prefix = path.StartsWith('/') ? "/" : string.Empty;
        return new Regex(Regex.Escape(prefix) + string.Join("/", parts), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }

    [GeneratedRegex(@"^(?::\w+|\{[^}]+\}|<[^>]+>|\[[^\]]+\])$", RegexOptions.None, 250)]
    private static partial Regex ParameterSegment();
}
