using System.Globalization;
using System.Text.RegularExpressions;
using Aide.Domain.Execution;
using Aide.Domain.Presets;

namespace Aide.Domain.Review;

/// <summary>
/// FR-77: the deterministic preset rules (<c>review.deterministic</c>, <see cref="PresetVocabulary.DeterministicRules"/>).
/// Language-independent text checks over the reviewed files; every finding is a critical security finding, so it carries
/// the §4.7 penalty (−0.20) like the v1 deterministic checks.
/// <list type="bullet">
/// <item><see cref="RlsOnEveryTable"/>: every <c>create table &lt;name&gt;</c> in <c>supabase/migrations/**</c> has a matching
/// <c>alter table [public.]&lt;name&gt; enable row level security</c> in some migration (case-insensitive; SQL comments are
/// ignored; temporary tables are not tables of the schema).</item>
/// <item><see cref="NoSecretEnvInClient"/>: a file whose first statement is the <c>"use client"</c> directive references no
/// <c>process.env.X</c> where X is <c>SUPABASE_SERVICE_ROLE_KEY</c> or doesn't start with <c>NEXT_PUBLIC_</c> (only
/// <c>NEXT_PUBLIC_*</c> values reach the browser; <c>NODE_ENV</c>, which Next.js inlines and is no secret, is allowed).</item>
/// </list>
/// </summary>
public static partial class PresetReviewChecks
{
    /// <summary>Where Supabase migrations live (§4.12).</summary>
    public const string MigrationsFolder = "supabase/migrations/";

    /// <summary>The service-role key: never in client code, even if someone prefixes it (§4.12).</summary>
    public const string ServiceRoleKey = "SUPABASE_SERVICE_ROLE_KEY";

    /// <summary>The only env prefix Next.js ships to the browser.</summary>
    public const string PublicPrefix = "NEXT_PUBLIC_";

    /// <summary>The findings of every deterministic rule the preset lists (none without a preset).</summary>
    public static IReadOnlyList<ReviewFinding> Run(Preset? preset, IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (preset is null)
        {
            return [];
        }

        var list = files.ToList();
        var findings = new List<ReviewFinding>();
        foreach (var rule in preset.Review.Deterministic)
        {
            findings.AddRange(rule switch
            {
                PresetVocabulary.RlsOnEveryTable => RlsOnEveryTable(list),
                PresetVocabulary.NoSecretEnvInClient => NoSecretEnvInClient(list),
                _ => [],
            });
        }

        return findings;
    }

    /// <summary><c>rls_on_every_table</c>: one critical security finding per table created without enabling RLS.</summary>
    public static IReadOnlyList<ReviewFinding> RlsOnEveryTable(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var migrations = files
            .Select(f => f with { Path = PathRules.Normalize(f.Path) })
            .Where(f => f.Path.StartsWith(MigrationsFolder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => (f.Path, Sql: StripSqlComments(f.Content)))
            .ToList();

        var enabled = migrations
            .SelectMany(m => EnableRlsPattern().Matches(m.Sql).Select(x => TableName(x.Groups["name"].Value)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var findings = new List<ReviewFinding>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, sql) in migrations)
        {
            foreach (Match created in CreateTablePattern().Matches(sql))
            {
                var name = TableName(created.Groups["name"].Value);
                if (!enabled.Contains(name) && reported.Add(name))
                {
                    findings.Add(new ReviewFinding(
                        ReviewCategory.Security,
                        Severity.Critical,
                        $"Table '{name}' is created without enabling row level security (add `alter table {name} enable row level security;` and its policies).",
                        path));
                }
            }
        }

        return findings;
    }

    /// <summary><c>no_secret_env_in_client</c>: one critical security finding per secret env name per <c>"use client"</c> file.</summary>
    public static IReadOnlyList<ReviewFinding> NoSecretEnvInClient(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var findings = new List<ReviewFinding>();
        foreach (var file in files.OrderBy(f => PathRules.Normalize(f.Path), StringComparer.Ordinal))
        {
            if (!IsClientFile(file.Content))
            {
                continue;
            }

            var names = EnvPattern().Matches(file.Content)
                .Select(m => m.Groups["dot"].Success ? m.Groups["dot"].Value : m.Groups["index"].Value)
                .Where(IsSecretInClient)
                .Distinct(StringComparer.Ordinal);
            findings.AddRange(names.Select(name => new ReviewFinding(
                ReviewCategory.Security,
                Severity.Critical,
                name == ServiceRoleKey
                    ? $"The Supabase service-role key ({name}) is referenced in a \"use client\" file; it bypasses RLS and must stay server-side."
                    : $"The server-only environment variable {name} is referenced in a \"use client\" file; only {PublicPrefix}* values may reach the browser.",
                PathRules.Normalize(file.Path))));
        }

        return findings;
    }

    /// <summary>Whether a client file may not reference <paramref name="name"/>.</summary>
    public static bool IsSecretInClient(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name == ServiceRoleKey || (!name.StartsWith(PublicPrefix, StringComparison.Ordinal) && name != "NODE_ENV");
    }

    /// <summary>Whether the file's first statement (after whitespace, a BOM and comments) is the <c>"use client"</c> directive.</summary>
    public static bool IsClientFile(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = content.TrimStart('﻿');
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (string.CompareOrdinal(text, i, "//", 0, 2) == 0)
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end + 1;
            }
            else if (string.CompareOrdinal(text, i, "/*", 0, 2) == 0)
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        var rest = text[i..];
        return rest.StartsWith("\"use client\"", StringComparison.Ordinal) || rest.StartsWith("'use client'", StringComparison.Ordinal);
    }

