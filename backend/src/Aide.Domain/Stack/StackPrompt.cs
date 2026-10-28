using System.Text;
using Aide.Domain.Agents;
using Aide.Domain.Common;

namespace Aide.Domain.Stack;

/// <summary>
/// FR-121: the prompt of a plan step run by a specialist (the composition equivalent of v2's <c>SpecialistPrompt</c>).
/// Order: (1) the base <c>code_generator.md</c> system prompt, already rendered with its output contract → (2) the
/// composition summary → (3) the agent's prompt, with the paths it owns → (4) each resolved skill, as
/// <c>## Skill: &lt;pack&gt; — &lt;section&gt;</c> → (5) the step input (the base template's user message, unchanged).
/// The run records <see cref="StackSpecialist.Packs"/> (<c>packs[] {id, version, sections[]}</c>).
/// </summary>
public static class StackPrompt
{
    /// <summary>The heading of a skill block.</summary>
    public static string SkillHeading(string pack, string section) => $"## Skill: {pack} — {section}";

    /// <summary>
    /// The specialist prompt for <paramref name="agentKey"/>: <paramref name="basePrompt"/>'s system prompt extended with the
    /// composition, and its user message (the step input) unchanged. <c>NOT_FOUND</c> for a key that isn't in play.
    /// </summary>
    public static Result<RenderedPrompt> Compose(RenderedPrompt basePrompt, StackResolution resolution, string agentKey)
    {
        ArgumentNullException.ThrowIfNull(basePrompt);
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Specialist(agentKey) is not { } specialist)
        {
            return Result.Failure<RenderedPrompt>(new DomainError(
                ErrorCodes.NotFound,
                $"The composition has no specialist '{agentKey}'; it has {string.Join(", ", resolution.AgentKeys)}."));
        }

        return Result.Success(new RenderedPrompt(ComposeSystem(basePrompt.System, resolution, specialist), basePrompt.User));
    }

    /// <summary>The composed system prompt (base → composition summary → agent → skills).</summary>
    public static string ComposeSystem(string baseSystem, StackResolution resolution, StackSpecialist specialist)
    {
        ArgumentNullException.ThrowIfNull(baseSystem);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(specialist);
        var sb = new StringBuilder(baseSystem.TrimEnd());
        sb.Append("\n\n").Append(resolution.Summary);
        sb.Append("\n\n## Your role: ").Append(specialist.Name).Append(" (").Append(specialist.Key).Append(")\n\n")
            .Append("You own these paths: ").Append(string.Join(", ", specialist.OwnedGlobs.Select(p => $"`{p}`"))).Append('.');
        if (specialist.Prompt.Length > 0)
        {
            sb.Append("\n\n").Append(specialist.Prompt);
        }

        foreach (var skill in specialist.Skills)
        {
            sb.Append("\n\n").Append(SkillHeading(skill.Pack, skill.Section)).Append("\n\n").Append(skill.Text);
        }

        return sb.ToString();
    }
}
