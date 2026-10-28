using System.Text.RegularExpressions;
using Aide.Domain.Stack;

namespace Aide.Infrastructure.Agents;

/// <summary>
/// FR-120–FR-127 (D13, §8): what the Fake responders read from a composed prompt. The stack blocks are rendered by
/// <see cref="StackGuidance"/> (spec, plan, review) and <see cref="StackPrompt"/> (code generator) into the system prompt,
/// so a Fake reply can follow the composition offline.
/// </summary>
internal static partial class FakePrompt
{
    /// <summary>The extra spec section titles of the stack block (<see cref="StackGuidance.SpecSectionsHeading"/>).</summary>
    public static IReadOnlyList<string> StackSpecSections(LlmRequest request)
    {
        var lines = request.System.ReplaceLineEndings("\n").Split('\n');
        var titles = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                inside = line.Trim() == StackGuidance.SpecSectionsHeading;
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inside = false;
                continue;
            }

            if (inside && line.StartsWith("#### ", StringComparison.Ordinal))
            {
                titles.Add(line[5..].Trim());
            }
        }

        return titles;
    }

    /// <summary>FR-121: the specialist key of a code generator prompt (<c>## Your role: Name (key)</c>), or <c>null</c>.</summary>
    public static string? SpecialistKey(LlmRequest request) =>
        RolePattern().Match(request.System) is { Success: true } m ? m.Groups["key"].Value : null;

    /// <summary>Whether the plan guidance says the repository already has code (no scaffold-first step).</summary>
    public static bool RepoHasCode(LlmRequest request) =>
        request.System.Contains("This repository already has code", StringComparison.Ordinal);

    [GeneratedRegex(@"^## Your role: .+ \((?<key>[a-z0-9]+(?:-[a-z0-9]+)*)\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RolePattern();
}
