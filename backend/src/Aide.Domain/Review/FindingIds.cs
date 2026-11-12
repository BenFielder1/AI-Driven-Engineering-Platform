using System.Globalization;
using System.Text.RegularExpressions;

namespace Aide.Domain.Review;

/// <summary>
/// FR-246, §4.42: the stable finding id of a deterministic (non-failure) finding, <c>finding:&lt;kind&gt;:&lt;subject&gt;</c>.
/// The subject is a slug: lower-cased, every run of characters outside <c>a-z0-9</c> becomes <c>-</c>, <c>-</c> is trimmed
/// from both ends, it is cut to 60 characters, and <c>root</c> is used when nothing is left. The id depends only on the kind
/// and subject, so it is the same in every cycle. Failure findings keep their <c>failure:</c> ids (<see cref="FailureIds"/>).
/// Pure.
/// </summary>
public static partial class FindingIds
{
    public const string Prefix = "finding:";

    /// <summary>A dependency outside the specification's allowlist; subject: the package name.</summary>
    public const string Dependency = "dependency";

    /// <summary>A specification endpoint missing from the code; subject: the spec path.</summary>
    public const string Endpoint = "endpoint";

    /// <summary>A secret-scan hit; subject: path and line.</summary>
    public const string Secret = "secret";

    /// <summary>A stack review check; subject: check key and path (or <c>repo</c>).</summary>
    public const string Stack = "stack";

    /// <summary>A Vercel config finding; subject: rule and path.</summary>
    public const string Vercel = "vercel";

    /// <summary>The longest subject kept in an id.</summary>
    public const int MaxSubjectLength = 60;

    /// <summary>The subject of a stack finding without a path.</summary>
    public const string RepoSubject = "repo";

    public static string Of(string kind, string subject)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(subject);
        return Prefix + kind + ":" + Slug(subject);
    }

    public static string ForDependency(string packageName) => Of(Dependency, packageName);

    public static string ForEndpoint(string specPath) => Of(Endpoint, specPath);

    public static string ForSecret(string path, int line) => Of(Secret, string.Create(CultureInfo.InvariantCulture, $"{path} {line}"));

    public static string ForStack(string checkKey, string? path) => Of(Stack, $"{checkKey} {(string.IsNullOrWhiteSpace(path) ? RepoSubject : path)}");

    public static string ForVercel(string rule, string path) => Of(Vercel, $"{rule} {path}");

    /// <summary>The §4.42 slug of <paramref name="subject"/>.</summary>
    public static string Slug(string subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var slug = NonSlugRun().Replace(subject.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > MaxSubjectLength)
        {
            // A cut never leaves a dangling '-' (it would read as the start of a -2 suffix).
            slug = slug[..MaxSubjectLength].TrimEnd('-');
        }

        return slug.Length == 0 ? "root" : slug;
    }

    /// <summary>
    /// FR-246: the findings with every finding id unique in the review: a second (third, …) finding with the same id, in
    /// report order, gets <c>-2</c> (<c>-3</c>, …). Findings without a finding id are unchanged.
    /// </summary>
    public static IReadOnlyList<ReviewFinding> Disambiguate(IReadOnlyList<ReviewFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ReviewFinding>(findings.Count);
        foreach (var finding in findings)
        {
            if (finding.FindingId is not { } id || used.Add(id))
            {
                result.Add(finding);
                continue;
            }

            var n = 2;
            while (!used.Add(string.Create(CultureInfo.InvariantCulture, $"{id}-{n}")))
            {
                n++;
            }

            result.Add(finding with { FindingId = string.Create(CultureInfo.InvariantCulture, $"{id}-{n}") });
        }

        return result;
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonSlugRun();
}
