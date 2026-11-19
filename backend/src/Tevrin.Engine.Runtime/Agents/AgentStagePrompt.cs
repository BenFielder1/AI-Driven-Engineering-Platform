using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Runtime.Agents;

/// <summary>
/// FR-295: the values an agent stage's prompt template is rendered with. Each input artifact fills the placeholder named
/// after its key (<c>draft</c> → <c>{{DRAFT}}</c>, <c>stack_profile</c> → <c>{{STACK_PROFILE}}</c>), each context section
/// the placeholder named after it, and the feedback <c>{{FEEDBACK}}</c>. An input that has no locked version is absent
/// (D-0.7.0-27): an optional placeholder (<c>{{DRAFT?}}</c>) renders empty, a required one fails with
/// <c>MISSING_INPUT</c>. Feedback for a template without a <c>FEEDBACK</c> placeholder is appended to the user message.
/// </summary>
public static class AgentStagePrompt
{
    public const string FeedbackPlaceholder = AgentPromptPlaceholders.Feedback;

    /// <summary>The heading of feedback appended to a template that has no <c>{{FEEDBACK}}</c>.</summary>
    public const string FeedbackHeading = "Feedback on your previous output (address every point):";

    /// <summary>The placeholder name of an artifact key or context section name (<see cref="AgentPromptPlaceholders.Of"/>).</summary>
    public static string PlaceholderOf(string name) => AgentPromptPlaceholders.Of(name);

    /// <summary>The values for <see cref="PromptTemplate.Render"/>.</summary>
    public static Dictionary<string, string?> Values(
        IEnumerable<Artifact> inputs,
        IEnumerable<AgentContextSection> context,
        string? feedback)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(context);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            values[PlaceholderOf(input.Type)] = input.Content;
        }

        foreach (var section in context)
        {
            values[PlaceholderOf(section.Name)] = section.Content;
        }

        values[FeedbackPlaceholder] = feedback;
        return values;
    }

    /// <summary>The user message: the rendered one, plus the feedback when the template doesn't place it itself.</summary>
    public static string UserMessage(PromptTemplate template, RenderedPrompt rendered, string? feedback)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(rendered);
        if (string.IsNullOrWhiteSpace(feedback) || template.Placeholders.Any(p => p.Name == FeedbackPlaceholder))
        {
            return rendered.User;
        }

        return $"{rendered.User}\n\n{FeedbackHeading}\n{template.Neutralise(feedback.Trim())}";
    }

    /// <summary>
    /// The artifact content of an answer: trimmed; for JSON, a single fenced block (<c>```json … ```</c>) is unwrapped.
    /// </summary>
    public static string Content(ArtifactFormat format, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (format != ArtifactFormat.Json || !trimmed.StartsWith("```", StringComparison.Ordinal) || !trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLine = trimmed.IndexOf('\n', StringComparison.Ordinal);
        return firstLine < 0 ? trimmed : trimmed[(firstLine + 1)..^3].Trim();
    }
}
