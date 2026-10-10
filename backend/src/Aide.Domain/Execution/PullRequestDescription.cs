using System.Globalization;
using System.Text;
using Aide.Domain.Artifacts;
using Aide.Domain.Common;

namespace Aide.Domain.Execution;

/// <summary>A completed step as it appears in the project PR description (FR-36).</summary>
public sealed record PullRequestStep(int Number, string Title, string? CommitSha, IReadOnlyList<string> Files);

/// <summary>
/// What the project PR description is generated from (FR-36): the project, the current technical specification, the plan
/// version that was executed and its steps.
/// </summary>
public sealed record PullRequestInput(
    Guid ProjectId,
    string ProjectName,
    int ProjectVersion,
    string? Specification,
    int? SpecificationVersion,
    ArtifactType PlanType,
    int PlanVersion,
    IReadOnlyList<PullRequestStep> Steps);

/// <summary>
/// FR-36: the title and Markdown description of the one project PR, generated deterministically (no agent call) from the
/// spec, the plan and the step list, after the old stack's project PR builder. Agent-written text (spec summary, step
/// titles) is neutralised so it can't mention users, inject HTML or break the table.
/// </summary>
public static class PullRequestDescription
{
    /// <summary>GitHub's PR title limit is 256 characters.</summary>
    public const int MaxTitleLength = 256;

    /// <summary>GitHub's PR body limit is 65,536 characters; stay well under it.</summary>
    public const int MaxBodyLength = 60_000;

    /// <summary>The spec summary quoted in the description is cut to this length.</summary>
    public const int MaxSummaryLength = 600;

    /// <summary>At most this many changed files are listed.</summary>
    public const int MaxListedFiles = 100;

    /// <summary>At most this many step rows are listed.</summary>
    public const int MaxListedSteps = 200;

    private const int MaxStepTitleLength = 120;
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;
    private const char ZeroWidthJoiner = (char)0x200D;

    /// <summary><c>[AIDE] &lt;project&gt; — Implementation (n steps)</c> (old stack's project PR title).</summary>
    public static string Title(string projectName, int stepCount)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        var suffix = $" — Implementation ({stepCount.ToString(CultureInfo.InvariantCulture)} {(stepCount == 1 ? "step" : "steps")})";
        var name = OneLine(projectName);
        var room = MaxTitleLength - "[AIDE] ".Length - suffix.Length;
        if (name.Length > room)
        {
            name = name[..Math.Max(0, room - 1)] + "…";
        }

