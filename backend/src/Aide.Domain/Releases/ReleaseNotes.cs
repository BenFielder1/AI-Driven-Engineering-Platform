using System.Globalization;
using System.Text;

namespace Aide.Domain.Releases;

/// <summary>A plan step as the release notes list it: its number, title and the commit it produced (if any).</summary>
public sealed record ReleaseNoteStep(int Number, string Title, string? CommitSha);

/// <summary>
/// What the GitHub Release notes of a version are made of (FR-64, §4.14): the version, the spec's "Changes in this version"
/// section (<c>null</c> when the spec has none, e.g. a first version), the plan step titles with their commits, the latest
/// review score and cycle, the repository's web URL (for commit links) and the link back to the version in AIDE.
/// </summary>
public sealed record ReleaseNotesInput(
    string ProjectName,
    string Version,
    string Tag,
    string? ChangesInThisVersion,
    IReadOnlyList<ReleaseNoteStep> Steps,
    string? RepoWebUrl,
    decimal? ReviewScore,
    int? ReviewCycle,
    string AideUrl);

/// <summary>FR-64, §4.14: AIDE's own release notes (not GitHub's auto-generated ones). Pure.</summary>
public static class ReleaseNotes
{
    /// <summary>GitHub's limit for a release body is 125,000 characters; AIDE stays well under it.</summary>
    public const int MaxBodyLength = 100_000;

    /// <summary>The GitHub Release name: the version itself, e.g. <c>1.2.0</c>.</summary>
    public static string Name(string version) => version;

    /// <summary>The release notes in Markdown.</summary>
    public static string Build(ReleaseNotesInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var notes = new StringBuilder();
        notes.Append(CultureInfo.InvariantCulture, $"## {OneLine(input.ProjectName)} {input.Version}\n\n");

        notes.Append("### Changes in this version\n\n");
        notes.Append(string.IsNullOrWhiteSpace(input.ChangesInThisVersion)
            ? "_The specification has no \"Changes in this version\" section (first version)._\n\n"
            : input.ChangesInThisVersion.Trim().ReplaceLineEndings("\n") + "\n\n");

        notes.Append("### Plan steps\n\n");
        if (input.Steps.Count == 0)
        {
            notes.Append("_No plan steps were recorded._\n\n");
        }
        else
        {
            foreach (var step in input.Steps.OrderBy(s => s.Number))
            {
                notes.Append(CultureInfo.InvariantCulture, $"{step.Number}. {OneLine(step.Title)}{CommitLink(input.RepoWebUrl, step.CommitSha)}\n");
            }

            notes.Append('\n');
        }

        notes.Append("### Review\n\n");
        notes.Append(input.ReviewScore is { } score
            ? string.Create(CultureInfo.InvariantCulture, $"Compliance score **{score:0.000}**{(input.ReviewCycle is { } cycle ? $" (review cycle {cycle})" : string.Empty)}.\n\n")
            : "_No review score was recorded._\n\n");

        notes.Append(CultureInfo.InvariantCulture, $"---\nReleased as `{input.Tag}` with [AIDE]({input.AideUrl}).\n");
        var text = notes.ToString();
        return text.Length <= MaxBodyLength ? text : text[..(MaxBodyLength - 1)] + "…";
    }

    private static string CommitLink(string? repoWebUrl, string? sha)
    {
        if (string.IsNullOrEmpty(sha) || sha.Length < 7 || !sha.All(char.IsAsciiHexDigit))
        {
            return string.Empty;
        }

        var shortSha = sha[..7];
        return string.IsNullOrEmpty(repoWebUrl)
            ? $" (`{shortSha}`)"
            : $" ([`{shortSha}`]({repoWebUrl.TrimEnd('/')}/commit/{sha}))";
    }

    private static string OneLine(string text) =>
        string.Join(' ', (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