    /// <summary>A table name without quotes and without the default <c>public.</c> schema, lowercased.</summary>
    private static string TableName(string raw)
    {
        var parts = raw.Split('.', StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"').ToLower(CultureInfo.InvariantCulture))
            .ToList();
        return parts.Count == 2 && parts[0] == "public" ? parts[1] : string.Join('.', parts);
    }

    private static string StripSqlComments(string sql) => SqlCommentPattern().Replace(sql, " ");

    private const string Name = @"(?:""[^""]+""|[A-Za-z_][\w$]*)";

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.CultureInvariant | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SqlCommentPattern();

    [GeneratedRegex(@"\bcreate\s+(?:unlogged\s+)?table\s+(?:if\s+not\s+exists\s+)?(?<name>" + Name + @"(?:\s*\.\s*" + Name + @")?)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CreateTablePattern();

    [GeneratedRegex(@"\balter\s+table\s+(?:if\s+exists\s+)?(?:only\s+)?(?<name>" + Name + @"(?:\s*\.\s*" + Name + @")?)\s+enable\s+row\s+level\s+security\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnableRlsPattern();

    [GeneratedRegex(@"\bprocess\s*\.\s*env\s*(?:\.\s*(?<dot>[A-Za-z_][A-Za-z0-9_]*)|\[\s*[""'`](?<index>[A-Za-z_][A-Za-z0-9_]*)[""'`]\s*\])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnvPattern();
}

/// <summary>
/// FR-77: the preset review checklist in the Code Review agent's reply. The agent reports a failed item as a violation whose
/// message starts with the item's marker (<c>[C1]</c>, …, from <see cref="PresetGuidance.Review"/>); such violations take the
/// item's category and severity. They stay agent violations (<see cref="FindingSource.Agent"/>): no penalty (§4.7).
/// </summary>
public static partial class PresetReviewChecklist
{
    /// <summary>The marker of checklist item <paramref name="number"/> (1-based): <c>[C1]</c>.</summary>
    public static string Marker(int number) => $"[C{number.ToString(CultureInfo.InvariantCulture)}]";

    /// <summary>The checklist item a violation message refers to (by its leading marker), or <c>null</c>.</summary>
    public static PresetChecklistItem? ItemOf(string message, Preset preset)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(preset);
        var match = MarkerPattern().Match(message);
        return match.Success && int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n >= 1 && n <= preset.Review.Checklist.Count
                ? preset.Review.Checklist[n - 1]
                : null;
    }

    /// <summary>
    /// The agent's violations with every checklist violation mapped to its item's category and severity; others unchanged.
    /// Without a preset the list is returned as is.
    /// </summary>
    public static IReadOnlyList<ReviewFinding> Apply(IReadOnlyList<ReviewFinding> violations, Preset? preset)
    {
        ArgumentNullException.ThrowIfNull(violations);
        if (preset is null || preset.Review.Checklist.Count == 0)
        {
            return violations;
        }

        return
        [
            .. violations.Select(v => v.Source == FindingSource.Agent && ItemOf(v.Message, preset) is { } item
                ? v with { Category = item.Category, Severity = item.Severity }
                : v),
        ];
    }

    [GeneratedRegex(@"^\s*\[C(?<n>\d{1,3})\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkerPattern();
}
