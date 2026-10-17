using System.Text;
using Aide.Domain.Agents;
using Aide.Domain.Common;

namespace Aide.Domain.Presets;

/// <summary>
/// FR-76, §4.11: the prompt of a plan step run by a specialist agent. Rendering order: the base <c>code_generator.md</c>
/// system prompt (already rendered, with the structured-output contract) → the preset's <c>specConventions</c> → the
/// agent's prompt (with the paths it owns) → each listed skill, in order → the step input (the base template's user
/// message, unchanged). The base contract comes first and the specialist text is added under headings, so the
/// output format and Guardian rules stay the code generator's.
/// </summary>
public static class SpecialistPrompt
{
    /// <summary>
    /// The specialist prompt for <paramref name="agentKey"/>: <paramref name="basePrompt"/>'s system prompt extended with
    /// the preset sections, and its user message (the step input) unchanged. <c>NOT_FOUND</c> for a key outside the catalogue.
    /// </summary>
    public static Result<RenderedPrompt> Compose(RenderedPrompt basePrompt, Preset preset, string agentKey)
    {
        ArgumentNullException.ThrowIfNull(basePrompt);
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Agent(agentKey) is not { } agent)
        {
            return Result.Failure<RenderedPrompt>(new DomainError(ErrorCodes.NotFound, $"The {preset.Id} preset has no agent '{agentKey}'."));
        }

        return Result.Success(new RenderedPrompt(ComposeSystem(basePrompt.System, preset, agent), basePrompt.User));
    }

    /// <summary>The composed system prompt (base → conventions → agent → skills).</summary>
    public static string ComposeSystem(string baseSystem, Preset preset, PresetAgent agent)
    {
        ArgumentNullException.ThrowIfNull(baseSystem);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(agent);
        var sb = new StringBuilder(baseSystem.TrimEnd());
        if (preset.SpecConventions.Length > 0)
        {
            sb.Append("\n\n## Stack conventions (").Append(preset.Name).Append(" preset)\n\n").Append(preset.SpecConventions);
        }

        sb.Append("\n\n## Your role: ").Append(agent.Name).Append(" (").Append(agent.Key).Append(")\n\n")
            .Append("You own these paths: ").Append(string.Join(", ", agent.Owns.Patterns.Select(p => $"`{p}`"))).Append(".\n\n")
            .Append(agent.Prompt);
        foreach (var skill in agent.SkillTexts)
        {
            sb.Append("\n\n## Skill: ").Append(skill.Name).Append("\n\n").Append(skill.Text);
        }

        return sb.ToString();
    }
}