        return $"[AIDE] {name}{suffix}";
    }

    /// <summary>The Markdown body: header, spec summary, plan and step table, changed files, metadata, footer.</summary>
    public static string Body(PullRequestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var steps = input.Steps.OrderBy(s => s.Number).ToList();
        var count = steps.Count.ToString(CultureInfo.InvariantCulture);
        var b = new StringBuilder();

        b.Append("## AIDE — ").Append(Inline(input.ProjectName)).AppendLine(" (implementation complete)").AppendLine();
        b.Append("> This pull request was opened by the **AIDE Platform** after all ").Append(count)
            .AppendLine(" steps of the plan completed. Each step is one commit on this branch.").AppendLine();

        b.AppendLine("### Specification").AppendLine();
        var summary = Summary(input.Specification);
        if (summary is null)
        {
            b.AppendLine("_No technical specification summary available._");
        }
        else
        {
            if (input.SpecificationVersion is { } specVersion)
            {
                b.Append("Technical specification v").Append(specVersion.ToString(CultureInfo.InvariantCulture)).AppendLine(":").AppendLine();
            }

            b.Append("> ").AppendLine(summary);
        }

        b.AppendLine();
        b.Append("### Plan: ").Append(input.PlanType.ToWire()).Append(" v").Append(input.PlanVersion.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(count).AppendLine(" steps)").AppendLine();
        b.AppendLine("| # | Step | Commit | Files |");
        b.AppendLine("|---|------|--------|-------|");
        foreach (var step in steps.Take(MaxListedSteps))
        {
            var sha = string.IsNullOrEmpty(step.CommitSha) ? "—" : $"`{Inline(step.CommitSha[..Math.Min(8, step.CommitSha.Length)])}`";
            b.Append("| ").Append(step.Number.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(Inline(Truncate(OneLine(step.Title), MaxStepTitleLength)))
                .Append(" | ").Append(sha)
                .Append(" | ").Append(step.Files.Count.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" |");
        }

        if (steps.Count > MaxListedSteps)
        {
            b.Append("| … | ").Append((steps.Count - MaxListedSteps).ToString(CultureInfo.InvariantCulture)).AppendLine(" more steps | | |");
        }

        b.AppendLine();
        var files = steps.SelectMany(s => s.Files).Select(PathRules.Normalize).Where(p => !PathRules.IsUnsafe(p))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        b.Append("### Files changed (").Append(files.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(")").AppendLine();
        if (files.Count == 0)
        {
            b.AppendLine("_No files changed._");
        }
        else
        {
            var listed = files.Take(MaxListedFiles).ToList();
            var fence = Fence(listed);
            b.Append(fence).AppendLine("text");
            foreach (var file in listed)
            {
                b.AppendLine(file);
            }

            if (files.Count > MaxListedFiles)
            {
                b.Append("... and ").Append((files.Count - MaxListedFiles).ToString(CultureInfo.InvariantCulture)).AppendLine(" more");
            }

            b.AppendLine(fence);
        }

        b.AppendLine();
        b.AppendLine("### AIDE metadata").AppendLine();
        b.AppendLine("| Key | Value |");
        b.AppendLine("|-----|-------|");
        b.AppendLine("| Phase | `execution` |");
        b.Append("| Project ID | `").Append(input.ProjectId.ToString("D")).AppendLine("` |");
        b.Append("| Project version | ").Append(input.ProjectVersion.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        b.Append("| Plan | ").Append(input.PlanType.ToWire()).Append(" v").Append(input.PlanVersion.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        b.Append("| Steps | ").Append(count).AppendLine(" |");
        b.AppendLine();
        b.AppendLine("---");
        b.AppendLine("_One branch per project; each step is a separate commit. AIDE merges this PR when the release is approved._");

        var body = b.ToString().ReplaceLineEndings("\n");
        return body.Length <= MaxBodyLength ? body : body[..(MaxBodyLength - 20)] + "\n\n_(truncated)_\n";
    }

    /// <summary>
    /// The spec summary: the first paragraph under a "Summary" or "Overview" heading, else the first paragraph that isn't a
    /// heading, list, table, fence or quote; neutralised and cut to <see cref="MaxSummaryLength"/>. <c>null</c> if none.
    /// </summary>
    public static string? Summary(string? specification)
    {
        if (string.IsNullOrWhiteSpace(specification))
        {
            return null;
        }

        var lines = specification.ReplaceLineEndings("\n").Split('\n');
        var paragraph = FirstParagraph(lines, preferSummary: true) ?? FirstParagraph(lines, preferSummary: false);
        return paragraph is null ? null : Inline(Truncate(paragraph, MaxSummaryLength));
    }

    private static string? FirstParagraph(string[] lines, bool preferSummary)
    {
        var start = 0;
        if (preferSummary)
        {
            var heading = Array.FindIndex(lines, l => l.TrimStart().StartsWith('#')
                && l.TrimStart().TrimStart('#').Trim() is var h
                && (h.Equals("Summary", StringComparison.OrdinalIgnoreCase) || h.Equals("Overview", StringComparison.OrdinalIgnoreCase)));
            if (heading < 0)
            {
                return null;
            }

            start = heading + 1;
        }

        var collected = new List<string>();
        var inFence = false;
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                if (collected.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#'))
            {
                if (collected.Count > 0 || (preferSummary && line.StartsWith('#')))
                {
                    break;
                }

                continue;
            }

            var numbered = char.IsDigit(line[0]) && line.IndexOf(". ", StringComparison.Ordinal) is >= 1 and <= 4;
            if (line[0] is '|' or '>' or '-' or '*' or '+' || numbered)
            {
                if (collected.Count > 0)
                {
                    break;
                }

                continue;
            }

            collected.Add(line);
        }

        return collected.Count == 0 ? null : string.Join(' ', collected);
    }

    /// <summary>
    /// A backtick code fence longer than any backtick run in <paramref name="lines"/> (at least three), so no listed path
    /// can close the block early and turn the rest into live Markdown.
    /// </summary>
    public static string Fence(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var longest = 0;
        foreach (var line in lines)
        {
            var run = 0;
            foreach (var c in line)
            {
                run = c == '`' ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
        }

        return new string('`', Math.Max(3, longest + 1));
    }

    /// <summary>
    /// Text safe inside one Markdown table cell or quote line: one line, no control characters, no HTML, no @mentions,
    /// no issue references (<c>#12</c>, <c>owner/repo#12</c>, <c>GH-12</c> get a zero-width joiner, so neither GitHub's
    /// autolinks nor closing keywords like "Fixes #12" apply) and no table/inline-code breakers.
    /// </summary>
    public static string Inline(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = OneLine(text);
        var b = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';
            if (c == '#' && char.IsAsciiDigit(next))
            {
                b.Append('#').Append(ZeroWidthJoiner);
                continue;
            }

            if (c == '-' && char.IsAsciiDigit(next) && i >= 2 && line.AsSpan(i - 2, 2).Equals("GH", StringComparison.OrdinalIgnoreCase))
            {
                b.Append('-').Append(ZeroWidthJoiner);
                continue;
            }

            switch (c)
            {
                case '@':
                    b.Append("&#64;");
                    break;
                case '<':
                    b.Append("&lt;");
                    break;
                case '>':
                    b.Append("&gt;");
                    break;
                case '|':
                    b.Append("\\|");
                    break;
                case '`':
                    b.Append('\'');
                    break;
                default:
                    b.Append(c);
                    break;
            }
        }

        return b.ToString();
    }

    private static string OneLine(string text)
    {
        var b = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            b.Append(char.IsControl(c) || c is LineSeparator or ParagraphSeparator ? ' ' : c);
        }

        return string.Join(' ', b.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
