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

    /// <summary>FR-356, §4.69: a conversation participant's prompt gets the turns so far (rendered, one heading per turn).</summary>
    public const string Transcript = "TRANSCRIPT";

    /// <summary>FR-356, §4.69: a conversation participant's prompt gets its own role name.</summary>
    public const string Role = "ROLE";

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
    /// pack's <paramref name="contextNames"/> (nor, on an aggregate stage, an input's <c>_ALL</c> list, FR-346).
    /// <paramref name="supplied"/>: placeholders the engine fills for this agent (FR-350: a validator agent's
    /// <c>{{OUTPUT}}</c>).
    /// </summary>
    public static string? Problem(
        StageDefinition stage, string templateSource, IReadOnlyCollection<string> contextNames, IReadOnlyCollection<string>? supplied = null)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(contextNames);
        var agent = stage.Agent ?? throw new ArgumentException("Not an agent stage.", nameof(stage));
        var parsed = PromptTemplate.Parse(agent.Key, templateSource);
        if (!parsed.IsSuccess)
        {
            return parsed.Error!.Message;
        }

        var known = stage.Inputs.Select(Of).Concat(contextNames.Select(Of)).Concat(supplied ?? []).ToHashSet(StringComparer.Ordinal);
        if (stage.Aggregate is not null)
        {
            // FR-346: an aggregate stage also gets {{<KEY>_ALL}}, every branch output of an input key.
            known.UnionWith(stage.Inputs.Select(Pipelines.FanOutRules.AllPlaceholder));
        }

        if (stage.Variants is not null || stage.FanOut is not null)
        {
            // FR-349: a branch's prompt gets its own label as {{VARIANT}}.
            known.Add(Pipelines.VariantRules.LabelPlaceholder);
        }

        if (stage.Feedback is { Inject: true })
        {
            // FR-351, §4.75: an injecting stage's prompt gets the agent's feedback entries as {{AGENT_FEEDBACK}}.
            known.Add(Pipelines.FeedbackInjectionRules.Placeholder);
        }

        var problems = new List<string>();
        foreach (var name in parsed.Value.RequiredInputs)
        {
            if (name == Feedback)
            {
                problems.Add("{{FEEDBACK}} must be optional ({{FEEDBACK?}})");
            }
            else if (name == Pipelines.FeedbackInjectionRules.Placeholder)
            {
                // FR-351: like {{FEEDBACK}}, a first run has no entries.
                problems.Add("{{AGENT_FEEDBACK}} must be optional ({{AGENT_FEEDBACK?}})");
            }
            else if (!known.Contains(name))
            {
                problems.Add($"{{{{{name}}}}} is required but is not an input of the stage or a registered context");
            }
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
}
