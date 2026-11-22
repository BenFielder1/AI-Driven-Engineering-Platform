namespace Tevrin.Engine.Domain.Pipelines;

/// <summary>
/// D-0.7.0-42: one <c>agents:</c> entry, <c>{ key, job_type, prompt?, inputs?, output?, tools? }</c>: an agent with no
/// stage of its own, bound to an existing stage. <see cref="Inputs"/> default to the stage's inputs (the loader fills
/// them in, so writing them out doesn't change the hash); <see cref="Output"/> <c>null</c> means the run stores no
/// artifact. <see cref="Prompt"/> <c>null</c> means the pack's prompt for <see cref="Key"/>, as for <c>agent.prompt</c>.
/// D-0.7.0-44: <see cref="Validate"/> and <see cref="OnInvalid"/> are the binding's own <c>validate:</c> and
/// <c>on_invalid:</c> (its output is checked like a stage's; none by default).
/// </summary>
public sealed record AgentBindingDefinition(
    string Key,
    string JobType,
    string? Prompt,
    IReadOnlyList<string> Inputs,
    string? Output,
    IReadOnlyList<string> Tools)
{
    /// <summary>D-0.7.0-44: the binding's <c>validate:</c> guards (none by default).</summary>
    public IReadOnlyList<StageValidation> Validate { get; init; } = [];

    /// <summary>D-0.7.0-44: the binding's <c>on_invalid:</c>, or <c>null</c>.</summary>
    public OnInvalidPolicy? OnInvalid { get; init; }
}

/// <summary>D-0.7.0-42: resolving the agent a request names on a stage.</summary>
public static class AgentBindings
{
    /// <summary>
    /// The stage as agent <paramref name="agentKey"/> sees it: <c>null</c> or the primary agent's key → the stage itself;
    /// a secondary binding's key → a view of the stage whose <see cref="StageDefinition.Agent"/>, inputs and output are the
    /// binding's, with the binding's own <c>validate:</c> and <c>on_invalid</c> (the stage's belong to its own output,
    /// D-0.7.0-44) and no secondary bindings. <c>null</c> when the stage binds no such agent (or, for a <c>null</c> key, has no primary agent).
    /// The view is for running the agent only; stages, transitions and gates always use the definition's stage.
    /// </summary>
    public static StageDefinition? ForAgent(this StageDefinition stage, string? agentKey)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (agentKey is null || (stage.Agent is { } primary && string.Equals(primary.Key, agentKey, StringComparison.Ordinal)))
        {
            return stage.Agent is null ? null : stage;
        }

        var binding = stage.Agents.FirstOrDefault(a => string.Equals(a.Key, agentKey, StringComparison.Ordinal));
        if (binding is null)
        {
            return null;
        }

        return stage with
        {
            Agent = new AgentStageDefinition(binding.Key, binding.Prompt, Model: null, binding.Tools, binding.JobType),
            Handler = null,
            Handlers = [],
            Inputs = binding.Inputs,
            Output = binding.Output,
            Validate = binding.Validate,
            OnInvalid = binding.OnInvalid,
            Agents = [],
        };
    }
}
