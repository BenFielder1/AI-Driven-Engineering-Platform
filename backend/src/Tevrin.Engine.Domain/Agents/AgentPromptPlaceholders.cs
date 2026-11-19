using System.Globalization;
using System.Text;
using Tevrin.Engine.Domain.Pipelines;

namespace Tevrin.Engine.Domain.Agents;

/// <summary>
/// FR-295, D-0.7.0-33: how an agent stage's prompt placeholders are filled. Each input artifact fills the placeholder named
/// after its key (<c>draft</c> → <c>{{DRAFT}}</c>), each context section the placeholder named after it, and the runner
/// fills <c>{{FEEDBACK}}</c>, which must be optional (a first run has none).
/// </summary>
public static class AgentPromptPlaceholders
{
    public const string Feedback = "FEEDBACK";

    /// <summary>The placeholder name of an artifact key or context name: upper case, <c>-</c>, <c>.</c> and spaces as <c>_</c>.</summary>
    public static string Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            builder.Append(c is '-' or ' ' or '.' ? '_' : char.ToUpper(c, CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Why <paramref name="templateSource"/> can't serve <paramref name="stage"/>'s agent, or <c>null</c>: it doesn't parse,
    /// <c>{{FEEDBACK}}</c> is required, or a required placeholder is neither one of the stage's inputs nor one of the
    /// pack's <paramref name="contextNames"/>.
    /// </summary>
    public static string? Problem(StageDefinition stage, string templateSource, IReadOnlyCollection<string> contextNames)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(contextNames);
        var agent = stage.Agent ?? throw new ArgumentException("Not an agent stage.", nameof(stage));
        var parsed = PromptTemplate.Parse(agent.Key, templateSource);
        if (!parsed.IsSuccess)
        {
            return parsed.Error!.Message;
        }

        var known = stage.Inputs.Select(Of).Concat(contextNames.Select(Of)).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var name in parsed.Value.RequiredInputs)
        {
            if (name == Feedback)
            {
                problems.Add("{{FEEDBACK}} must be optional ({{FEEDBACK?}})");
            }
            else if (!known.Contains(name))
            {
                problems.Add($"{{{{{name}}}}} is required but is not an input of the stage or a registered context");
            }
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
}
