using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Domain.Agents;

/// <summary>FR-22, FR-79: which LLM providers need an API key.</summary>
public static class LlmProviderKeys
{
    /// <summary>Anthropic and OpenAI need a key; Ollama and Fake don't.</summary>
    public static bool Required(LlmProvider provider) => provider is LlmProvider.Anthropic or LlmProvider.Openai;
}
