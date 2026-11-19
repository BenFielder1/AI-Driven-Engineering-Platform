using Tevrin.Engine.Domain.Entities;

namespace Tevrin.Engine.Runtime.Llm;

/// <summary>Picks the provider, model and credential for an agent run (FR-22, FR-79, D-V2-5).</summary>
public interface ILlmResolver
{
    /// <summary>
    /// The project's LLM settings (provider, the agent's model or the default model) when saved, else the system
    /// defaults (<c>ILlmProviderCatalog.FallbackProvider</c>, with its configured models); plus the key.
    /// </summary>
    Task<ResolvedLlm> ResolveAsync(Guid projectId, string agent, CancellationToken cancellationToken);

    /// <summary>
    /// The key for <paramref name="provider"/> (<c>LlmConnectionRules.ChooseKey</c>): the project's bound connection
    /// (FR-79) whoever triggered the run; else the system key, only with <c>Llm:AllowSystemKeys=true</c>; none for
    /// Ollama/Fake or when nothing is usable. A bound key that can't be decrypted is no key (never a silent fallback).
    /// </summary>
    Task<LlmKey> KeyAsync(Guid projectId, LlmProvider provider, CancellationToken cancellationToken);

    /// <summary>The <see cref="ILlmProvider"/> implementation of a provider.</summary>
    ILlmProvider ProviderFor(LlmProvider provider);
}
