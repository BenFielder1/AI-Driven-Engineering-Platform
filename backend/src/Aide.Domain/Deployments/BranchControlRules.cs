using System.Text.Json;
using System.Text.RegularExpressions;
using Aide.Domain.Execution;

namespace Aide.Domain.Deployments;

/// <summary>
/// FR-233, FR-234, §4.36: which Tevrin branch prefixes a project's branch control must block, and whether a platform config
/// blocks them. Required = {<c>tevrin/</c>} ∪ {<c>aide/</c> when any recorded version or step branch of the project starts
/// with <c>aide/</c>}. Config Tevrin writes or suggests always blocks both (<see cref="AllPrefixes"/>).
/// </summary>
public static partial class BranchControlRules
{
    /// <summary>Every prefix Tevrin has used, newest first: what config Tevrin writes or suggests blocks (FR-234).</summary>
    public static IReadOnlyList<string> AllPrefixes { get; } = [BranchNaming.Prefix, BranchNaming.LegacyPrefix];

    /// <summary>A project with only <c>tevrin/</c> branches (or none yet).</summary>
    public static IReadOnlyList<string> TevrinOnly { get; } = [BranchNaming.Prefix];

    /// <summary>§4.36: the prefixes branch control must block, given whether the project uses an <c>aide/</c> branch.</summary>
    public static IReadOnlyList<string> RequiredPrefixes(bool usesLegacyBranch) => usesLegacyBranch ? AllPrefixes : TevrinOnly;

    /// <summary>§4.36: the prefixes branch control must block for a project with these recorded version and step branches.</summary>
    public static IReadOnlyList<string> RequiredPrefixes(IEnumerable<string?> projectBranches)
    {
        ArgumentNullException.ThrowIfNull(projectBranches);
        return RequiredPrefixes(projectBranches.Any(BranchNaming.IsLegacy));
    }

    /// <summary>The Vercel <c>git.deploymentEnabled</c> key for a prefix: <c>tevrin/**</c>.</summary>
    public static string VercelPattern(string prefix) => prefix + "**";

    /// <summary>The glob a Netlify <c>ignore</c> case or a Cloudflare exclusion uses for a prefix: <c>tevrin/*</c>.</summary>
    public static string BranchGlob(string prefix) => prefix + "*";

    /// <summary>The <c>git.deploymentEnabled</c> object Tevrin writes or suggests (FR-234): every prefix set to <c>false</c>.</summary>
    public static string VercelSuggestion() =>
        "{ " + string.Join(", ", AllPrefixes.Select(p => $"\"{VercelPattern(p)}\": false")) + " }";

    /// <summary>The Netlify <c>[build] ignore</c> command Tevrin writes or suggests (FR-234): skip every prefix, build the rest.</summary>
    public static string NetlifyIgnoreCommand() =>
        $"case \"$BRANCH\" in {string.Join('|', AllPrefixes.Select(BranchGlob))}) exit 0 ;; *) exit 1 ;; esac";

    /// <summary>The patterns for a message: <c>tevrin/**</c>, or <c>tevrin/** and aide/**</c> (quoted: <c>"tevrin/**"</c>).</summary>
    public static string Names(IReadOnlyList<string> patterns, bool quoted = false)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var items = patterns.Select(p => quoted ? $"\"{p}\"" : p).ToList();
        return items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
    }

    /// <summary>
    /// §4.36 Vercel: the <c>&lt;prefix&gt;**</c> patterns of <paramref name="requiredPrefixes"/> that <paramref name="content"/>
    /// (a <c>vercel.json</c>, comments and trailing commas allowed) doesn't block. A prefix is blocked when
    /// <c>git.deploymentEnabled["&lt;prefix&gt;**"]</c> is <c>false</c> and no other key matching branches under the prefix is
    /// <c>true</c>. Unreadable JSON blocks nothing. Empty: every required prefix is blocked.
    /// </summary>
    public static IReadOnlyList<string> VercelMissing(string content, IReadOnlyList<string> requiredPrefixes)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(requiredPrefixes);
        var rules = DeploymentEnabled(content);
        return [.. requiredPrefixes.Where(p => !VercelBlocks(rules, p)).Select(VercelPattern)];
    }

    /// <summary>
    /// §4.36 Netlify: the <c>&lt;prefix&gt;*</c> globs of <paramref name="requiredPrefixes"/> that no <c>ignore</c> command in
    /// the <c>[build]</c> section of <paramref name="toml"/> names. A missing file or no <c>[build] ignore</c> blocks nothing.
    /// </summary>
    public static IReadOnlyList<string> NetlifyMissing(string? toml, IReadOnlyList<string> requiredPrefixes)
    {
        ArgumentNullException.ThrowIfNull(requiredPrefixes);
        var commands = NetlifyIgnoreCommands(toml);
        return [.. requiredPrefixes.Where(p => !commands.Any(c => c.Contains(p, StringComparison.Ordinal))).Select(BranchGlob)];
    }

    private static List<string> NetlifyIgnoreCommands(string? toml)
    {
        var commands = new List<string>();
        if (string.IsNullOrEmpty(toml))
        {
            return commands;
        }

        var inBuild = false;
        foreach (var raw in toml.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inBuild = line.Replace(" ", string.Empty, StringComparison.Ordinal) == "[build]";
                continue;
            }

            if (!inBuild || line.StartsWith('#') || !line.StartsWith("ignore", StringComparison.Ordinal))
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && line[..eq].Trim() == "ignore")
            {
                commands.Add(line[(eq + 1)..]);
            }
        }

        return commands;
    }

    private static bool VercelBlocks(IReadOnlyList<(string Key, JsonValueKind Value)>? rules, string prefix)
    {
        if (rules is null)
        {
            return false;
        }

        var pattern = VercelPattern(prefix);
        return rules.Any(r => r.Key == pattern && r.Value == JsonValueKind.False)
            && !rules.Any(r => r.Key != pattern && r.Value == JsonValueKind.True && MatchesUnder(r.Key, prefix));
    }

    /// <summary>
    /// Whether a <c>git.deploymentEnabled</c> key could match a branch under <paramref name="prefix"/>: it starts with the
    /// prefix (e.g. <c>tevrin/shop*</c>), or as a glob (<c>*</c> within a segment, <c>**</c> across segments, <c>?</c> one
    /// character) it matches a branch one or two segments under the prefix (e.g. <c>**</c>, <c>tev*/**</c>).
    /// </summary>
    private static bool MatchesUnder(string key, string prefix)
    {
        if (key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return true;
        }

        if (key.IndexOfAny(['*', '?']) < 0)
        {
            return false;
        }

        var regex = new Regex(
            "\\A" + GlobToken().Replace(key, m => m.Value switch
            {
                "**" => ".*",
                "*" => "[^/]*",
                "?" => "[^/]",
                _ => Regex.Escape(m.Value),
            }) + "\\z",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        return regex.IsMatch(prefix + "x") || regex.IsMatch(prefix + "x/y");
    }

    private static List<(string Key, JsonValueKind Value)>? DeploymentEnabled(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("git", out var git) && git.ValueKind == JsonValueKind.Object
                && git.TryGetProperty("deploymentEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.Object)
            {
                return [.. enabled.EnumerateObject().Select(p => (p.Name, p.Value.ValueKind))];
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\*\*|\*|\?|[^*?]+", RegexOptions.CultureInvariant)]
    private static partial Regex GlobToken();
}
