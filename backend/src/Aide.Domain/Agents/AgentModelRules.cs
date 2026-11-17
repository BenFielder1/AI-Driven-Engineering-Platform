using Tevrin.Engine.Domain.Agents;
using Tevrin.Engine.Domain.Entities;
using Tevrin.Pack.Sdlc;

namespace Aide.Domain.Agents;

/// <summary>FR-22: the model an SDLC agent runs on. Stays with the host: <see cref="ModelKey"/> maps
/// <see cref="SdlcAgents.BuildFix"/> to the <see cref="SdlcAgents.CodeReview"/> settings (D-0.6.1-17).</summary>
public static class AgentModelRules
{
    /// <summary>
    /// FR-22: the model for an agent under the given LLM settings: the agent's own model when set (keyed by
    /// <see cref="ModelKey"/>, the agent's wire name, e.g. <c>idea_refinement</c>), else the default model.
    /// </summary>
    public static LlmChoice ChooseModel(string agent, LlmProvider provider, string defaultModel, IReadOnlyDictionary<string, string>? modelsByAgent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultModel);
        var model = modelsByAgent is not null && modelsByAgent.TryGetValue(ModelKey(agent), out var own) && !string.IsNullOrWhiteSpace(own)
            ? own.Trim()
            : defaultModel.Trim();
        return new LlmChoice(provider, model);
    }

    /// <summary>
    /// The key an agent's model is configured under: its wire name, except <c>build_fix</c>, which uses the
    /// <c>code_review</c> settings (D-0.6.1-17).
    /// </summary>
    public static string ModelKey(string agent) => agent == SdlcAgents.BuildFix ? SdlcAgents.CodeReview : agent;
}
